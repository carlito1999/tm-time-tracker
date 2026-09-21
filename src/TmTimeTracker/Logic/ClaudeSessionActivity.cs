namespace TmTimeTracker.Logic;

/// <summary>
/// One entry from ~/.claude/sessions/&lt;pid&gt;.json, reduced to the four fields that decide
/// whether a repo is earning time. Everything else in that file is Claude Code's own
/// bookkeeping. <see cref="Status"/> is null on builds that predate the field and on session
/// files that have not been updated since they were created.
/// </summary>
public sealed record ClaudeSession(int Pid, string? Cwd, string? Status, long? ProcStartFileTime);

/// <summary>
/// Which tracked repos Claude Code is working in *right now*.
///
/// The transcript under ~/.claude/projects is only appended to when a message completes, so its
/// mtime freezes for the entire length of a tool call - ten minutes into a test run, an active
/// session is indistinguishable from an abandoned one. Claude Code separately latches
/// "status": "busy" in its session file for the duration of a turn and flips it to "idle" when
/// the turn ends, and that latch is what this class reads.
///
/// The window is deliberately uncapped: a repo keeps earning until Claude says it stopped. That
/// is only safe because a session killed mid-tool-call never writes a stop record, so it is
/// liveness of the owning process, not a timeout, that ends an abandoned run.
/// </summary>
public static class ClaudeSessionActivity
{
    private const string BusyStatus = "busy";

    /// <param name="repoPaths">Tracked repos. Results come back in this spelling.</param>
    /// <param name="isAlive">
    /// Asked only about sessions that would otherwise count. Resolving a process is the
    /// expensive part of a tick that runs every minute, and stale session files outnumber live
    /// ones by an order of magnitude.
    /// </param>
    public static IReadOnlySet<string> BusyRepos(
        IEnumerable<ClaudeSession> sessions,
        IEnumerable<string> repoPaths,
        Func<ClaudeSession, bool> isAlive)
    {
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var byNormalised = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in repoPaths)
        {
            var key = Normalise(repo);
            if (key is not null) byNormalised[key] = repo;
        }
        if (byNormalised.Count == 0) return active;

        foreach (var session in sessions)
        {
            if (!string.Equals(session.Status, BusyStatus, StringComparison.OrdinalIgnoreCase))
                continue;

            var cwd = Normalise(session.Cwd);
            if (cwd is null || !byNormalised.TryGetValue(cwd, out var repo)) continue;
            if (active.Contains(repo)) continue;

            if (isAlive(session)) active.Add(repo);
        }
        return active;
    }

    /// <summary>
    /// Exact-match normalisation - casing and a trailing separator are noise, anything else is
    /// a different directory. Prefix matching is deliberately avoided: it would treat a worktree
    /// under the repo as the repo itself, billing the main checkout's branch (the wrong ticket)
    /// and putting two tickets on one repo path, which the aggregator's model forbids.
    /// </summary>
    private static string? Normalise(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                     or PathTooLongException or IOException
                                     or System.Security.SecurityException)
        {
            return null;
        }
    }
}
