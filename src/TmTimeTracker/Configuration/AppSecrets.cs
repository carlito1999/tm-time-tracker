namespace TmTimeTracker.Configuration;

public sealed class AppSecrets
{
    public AtlassianSecrets Atlassian { get; init; } = new();
}

public sealed class AtlassianSecrets
{
    public string OAuthClientId { get; init; } = "";
    public string OAuthClientSecret { get; init; } = "";
    public string RedirectUri { get; init; } = "http://localhost:53682/callback";
}
