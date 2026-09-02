using System.Net.Http.Json;
using System.Text;

namespace TmTimeTracker.Jira;

public sealed class JiraOAuthClient
{
    private const string DefaultOAuthBase = "https://auth.atlassian.com";
    private const string AuthorizePath = "/authorize";
    private const string TokenPath = "/oauth/token";

    private readonly HttpClient _http;
    private readonly IOAuthAppConfigSource _source;
    private readonly string _oauthBase;

    public JiraOAuthClient(HttpClient http, IOAuthAppConfigSource source, string? oauthBaseOverride = null)
    {
        _http = http;
        _source = source;
        _oauthBase = (oauthBaseOverride ?? DefaultOAuthBase).TrimEnd('/');
    }

    public string BuildAuthorizationUrl(string state)
    {
        var c = _source.Get();
        var qs = new StringBuilder()
            .Append("audience=api.atlassian.com")
            .Append("&client_id=").Append(Uri.EscapeDataString(c.ClientId))
            // Deliberately no dev-info scope here. /rest/dev-status/ does not accept OAuth 3LO
            // tokens at all - it answers 401 "scope does not match" whatever is granted, and no
            // scope that would help is offered in the developer console. It is read with basic
            // auth instead; see BasicAuthDevStatusClient.
            .Append("&scope=").Append(Uri.EscapeDataString(
                "read:jira-work write:jira-work offline_access"))
            .Append("&redirect_uri=").Append(Uri.EscapeDataString(c.RedirectUri))
            .Append("&state=").Append(Uri.EscapeDataString(state))
            .Append("&response_type=code&prompt=consent");
        return $"{_oauthBase}{AuthorizePath}?{qs}";
    }

    public async Task<TokenResponse> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        var c = _source.Get();
        var payload = new
        {
            grant_type = "authorization_code",
            client_id = c.ClientId,
            client_secret = c.ClientSecret,
            code,
            redirect_uri = c.RedirectUri
        };
        var resp = await _http.PostAsJsonAsync($"{_oauthBase}{TokenPath}", payload, ct)
                              .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct)
                              .ConfigureAwait(false))!;
    }

    public async Task<TokenResponse> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var c = _source.Get();
        var payload = new
        {
            grant_type = "refresh_token",
            client_id = c.ClientId,
            client_secret = c.ClientSecret,
            refresh_token = refreshToken
        };
        var resp = await _http.PostAsJsonAsync($"{_oauthBase}{TokenPath}", payload, ct)
                              .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct)
                              .ConfigureAwait(false))!;
    }
}
