using TmTimeTracker.Data;

namespace TmTimeTracker.Jira;

public interface IOAuthAppConfigSource
{
    /// <summary>Returns the current OAuth app config; throws if setup is not yet complete.</summary>
    OAuthAppConfig Get();

    bool IsConfigured { get; }
}

public sealed class RepositoryOAuthAppConfigSource : IOAuthAppConfigSource
{
    private readonly OAuthAppConfigRepository _repo;
    public RepositoryOAuthAppConfigSource(OAuthAppConfigRepository repo) => _repo = repo;

    public bool IsConfigured => _repo.Load() is not null;

    public OAuthAppConfig Get() =>
        _repo.Load() ?? throw new InvalidOperationException(
            "OAuth app credentials not configured. Open Settings to add Client ID and Secret.");
}
