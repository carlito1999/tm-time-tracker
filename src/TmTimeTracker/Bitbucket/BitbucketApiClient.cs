using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;

namespace TmTimeTracker.Bitbucket;

public interface IBitbucketPullRequestSource
{
    Task<IReadOnlyList<PullRequestCandidate>> GetPullRequestsAsync(
        BitbucketRepo repo, string ticketKey, CancellationToken ct);
}

/// <summary>
/// Reads pull requests straight from Bitbucket, which is the only place that knows about one the
/// moment it is opened.
///
/// The announcement used to read Jira's dev-status instead. That is a webhook-fed mirror of this
/// same data, and on 2026-09-04 it had not ingested pull request 367 when asked 15 seconds after
/// the pull request was created - so SN-291 was announced pointing at the superseded 353. Asking
/// Bitbucket removes the mirror, and with it the lag.
///
/// Needs a token carrying read:pullrequest:bitbucket; the scopeless one Jira's dev-status accepts
/// is rejected here. Every failure returns an empty list rather than throwing, so a missing token
/// or a Bitbucket outage falls back to the dev-status path instead of breaking the announcement.
/// </summary>
public sealed class BitbucketApiClient : IBitbucketPullRequestSource
{
    private const string DefaultApiBase = "https://api.bitbucket.org/2.0";

    // A single branch never has enough pull requests to page, and the query is branch-scoped.
    private const int PageLength = 50;

    private readonly HttpClient _http;
    private readonly BitbucketApiTokenRepository _credentials;
    private readonly ILogger<BitbucketApiClient> _log;
    private readonly string _apiBase;

    private bool _warnedUnconfigured;

    public BitbucketApiClient(
        HttpClient http,
        BitbucketApiTokenRepository credentials,
        ILogger<BitbucketApiClient> log,
        string? apiBaseOverride = null)
    {
        _http = http;
        _credentials = credentials;
        _log = log;
        _apiBase = (apiBaseOverride ?? DefaultApiBase).TrimEnd('/');
    }

    public async Task<IReadOnlyList<PullRequestCandidate>> GetPullRequestsAsync(
        BitbucketRepo repo, string ticketKey, CancellationToken ct)
    {
        var credential = _credentials.Get();
        if (credential is null)
        {
            // Said once rather than every 30s, so the log shows the cause without drowning in it.
            if (!_warnedUnconfigured)
            {
                _warnedUnconfigured = true;
                _log.LogWarning(
                    "No Bitbucket API token configured, so pull requests fall back to Jira's " +
                    "dev-status mirror, which lags. Set one with --set-bitbucket-token <email> <token>.");
            }
            return Array.Empty<PullRequestCandidate>();
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, QueryUrl(repo, ticketKey));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Encode(credential));

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Bitbucket returned {Status} for {Workspace}/{Slug} pull requests",
                    (int)response.StatusCode, repo.Workspace, repo.Slug);
                return Array.Empty<PullRequestCandidate>();
            }

            var page = await response.Content
                .ReadFromJsonAsync<BitbucketPullRequestPage>(cancellationToken: ct)
                .ConfigureAwait(false);

            return (page?.Values ?? Array.Empty<BitbucketPullRequest>())
                .Where(pr => !string.IsNullOrWhiteSpace(pr.Links?.Html?.Href))
                .Select(pr => new PullRequestCandidate(
                    pr.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    pr.Title,
                    pr.State,
                    pr.Links!.Html!.Href,
                    repo.Slug,
                    pr.UpdatedOn,
                    pr.Source?.Branch?.Name))
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read pull requests for {Workspace}/{Slug}",
                repo.Workspace, repo.Slug);
            return Array.Empty<PullRequestCandidate>();
        }
    }

    /// <summary>
    /// Scopes the query to branches carrying the ticket key, so another ticket's pull request
    /// never reaches the selector in the first place.
    ///
    /// Every state is requested explicitly: Bitbucket returns only OPEN ones by default, and a
    /// ticket whose pull request was merged before the announcement still needs a link.
    /// </summary>
    private string QueryUrl(BitbucketRepo repo, string ticketKey)
    {
        var filter = Uri.EscapeDataString($"source.branch.name ~ \"{ticketKey}\"");

        return $"{_apiBase}/repositories/{Uri.EscapeDataString(repo.Workspace)}/" +
               $"{Uri.EscapeDataString(repo.Slug)}/pullrequests" +
               $"?q={filter}&pagelen={PageLength}" +
               "&state=OPEN&state=MERGED&state=DECLINED&state=SUPERSEDED" +
               "&fields=values.id,values.title,values.state,values.updated_on," +
               "values.source.branch.name,values.links.html.href";
    }

    internal static string Encode(AtlassianCredential credential) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.Email}:{credential.Token}"));
}
