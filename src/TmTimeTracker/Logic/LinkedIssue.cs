namespace TmTimeTracker.Logic;

/// <summary>
/// An issue in another tracker that a Jira ticket links to, shaped the way the estimation prompt
/// wants it.
///
/// Deliberately free of any GitLab type: the prompt builder should not have to know where the
/// text came from, and a second source later reuses this shape rather than adding a second one.
/// </summary>
/// <param name="Url">The link as the ticket wrote it, so the prompt can cite it.</param>
/// <param name="Comments">Human discussion only - tracker activity entries are dropped.</param>
public sealed record LinkedIssue(
    string Url,
    long Iid,
    string ProjectPath,
    string Title,
    string Description,
    IReadOnlyList<string> Comments);
