using System.Globalization;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Bitbucket;
using TmTimeTracker.Logic;

namespace TmTimeTracker.Services;

/// <summary>
/// Asks Bitbucket first and Jira's dev-status only as a fallback.
///
/// dev-status is a webhook-fed mirror of Bitbucket, and on 2026-09-04 it had not ingested pull
/// request 367 when the announcement asked, 15 seconds after the pull request was created - so
/// SN-291 was announced pointing at the superseded 353. Bitbucket has the pull request the moment
/// it exists, so it is the authority here.
///
/// The fallback still earns its place twice over: it covers a user who has not stored a Bitbucket
/// token, a repo whose remote is not on Bitbucket, and any outage; and it is the only source of
/// the branch's last commit time, which anchors the overdue warning. That warning only fires when
/// there is no pull request to announce, so the fallback is skipped entirely - one request, not
/// two - whenever Bitbucket has already answered.
/// </summary>
public sealed class BitbucketPullRequestSource : IPullRequestSource
{
    private readonly IBitbucketPullRequestSource _bitbucket;
    private readonly BitbucketRepoResolver _repos;
    private readonly IPullRequestSource _fallback;
    private readonly ILogger<BitbucketPullRequestSource> _log;

    public BitbucketPullRequestSource(
        IBitbucketPullRequestSource bitbucket,
        BitbucketRepoResolver repos,
        IPullRequestSource fallback,
        ILogger<BitbucketPullRequestSource> log)
    {
        _bitbucket = bitbucket;
        _repos = repos;
        _fallback = fallback;
        _log = log;
    }

    public async Task<DevInfoSnapshot> GetSnapshotAsync(
        string? issueId, string ticketKey, CancellationToken ct)
    {
        var candidates = new List<PullRequestCandidate>();

        foreach (var repo in _repos.ReposFor(ticketKey))
        {
            ct.ThrowIfCancellationRequested();
            candidates.AddRange(
                await _bitbucket.GetPullRequestsAsync(repo, ticketKey, ct).ConfigureAwait(false));
        }

        // The query is branch-scoped, but the client does not filter what comes back; the selector
        // is still what decides a pull request belongs to this ticket.
        var best = PullRequestSelector.Best(candidates, ticketKey);
        if (best?.Url is not null)
        {
            _log.LogDebug("Bitbucket reports pull request {Id} for {Ticket}", best.Id, ticketKey);
            return new DevInfoSnapshot(
                new PullRequestInfo(best.Url, best.Title ?? string.Empty,
                                    best.Status ?? string.Empty, UpdatedAt(best)),
                null);
        }

        return await _fallback.GetSnapshotAsync(issueId, ticketKey, ct).ConfigureAwait(false);
    }

    private static DateTimeOffset? UpdatedAt(PullRequestCandidate candidate) =>
        DateTimeOffset.TryParse(candidate.LastUpdate, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
}
