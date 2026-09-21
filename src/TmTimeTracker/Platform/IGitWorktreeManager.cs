namespace TmTimeTracker.Platform;

public sealed class GitWorktreeException : Exception
{
    public GitWorktreeException(string message) : base(message) { }
}

public interface IGitWorktreeManager
{
    /// <param name="branchPattern">
    /// Branch to estimate against, or null for the remote's default. A value containing '*'
    /// resolves to the most recently committed matching remote branch, which is what keeps a
    /// rolling convention like dev-01-09-2026 working without anyone updating a setting.
    /// </param>
    Task<string> PrepareAsync(string repoPath, string? branchPattern, CancellationToken ct);
    void Remove(string repoPath);

    /// <summary>
    /// Recent commit subjects, used as the prompt's reference class. The estimation run is
    /// --restricted and so cannot run git for itself.
    /// </summary>
    Task<IReadOnlyList<string>> RecentCommitSubjectsAsync(string repoPath, int count, CancellationToken ct);
}
