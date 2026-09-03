namespace TmTimeTracker.Jira;

/// <summary>
/// Narrow read seam over the JQL search, so the estimation sweep can be unit tested without an
/// HTTP client. Kept separate from IJiraIssueSource because only the estimator needs search.
/// </summary>
public interface IJiraSearchSource
{
    Task<IReadOnlyList<Issue>> SearchIssuesAsync(string jql, CancellationToken ct);
}

/// <summary>
/// Write seam for the Original Estimate. Separate from the read seams because this is the only
/// place the estimator mutates Jira, and a fake for it is what lets the gate-4 read-back test
/// simulate Jira accepting a write and storing nothing.
/// </summary>
public interface IJiraEstimateWriter
{
    Task SetOriginalEstimateAsync(string ticketKey, int minutes, CancellationToken ct);
}
