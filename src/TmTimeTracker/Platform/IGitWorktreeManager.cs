namespace TmTimeTracker.Platform;

public sealed class GitWorktreeException : Exception
{
    public GitWorktreeException(string message) : base(message) { }
}

public interface IGitWorktreeManager
{
    Task<string> PrepareAsync(string repoPath, CancellationToken ct);
    void Remove(string repoPath);
}
