namespace TmTimeTracker.Platform;

public sealed class GitWorktreeException : Exception
{
    public GitWorktreeException(string message) : base(message) { }
}

public interface IGitWorktreeManager
{
    Task<string> PrepareAsync(string repoPath, CancellationToken ct);
    void Remove(string repoPath);

    /// <summary>
    /// Recent commit subjects, used as the prompt's reference class. The estimation run is
    /// --restricted and so cannot run git for itself.
    /// </summary>
    Task<IReadOnlyList<string>> RecentCommitSubjectsAsync(string repoPath, int count, CancellationToken ct);
}
