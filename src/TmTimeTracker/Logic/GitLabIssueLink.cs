using System.Text.RegularExpressions;

namespace TmTimeTracker.Logic;

/// <summary>One GitLab issue a ticket points at.</summary>
/// <param name="ProjectPath">Namespace path, e.g. si-bv/stamboekonline. Groups nest arbitrarily.</param>
/// <param name="Iid">The per-project number shown in the URL, not the global id.</param>
/// <param name="Url">The link as written, kept so the prompt can cite it.</param>
public sealed record GitLabIssueRef(string ProjectPath, long Iid, string Url);

/// <summary>
/// Recognises the GitLab issue URLs that turn up in Jira descriptions here.
///
/// Two URL shapes name the same thing: /-/issues/377 and /-/work_items/377. GitLab's newer work
/// item UI produces the latter and SN-305's description uses it, but the REST API is reached
/// through /issues/:iid either way - so both parse, and both collapse to one fetch.
///
/// The project path is matched lazily up to the "/-/" separator because groups nest: a path can
/// be "a/b" or "a/b/c/d". It is percent-encoded by the caller rather than here, so this record
/// keeps the human-readable value for logging.
///
/// Only gitlab.com is accepted. A self-hosted instance would need its own base URL in settings,
/// and silently trusting an arbitrary host would send the token somewhere unintended.
/// </summary>
public static class GitLabIssueLink
{
    /// <summary>Each linked issue is content in a paid context window.</summary>
    public const int MaxLinks = 3;

    private static readonly Regex IssueUrl = new(
        @"^https?://gitlab\.com/(?<project>.+?)/-/(?:issues|work_items)/(?<iid>\d+)(?:[/?#]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static GitLabIssueRef? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var trimmed = url.Trim();
        var match = IssueUrl.Match(trimmed);
        if (!match.Success) return null;

        if (!long.TryParse(match.Groups["iid"].Value, out var iid) || iid <= 0) return null;

        var project = match.Groups["project"].Value.Trim('/');
        return project.Length == 0 ? null : new GitLabIssueRef(project, iid, trimmed);
    }

    /// <summary>
    /// Every distinct issue in a set of candidate URLs, capped. Both URL shapes for one issue
    /// collapse to a single entry, so a description linking the work item and the issue view of
    /// the same ticket costs one fetch rather than two.
    /// </summary>
    public static IReadOnlyList<GitLabIssueRef> FindAll(IEnumerable<string>? urls, int max = MaxLinks)
    {
        if (urls is null) return Array.Empty<GitLabIssueRef>();

        var found = new List<GitLabIssueRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var url in urls)
        {
            var link = Parse(url);
            if (link is null) continue;
            if (!seen.Add($"{link.ProjectPath}#{link.Iid}")) continue;

            found.Add(link);
            if (found.Count == max) break;
        }

        return found;
    }
}
