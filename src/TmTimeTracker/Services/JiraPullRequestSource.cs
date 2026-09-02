using System.Globalization;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Jira;
using TmTimeTracker.Logic;

namespace TmTimeTracker.Services;

public sealed record PullRequestInfo(string Url, string Title, string Status);

/// <summary>
/// What Jira knows about an issue's development activity right now. LastCommitUtc anchors the
/// "no pull request yet" warning: waiting is measured from the last commit, not from when the
/// ticket happened to be dragged to Review.
/// </summary>
public sealed record DevInfoSnapshot(PullRequestInfo? PullRequest, DateTimeOffset? LastCommitUtc)
{
    public static readonly DevInfoSnapshot Empty = new(null, null);
}

public interface IPullRequestSource
{
    Task<DevInfoSnapshot> GetSnapshotAsync(string? issueId, CancellationToken ct);
}

/// <summary>
/// Reads development information from Jira with the OAuth token the app already holds - no
/// Bitbucket credential is involved. Requires the read:dev-info:jira scope.
///
/// Every failure returns an empty snapshot rather than throwing: the endpoint is undocumented, so
/// "nothing known yet" has to be an ordinary outcome.
/// </summary>
public sealed class JiraPullRequestSource : IPullRequestSource
{
    private readonly IDevStatusSource _devStatus;
    private readonly ILogger<JiraPullRequestSource> _log;

    public JiraPullRequestSource(IDevStatusSource devStatus, ILogger<JiraPullRequestSource> log)
    {
        _devStatus = devStatus;
        _log = log;
    }

    public async Task<DevInfoSnapshot> GetSnapshotAsync(string? issueId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(issueId)) return DevInfoSnapshot.Empty;

        try
        {
            var response = await _devStatus.GetPullRequestDetailAsync(issueId, ct).ConfigureAwait(false);

            var detail = response.Detail?.FirstOrDefault();
            if (detail is null) return DevInfoSnapshot.Empty;

            var branches = detail.Branches ?? Array.Empty<DevStatusBranch>();
            var lastCommit = branches
                .Select(b => ParseTimestamp(b.LastCommit?.AuthorTimestamp))
                .Where(t => t is not null)
                .DefaultIfEmpty(null)
                .Max();

            var candidates = (detail.PullRequests ?? Array.Empty<DevStatusPullRequest>())
                .Select(pr => new PullRequestCandidate(
                    pr.Id, pr.Name, pr.Status, pr.Url, pr.RepositoryName, pr.LastUpdate));

            var best = PullRequestSelector.Best(candidates);
            if (best?.Url is null) return new DevInfoSnapshot(null, lastCommit);

            // Commit URLs carry the readable workspace slug that the PR URL lacks.
            var commitUrls = branches.Select(branch => branch.LastCommit?.Url);
            var url = BitbucketPrLink.Build(best.Url, best.Id, best.RepositoryName, commitUrls);

            return new DevInfoSnapshot(
                new PullRequestInfo(url, best.Title ?? string.Empty, best.Status ?? string.Empty),
                lastCommit);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read development information for issue {IssueId}", issueId);
            return DevInfoSnapshot.Empty;
        }
    }

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
}
