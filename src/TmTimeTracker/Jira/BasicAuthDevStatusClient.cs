using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Services;

namespace TmTimeTracker.Jira;

/// <summary>
/// Reads Jira's dev-status API - the one behind an issue's Development panel, carrying the linked
/// branches, commits and pull requests.
///
/// This is a separate client from <see cref="JiraApiClient"/> because it needs a different
/// credential and a different host. dev-status does not accept OAuth 3LO tokens at all: through
/// the api.atlassian.com gateway it answers 401 "scope does not match" no matter which scopes are
/// granted, and no scope that would help is even offered in the developer console. Basic auth with
/// an Atlassian account email and API token, straight at the site host, is the documented way in.
///
/// Like the OAuth client, every failure is an empty response rather than an exception: the
/// endpoint is undocumented, and "nothing known yet" is an ordinary outcome for a fresh branch.
/// </summary>
public sealed class BasicAuthDevStatusClient : IDevStatusSource
{
    private readonly HttpClient _http;
    private readonly JiraApiTokenRepository _credentials;
    private readonly IJiraSiteResolver _site;
    private readonly ILogger<BasicAuthDevStatusClient> _log;

    private bool _warnedUnconfigured;

    public BasicAuthDevStatusClient(
        HttpClient http,
        JiraApiTokenRepository credentials,
        IJiraSiteResolver site,
        ILogger<BasicAuthDevStatusClient> log)
    {
        _http = http; _credentials = credentials; _site = site; _log = log;
    }

    public async Task<DevStatusResponse> GetPullRequestDetailAsync(string issueId, CancellationToken ct)
    {
        var credential = _credentials.Get();
        if (credential is null)
        {
            // Said once rather than every 30s. Without this the app polls forever in silence and
            // looks merely broken, which is exactly how the OAuth dead end went unnoticed.
            if (!_warnedUnconfigured)
            {
                _warnedUnconfigured = true;
                _log.LogWarning(
                    "No Jira API token configured, so pull request URLs cannot be resolved. " +
                    "Set one with --set-jira-token <email> <token>.");
            }
            return DevStatusResponse.Empty;
        }

        var site = await _site.GetSiteUrlAsync(ct).ConfigureAwait(false);
        if (site is null) return DevStatusResponse.Empty;

        var url = $"{site.TrimEnd('/')}/rest/dev-status/1.0/issue/detail" +
                  $"?issueId={Uri.EscapeDataString(issueId)}" +
                  "&applicationType=bitbucket&dataType=pullrequest";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Encode(credential));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning("dev-status returned {Status} for issue {IssueId}",
                (int)response.StatusCode, issueId);
            return DevStatusResponse.Empty;
        }

        return await response.Content.ReadFromJsonAsync<DevStatusResponse>(cancellationToken: ct)
                                     .ConfigureAwait(false)
               ?? DevStatusResponse.Empty;
    }

    internal static string Encode(JiraApiCredential credential) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.Email}:{credential.Token}"));
}
