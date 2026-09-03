namespace TmTimeTracker.Logic;

/// <summary>
/// The single answer to "is Claude working in this repo", shared by the aggregator that bills
/// minutes and the dashboard badge that displays them. They used to decide separately, which is
/// how the badge came to read "Claude idle" while a session was mid-tool-call.
/// </summary>
public static class ClaudeRepoActivity
{
    /// <param name="busyRepos">
    /// From <see cref="ClaudeSessionActivity.BusyRepos"/>. Covers the long tail of a tool call,
    /// when nothing is being written to the transcript at all.
    /// </param>
    /// <param name="freshAfterUtc">
    /// A transcript write later than this counts. The caller decides what "recent" means: the
    /// aggregator passes its last tick so no write is counted twice, the badge passes a rolling
    /// window because it repaints on its own timer.
    /// </param>
    public static bool IsActive(
        string repoPath,
        IReadOnlyDictionary<string, DateTime> transcriptMtimes,
        IReadOnlySet<string> busyRepos,
        DateTime freshAfterUtc) =>
        busyRepos.Contains(repoPath)
        || (transcriptMtimes.TryGetValue(ClaudeProjectSlug.FromPath(repoPath), out var mtime)
            && mtime > freshAfterUtc);
}
