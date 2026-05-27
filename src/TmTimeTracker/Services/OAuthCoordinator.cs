using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;
using TmTimeTracker.Platform;

namespace TmTimeTracker.Services;

public sealed class OAuthCoordinator : IAccessTokenSource
{
    private readonly OAuthStateRepository _state;
    private readonly ITokenProtector _protector;
    private readonly JiraOAuthClient _oauth;
    private readonly HttpClient _httpForCloud;
    private readonly ILogger<OAuthCoordinator> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _forceRefreshOnNext;

    public OAuthCoordinator(OAuthStateRepository state, ITokenProtector protector,
        JiraOAuthClient oauth, HttpClient httpForCloud, ILogger<OAuthCoordinator> log)
    {
        _state = state; _protector = protector; _oauth = oauth;
        _httpForCloud = httpForCloud; _log = log;
    }

    public bool IsAuthenticated => _state.Load() is not null;

    public async Task CompleteFirstRunAsync(string authorizationCode, CancellationToken ct)
    {
        var token = await _oauth.ExchangeCodeAsync(authorizationCode, ct).ConfigureAwait(false);
        var resources = await FetchAccessibleAsync(token.AccessToken, ct).ConfigureAwait(false);
        if (resources.Count == 0)
            throw new InvalidOperationException("No accessible Atlassian sites returned by /accessible-resources.");
        if (resources.Count > 1)
            _log.LogWarning(
                "OAuth granted access to {Count} sites; defaulting to {Url}. Switch via Settings → Connection.",
                resources.Count, resources[0].Url);
        SaveTokens(token, resources[0].Id);
        _log.LogInformation("OAuth bootstrap complete; cloudId={CloudId} url={Url}",
            resources[0].Id, resources[0].Url);
    }

    public async Task ReauthorizeAsync(string authorizationCode, CancellationToken ct)
    {
        var token = await _oauth.ExchangeCodeAsync(authorizationCode, ct).ConfigureAwait(false);
        var existing = _state.Load()
            ?? throw new InvalidOperationException("Cannot reauthorize before initial setup.");
        SaveTokens(token, existing.CloudId);
        _log.LogInformation("OAuth reauthorization complete; kept cloudId={CloudId}", existing.CloudId);
    }

    public async Task<IReadOnlyList<AtlassianResource>> ListAccessibleAsync(CancellationToken ct)
    {
        var (token, _) = await GetAccessTokenAsync(ct).ConfigureAwait(false);
        return await FetchAccessibleAsync(token, ct).ConfigureAwait(false);
    }

    public void SwitchCloudId(string newCloudId)
    {
        _state.UpdateCloudId(newCloudId);
        _log.LogInformation("Active cloudId switched to {CloudId}", newCloudId);
    }

    public void Disconnect()
    {
        _state.Clear();
        _log.LogInformation("OAuth state cleared by user (Disconnect).");
    }

    public async Task<(string AccessToken, string CloudId)> GetAccessTokenAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var s = _state.Load() ?? throw new InvalidOperationException("Not authenticated; run OAuth first.");
            if (_forceRefreshOnNext || s.AccessExpiresAt <= DateTime.UtcNow.AddMinutes(1))
            {
                var refresh = _protector.Unprotect(s.RefreshTokenDpapi);
                var fresh = await _oauth.RefreshAsync(refresh, ct).ConfigureAwait(false);
                SaveTokens(fresh, s.CloudId);
                _forceRefreshOnNext = false;
                return (fresh.AccessToken, s.CloudId);
            }
            var access = _protector.Unprotect(s.AccessTokenDpapi);
            return (access, s.CloudId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task ForceRefreshAsync(CancellationToken ct)
    {
        _forceRefreshOnNext = true;
        return Task.CompletedTask;
    }

    private async Task<IReadOnlyList<AtlassianResource>> FetchAccessibleAsync(string accessToken, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get,
            "https://api.atlassian.com/oauth/token/accessible-resources");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var resp = await _httpForCloud.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var resources = await resp.Content.ReadFromJsonAsync<AtlassianResource[]>(cancellationToken: ct)
                                          .ConfigureAwait(false);
        return resources ?? Array.Empty<AtlassianResource>();
    }

    private void SaveTokens(TokenResponse token, string cloudId) =>
        _state.Save(new OAuthState(
            CloudId: cloudId,
            AccessTokenDpapi: _protector.Protect(token.AccessToken),
            RefreshTokenDpapi: _protector.Protect(token.RefreshToken),
            AccessExpiresAt: DateTime.UtcNow.AddSeconds(token.ExpiresInSeconds),
            Scope: token.Scope));
}
