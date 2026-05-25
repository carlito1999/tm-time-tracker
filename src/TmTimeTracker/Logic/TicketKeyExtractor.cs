using System.Text.RegularExpressions;

namespace TmTimeTracker.Logic;

public static class TicketKeyExtractor
{
    private static readonly Regex Pattern = new(@"\bTM-\d+\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string? Extract(string? branchName)
    {
        if (string.IsNullOrEmpty(branchName)) return null;
        var match = Pattern.Match(branchName);
        return match.Success ? match.Value : null;
    }
}
