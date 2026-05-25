namespace TmTimeTracker.Platform;

public interface IGitBranchProbe
{
    string? GetCurrentBranch(string repoPath);
}
