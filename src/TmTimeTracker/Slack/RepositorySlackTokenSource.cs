using TmTimeTracker.Data;

namespace TmTimeTracker.Slack;

/// <summary>
/// Reads the Slack token from the database on every call, so saving a new token in Settings takes
/// effect immediately without restarting the host. Mirrors RepositoryOAuthAppConfigSource.
/// </summary>
public sealed class RepositorySlackTokenSource : ISlackTokenSource
{
    private readonly SlackCredentialRepository _repo;

    public RepositorySlackTokenSource(SlackCredentialRepository repo) => _repo = repo;

    public string? GetToken() => _repo.Get();
}
