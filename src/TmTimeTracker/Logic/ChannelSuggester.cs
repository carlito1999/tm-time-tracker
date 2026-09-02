namespace TmTimeTracker.Logic;

public static class ChannelSuggester
{
    /// <summary>
    /// Pre-matches a Jira project name to a Slack channel by comparing alphanumerics only.
    /// Returns null when nothing matches OR when more than one candidate does: a wrong
    /// pre-selection the user clicks past posts to the wrong channel silently, which is worse
    /// than no pre-selection at all.
    /// </summary>
    public static string? Suggest(string? projectName, IEnumerable<string> channelNames)
    {
        var needle = Normalize(projectName);
        if (needle.Length == 0) return null;

        var matches = channelNames
            .Where(name =>
            {
                var candidate = Normalize(name);
                return candidate.Length > 0
                    && (needle.StartsWith(candidate, StringComparison.Ordinal)
                     || candidate.StartsWith(needle, StringComparison.Ordinal));
            })
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    private static string Normalize(string? value) =>
        new((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
}
