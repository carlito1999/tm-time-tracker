using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace TmTimeTracker.Jira;

public sealed class JiraApiClient
{
    private const string DefaultApiBase = "https://api.atlassian.com/ex/jira";
    private readonly HttpClient _http;
    private readonly IAccessTokenSource _tokens;
    private readonly ILogger<JiraApiClient> _log;
    private readonly string _apiBase;

    public JiraApiClient(HttpClient http, IAccessTokenSource tokens, ILogger<JiraApiClient> log,
        string? apiBaseOverride = null)
    {
        _http = http; _tokens = tokens; _log = log;
        _apiBase = (apiBaseOverride ?? DefaultApiBase).TrimEnd('/');
    }

    public async Task<Issue> GetIssueAsync(string key, CancellationToken ct)
    {
        var (resp, _) = await SendAsync(HttpMethod.Get,
            cloudId => $"{_apiBase}/{cloudId}/rest/api/3/issue/{key}?fields=status",
            content: null, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<Issue>(cancellationToken: ct).ConfigureAwait(false))!;
    }

    public async Task<WorklogResponse> PostWorklogAsync(string key, WorklogRequest body, CancellationToken ct)
    {
        var (resp, _) = await SendAsync(HttpMethod.Post,
            cloudId => $"{_apiBase}/{cloudId}/rest/api/3/issue/{key}/worklog",
            content: JsonContent.Create(body), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<WorklogResponse>(cancellationToken: ct).ConfigureAwait(false))!;
    }

    private async Task<(HttpResponseMessage Response, string CloudId)> SendAsync(
        HttpMethod method, Func<string, string> urlBuilder, HttpContent? content, CancellationToken ct)
    {
        async Task<(HttpResponseMessage, string)> One()
        {
            var (token, cloud) = await _tokens.GetAccessTokenAsync(ct).ConfigureAwait(false);
            var req = new HttpRequestMessage(method, urlBuilder(cloud));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (content is not null) req.Content = content;
            var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            return (resp, cloud);
        }

        var first = await One().ConfigureAwait(false);
        if (first.Item1.StatusCode != HttpStatusCode.Unauthorized) return first;

        _log.LogWarning("Jira 401 on {Method} - forcing token refresh and retrying once", method);
        first.Item1.Dispose();
        await _tokens.ForceRefreshAsync(ct).ConfigureAwait(false);
        return await One().ConfigureAwait(false);
    }
}
