using TmTimeTracker.Logic;

namespace TmTimeTracker.Jev;

/// <summary>
/// The state Jev is asked about for one ticket: the ticket's own words and nothing else.
///
/// Jev's accuracy falls as unrelated material piles into the state, so this deliberately leaves
/// out the key, the repository and any code. Attachments go in by name only - Jev reads text and
/// cannot open them - because knowing a screenshot exists tells it the description is not the
/// whole story. A linked GitLab issue goes in whole, since an SN ticket is often nothing but that
/// link. Empty sections are omitted rather than sent blank.
/// </summary>
public static class JevTicketState
{
    // Well inside the 32k-token state limit, and matching the estimation prompt's own cap so a
    // ticket reads the same to both models.
    private const int MaxDescriptionChars = 8000;

    public static IReadOnlyDictionary<string, object> Build(
        string? summary,
        string description,
        IReadOnlyList<string> attachmentNames,
        IReadOnlyList<LinkedIssue> linked)
    {
        var state = new Dictionary<string, object>
        {
            ["summary"] = string.IsNullOrWhiteSpace(summary) ? "(none given)" : summary.Trim()
        };

        var text = description.Trim();
        if (text.Length > 0)
            state["description"] = text.Length <= MaxDescriptionChars
                ? text
                : text[..MaxDescriptionChars] + "\n\n[truncated]";

        if (attachmentNames.Count > 0)
            state["attachments"] = attachmentNames.ToArray();

        if (linked.Count > 0)
            state["linked_issues"] = linked.Select(i => new Dictionary<string, object>
            {
                ["title"] = i.Title,
                ["description"] = i.Description,
                ["comments"] = i.Comments.ToArray()
            }).ToArray();

        return state;
    }
}
