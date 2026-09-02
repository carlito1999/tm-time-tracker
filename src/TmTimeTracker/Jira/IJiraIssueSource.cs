namespace TmTimeTracker.Jira;

/// <summary>
/// Narrow read seam over <see cref="JiraApiClient"/> so poll/notify services can be unit tested
/// without an HTTP client. JiraApiClient is sealed, so this is the only way to fake issue reads.
/// </summary>
public interface IJiraIssueSource
{
    Task<Issue> GetIssueAsync(string key, CancellationToken ct);
}

/// <summary>
/// Seam over the /accessible-resources lookup, used to resolve the Jira site URL lazily.
/// </summary>
public interface IAccessibleSiteSource
{
    Task<IReadOnlyList<AtlassianResource>> ListAccessibleAsync(CancellationToken ct);
}

/// <summary>
/// Seam over Jira's development-information lookup, so pull-request handling can be unit tested
/// without HTTP. Kept separate from IJiraIssueSource because this endpoint is internal and
/// unversioned - isolating it keeps the blast radius small if Atlassian changes it.
/// </summary>
public interface IDevStatusSource
{
    Task<DevStatusResponse> GetPullRequestDetailAsync(string issueId, CancellationToken ct);
}
