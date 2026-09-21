namespace TmTimeTracker.Platform;

/// <summary>
/// Reads a clone's origin remote. Kept apart from <see cref="IGitBranchProbe"/> because the two
/// answer different questions and are wanted in different places - the branch every tick, the
/// remote only when an announcement needs to know which Bitbucket repository to ask.
/// </summary>
public interface IGitRemoteProbe
{
    string? GetRemoteUrl(string repoPath);
}
