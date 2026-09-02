using Microsoft.Extensions.Logging;
using TmTimeTracker.Jira;
using TmTimeTracker.Logic;

namespace TmTimeTracker.Services;

public sealed record PullRequestInfo(string Url, string Title, string Status);

public interface IPullRequestSource
{
    Task<PullRequestInfo?> GetBestAsync(string? issueId, CancellationToken ct);
}

/// <summary>
/// Reads the pull request linked to an issue from Jira's development information, using the OAuth
/// token the app already holds - no Bitbucket credential is involved.
///
/// Every failure returns null rather than throwing. The endpoint is undocumented and carries no
/// scope contract, so "no pull request known" must be an ordinary outcome: the notification still
/// sends, with {PR_URL} left literal.
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

    public async Task<PullRequestInfo?> GetBestAsync(string? issueId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(issueId)) return null;

        try
        {
            var response = await _devStatus.GetPullRequestDetailAsync(issueId, ct).ConfigureAwait(false);

            var detail = response.Detail?.FirstOrDefault();
            if (detail is null) return null;

            var candidates = (detail.PullRequests ?? Array.Empty<DevStatusPullRequest>())
                .Select(pr => new PullRequestCandidate(
                    pr.Id, pr.Name, pr.Status, pr.Url, pr.RepositoryName, pr.LastUpdate));

            var best = PullRequestSelector.Best(candidates);
            if (best?.Url is null) return null;

            // Commit URLs carry the readable workspace slug that the PR URL lacks.
            var commitUrls = (detail.Branches ?? Array.Empty<DevStatusBranch>())
                .Select(branch => branch.LastCommit?.Url);

            var url = BitbucketPrLink.Build(best.Url, best.Id, best.RepositoryName, commitUrls);
            return new PullRequestInfo(url, best.Title ?? string.Empty, best.Status ?? string.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Could not read development information for issue {IssueId}; {{PR_URL}} will be left literal",
                issueId);
            return null;
        }
    }
}
