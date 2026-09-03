using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace TmTimeTracker.Jira;

public sealed class JiraApiClient : IJiraIssueSource, IDevStatusSource, IJiraSearchSource, IJiraEstimateWriter
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
            cloudId => $"{_apiBase}/{cloudId}/rest/api/3/issue/{key}"
                     + "?fields=status,summary,timetracking,description",
            contentFactory: null, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<Issue>(cancellationToken: ct).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Development information (Bitbucket pull requests) linked to an issue.
    ///
    /// This is Jira's internal dev-status API, not the documented REST v3 surface: it is what the
    /// issue view's Development panel calls, it keys on the NUMERIC issue id rather than the key,
    /// and it carries no published scope contract. Callers must treat failure as "no PR known"
    /// rather than an error.
    /// </summary>
    public async Task<DevStatusResponse> GetPullRequestDetailAsync(string issueId, CancellationToken ct)
    {
        var (resp, _) = await SendAsync(HttpMethod.Get,
            cloudId => $"{_apiBase}/{cloudId}/rest/dev-status/1.0/issue/detail" +
                       $"?issueId={Uri.EscapeDataString(issueId)}" +
                       "&applicationType=bitbucket&dataType=pullrequest",
            contentFactory: null, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<DevStatusResponse>(cancellationToken: ct)
                                  .ConfigureAwait(false))!;
    }

    public async Task<IReadOnlyList<JiraProject>> ListProjectsAsync(CancellationToken ct)
    {
        var (resp, _) = await SendAsync(HttpMethod.Get,
            cloudId => $"{_apiBase}/{cloudId}/rest/api/3/project/search?maxResults=50",
            contentFactory: null, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var page = await resp.Content.ReadFromJsonAsync<JiraProjectPage>(cancellationToken: ct)
                                     .ConfigureAwait(false);
        return page?.Values ?? Array.Empty<JiraProject>();
    }

    public async Task<WorklogResponse> PostWorklogAsync(string key, WorklogRequest body, CancellationToken ct)
    {
        var (resp, _) = await SendAsync(HttpMethod.Post,
            cloudId => $"{_apiBase}/{cloudId}/rest/api/3/issue/{key}/worklog",
            () => JsonContent.Create(body), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<WorklogResponse>(cancellationToken: ct).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Runs a JQL search.
    ///
    /// Atlassian replaced /rest/api/3/search with /rest/api/3/search/jql, and instances differ in
    /// when they cut over, so a rejection of the new path falls back to the old one rather than
    /// failing the sweep outright.
    /// </summary>
    public async Task<IReadOnlyList<Issue>> SearchIssuesAsync(string jql, CancellationToken ct)
    {
        var body = new
        {
            jql,
            maxResults = 50,
            fields = new[] { "summary", "status", "timetracking", "description" }
        };

        var (resp, _) = await SendAsync(HttpMethod.Post,
            cloudId => $"{_apiBase}/{cloudId}/rest/api/3/search/jql",
            () => JsonContent.Create(body), ct).ConfigureAwait(false);

        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            _log.LogDebug("Jira rejected /search/jql with {Status}; falling back to /search",
                resp.StatusCode);
            resp.Dispose();
            (resp, _) = await SendAsync(HttpMethod.Post,
                cloudId => $"{_apiBase}/{cloudId}/rest/api/3/search",
                () => JsonContent.Create(body), ct).ConfigureAwait(false);
        }

        using (resp)
        {
            resp.EnsureSuccessStatusCode();
            var page = await resp.Content.ReadFromJsonAsync<JiraSearchResponse>(cancellationToken: ct)
                                         .ConfigureAwait(false);
            return page?.Issues ?? Array.Empty<Issue>();
        }
    }

    /// <summary>
    /// Writes the Original Estimate.
    ///
    /// The value is sent as plain minutes ("140m") rather than Jira's day/week syntax, because
    /// "1d" means a working day whose length is a per-instance setting - expressing the estimate
    /// in minutes keeps it independent of that configuration.
    ///
    /// A 2xx here does NOT mean the estimate was stored: Jira accepts the request and silently
    /// ignores timetracking when the field is absent from the edit screen. The caller must read
    /// the issue back.
    /// </summary>
    public async Task SetOriginalEstimateAsync(string key, int minutes, CancellationToken ct)
    {
        var body = new { fields = new { timetracking = new { originalEstimate = $"{minutes}m" } } };

        var (resp, _) = await SendAsync(HttpMethod.Put,
            cloudId => $"{_apiBase}/{cloudId}/rest/api/3/issue/{key}",
            () => JsonContent.Create(body), ct).ConfigureAwait(false);

        using (resp) resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// The body arrives as a factory rather than an instance because this method retries after a
    /// 401, and an HttpContent cannot be sent twice - the second send throws on the already
    /// consumed stream, turning a recoverable token refresh into a hard failure.
    /// </summary>
    private async Task<(HttpResponseMessage Response, string CloudId)> SendAsync(
        HttpMethod method, Func<string, string> urlBuilder, Func<HttpContent>? contentFactory,
        CancellationToken ct)
    {
        async Task<(HttpResponseMessage, string)> One()
        {
            var (token, cloud) = await _tokens.GetAccessTokenAsync(ct).ConfigureAwait(false);
            var req = new HttpRequestMessage(method, urlBuilder(cloud));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (contentFactory is not null) req.Content = contentFactory();
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
