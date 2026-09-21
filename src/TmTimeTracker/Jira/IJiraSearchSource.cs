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

/// <summary>
/// Seam over the project list, used to auto-match a tracked repo folder to its Jira board.
/// </summary>
public interface IJiraProjectSource
{
    Task<IReadOnlyList<JiraProject>> ListProjectsAsync(CancellationToken ct);
}

/// <summary>
/// Seam over attachment downloads. Ticket detail frequently lives only in a screenshot, so the
/// estimator fetches images and lets Claude read them alongside the code.
/// </summary>
public interface IJiraAttachmentSource
{
    Task<byte[]> DownloadAttachmentAsync(string contentUrl, CancellationToken ct);
}
