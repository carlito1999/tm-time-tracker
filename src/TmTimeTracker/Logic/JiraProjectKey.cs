using System.Text.RegularExpressions;

namespace TmTimeTracker.Logic;

public static class JiraProjectKey
{
    private static readonly Regex Pattern = new(@"^(?<project>[A-Z][A-Z0-9_]+)-\d+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string? From(string? ticketKey)
    {
        if (string.IsNullOrWhiteSpace(ticketKey)) return null;
        var match = Pattern.Match(ticketKey.Trim());
        return match.Success ? match.Groups["project"].Value : null;
    }
}
