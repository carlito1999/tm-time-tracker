using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using TmTimeTracker.Platform;

namespace TmTimeTracker.Services;

/// <summary>One repo that earned a minute this tick, with the ticket to bill it to.</summary>
public sealed record RepoActivity(string RepoPath, string? Branch, string? TicketKey);

public interface IRepoActivitySource
{
    /// <param name="lastTickUtc">Claude writes count as fresh if they are newer than this.</param>
    /// <param name="humanActive">False when idle or locked. Suppresses the human stream only.</param>
    IReadOnlyList<RepoActivity> Sample(DateTime lastTickUtc, bool humanActive);
}

/// <summary>
/// The credit set for one minute: the repo the user is working in, plus every repo Claude Code
/// is working in. Two independent streams, so hand-editing one project while an agent works
/// another pays both and neither can take the other's minutes.
///
/// Claude streams deliberately ignore idle state and are never capped - crediting unattended
/// agent work is the whole reason the second stream exists.
///
/// "Claude is working here" comes from two signals, because neither is sufficient alone. A
/// transcript write since the last tick catches ordinary back-and-forth. A session latched to
/// "busy" catches the rest of a long tool call, when Claude Code writes nothing at all and the
/// transcript looks abandoned - a ten-minute test run used to bill zero minutes because of it.
/// </summary>
public sealed class RepoActivityMonitor : IRepoActivitySource
{
    private readonly TrackedRepoRepository _repos;
    private readonly ActiveRepoResolver _resolver;
    private readonly IClaudeCodeActivityProbe _claude;
    private readonly IClaudeSessionProbe _sessions;
    private readonly IProcessLiveness _liveness;
    private readonly IGitBranchProbe _git;
    private readonly ILogger<RepoActivityMonitor> _log;
    private readonly HashSet<string> _warnedOnce = new(StringComparer.OrdinalIgnoreCase);

    public RepoActivityMonitor(TrackedRepoRepository repos, ActiveRepoResolver resolver,
        IClaudeCodeActivityProbe claude, IClaudeSessionProbe sessions, IProcessLiveness liveness,
        IGitBranchProbe git, ILogger<RepoActivityMonitor> log)
    {
        _repos = repos; _resolver = resolver; _claude = claude; _sessions = sessions;
        _liveness = liveness; _git = git; _log = log;
    }

    public IReadOnlyList<RepoActivity> Sample(DateTime lastTickUtc, bool humanActive)
    {
        var byPath = new Dictionary<string, RepoActivity>(StringComparer.OrdinalIgnoreCase);

        // Human stream: at most one repo, and only while the user is actually at the desk.
        if (humanActive)
        {
            var human = _resolver.Resolve();
            if (human is not null && Directory.Exists(human.RepoPath))
                byPath[human.RepoPath] = new RepoActivity(human.RepoPath, human.Branch, human.TicketKey);
        }

        // Claude streams: any number of repos, regardless of idle. The resolver already looked up
        // a branch for the human's repo, so only repos it did not cover cost a git spawn.
        var tracked = _repos.GetAll();
        var writes = SafeSnapshot();
        var busy = SafeBusyRepos(tracked.Select(r => r.Path));

        foreach (var repo in tracked)
        {
            if (byPath.ContainsKey(repo.Path)) continue;
            if (!ClaudeRepoActivity.IsActive(repo.Path, writes, busy, lastTickUtc)) continue;
            if (!Directory.Exists(repo.Path)) { WarnOnce(repo.Path); continue; }

            var branch = _git.GetCurrentBranch(repo.Path);
            var ticket = branch is null ? null : TicketKeyExtractor.Extract(branch);
            byPath[repo.Path] = new RepoActivity(repo.Path, branch, ticket);
        }

        return byPath.Values.ToList();
    }

    private IReadOnlyDictionary<string, DateTime> SafeSnapshot()
    {
        try
        {
            return _claude.Snapshot();
        }
        catch (Exception ex)
        {
            if (_warnedOnce.Add("<claude-probe>"))
                _log.LogWarning(ex, "Claude activity probe failed; Claude streams idle this tick");
            return new Dictionary<string, DateTime>();
        }
    }

    /// <summary>
    /// Degrades to an empty set, which leaves the transcript-write rule to decide on its own -
    /// the behaviour this repo had before session status was read at all.
    /// </summary>
    private IReadOnlySet<string> SafeBusyRepos(IEnumerable<string> repoPaths)
    {
        try
        {
            return ClaudeSessionActivity.BusyRepos(_sessions.Snapshot(), repoPaths,
                                                   _liveness.IsRunning);
        }
        catch (Exception ex)
        {
            if (_warnedOnce.Add("<claude-sessions>"))
                _log.LogWarning(ex, "Claude session probe failed; falling back to transcript writes");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void WarnOnce(string path)
    {
        if (_warnedOnce.Add(path))
            _log.LogWarning("Tracked repo path is not accessible: {Path}", path);
    }
}
