using System.Net.Http.Json;
using System.Text;
using TmTimeTracker.Configuration;

namespace TmTimeTracker.Jira;

public sealed class JiraOAuthClient
{
    private const string DefaultOAuthBase = "https://auth.atlassian.com";
    private const string AuthorizePath = "/authorize";
    private const string TokenPath = "/oauth/token";

    private readonly HttpClient _http;
    private readonly AtlassianSecrets _secrets;
    private readonly string _oauthBase;

    public JiraOAuthClient(HttpClient http, AtlassianSecrets secrets, string? oauthBaseOverride = null)
    {
        _http = http; _secrets = secrets;
        _oauthBase = (oauthBaseOverride ?? DefaultOAuthBase).TrimEnd('/');
    }

    public string BuildAuthorizationUrl(string state)
    {
        var qs = new StringBuilder()
            .Append("audience=api.atlassian.com")
            .Append("&client_id=").Append(Uri.EscapeDataString(_secrets.OAuthClientId))
            .Append("&scope=").Append(Uri.EscapeDataString("read:jira-work write:jira-work offline_access"))
            .Append("&redirect_uri=").Append(Uri.EscapeDataString(_secrets.RedirectUri))
            .Append("&state=").Append(Uri.EscapeDataString(state))
            .Append("&response_type=code&prompt=consent");
        return $"{_oauthBase}{AuthorizePath}?{qs}";
    }

    public async Task<TokenResponse> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        var payload = new
        {
            grant_type = "authorization_code",
            client_id = _secrets.OAuthClientId,
            client_secret = _secrets.OAuthClientSecret,
            code,
            redirect_uri = _secrets.RedirectUri
        };
        var resp = await _http.PostAsJsonAsync($"{_oauthBase}{TokenPath}", payload, ct)
                              .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct)
                              .ConfigureAwait(false))!;
    }

    public async Task<TokenResponse> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var payload = new
        {
            grant_type = "refresh_token",
            client_id = _secrets.OAuthClientId,
            client_secret = _secrets.OAuthClientSecret,
            refresh_token = refreshToken
        };
        var resp = await _http.PostAsJsonAsync($"{_oauthBase}{TokenPath}", payload, ct)
                              .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct)
                              .ConfigureAwait(false))!;
    }
}
