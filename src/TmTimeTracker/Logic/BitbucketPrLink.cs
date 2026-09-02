using System.Text.RegularExpressions;

namespace TmTimeTracker.Logic;

/// <summary>
/// Turns the pull-request URL Jira reports into one a human can read.
///
/// Jira's development information returns PR URLs in UUID form, e.g.
///   https://bitbucket.org/{824edde7-...}/{7730b682-...}/pull-requests/360
/// which resolves correctly but tells a Slack reader nothing about which repository it is.
/// The friendly workspace slug is available elsewhere in the same payload - commit URLs use it -
/// so a readable link can be rebuilt with no extra API call.
/// </summary>
public static class BitbucketPrLink
{
    private static readonly Regex CommitUrl = new(
        @"^https://bitbucket\.org/(?<workspace>[^/{}]+)/(?<repo>[^/{}]+)/commits?/",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Builds the most readable URL available. Falls back to the raw URL with its braces
    /// percent-encoded, since a bare '{' stops Slack linkifying the whole thing.
    /// </summary>
    public static string Build(
        string rawUrl, string? prId, string? repositoryName, IEnumerable<string?>? commitUrls)
    {
        var workspace = WorkspaceFor(repositoryName, commitUrls);

        if (workspace is not null
            && !string.IsNullOrWhiteSpace(repositoryName)
            && !string.IsNullOrWhiteSpace(prId))
        {
            return $"https://bitbucket.org/{workspace}/{repositoryName}/pull-requests/{prId}";
        }

        return EncodeBraces(rawUrl);
    }

    /// <summary>
    /// Finds the workspace slug from a commit URL belonging to the same repository. Requiring the
    /// repository to match avoids stitching one repo's workspace onto another's name when an issue
    /// spans several repositories.
    /// </summary>
    private static string? WorkspaceFor(string? repositoryName, IEnumerable<string?>? commitUrls)
    {
        if (string.IsNullOrWhiteSpace(repositoryName) || commitUrls is null) return null;

        foreach (var url in commitUrls)
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            var match = CommitUrl.Match(url);
            if (match.Success &&
                string.Equals(match.Groups["repo"].Value, repositoryName, StringComparison.OrdinalIgnoreCase))
            {
                return match.Groups["workspace"].Value;
            }
        }

        return null;
    }

    private static string EncodeBraces(string url) =>
        url.Replace("{", "%7B", StringComparison.Ordinal)
           .Replace("}", "%7D", StringComparison.Ordinal);
}
