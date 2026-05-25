using System.Text.RegularExpressions;

namespace TmTimeTracker.Logic;

public static class VsCodeWindowTitleParser
{
    private static readonly Regex WithFile = new(
        @"^(?:●\s*)?.+?\s-\s(?<folder>.+?)\s-\sVisual Studio Code$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FolderOnly = new(
        @"^(?<folder>.+?)\s-\sVisual Studio Code$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string? Parse(string? title)
    {
        if (string.IsNullOrEmpty(title)) return null;
        var withFile = WithFile.Match(title);
        if (withFile.Success) return withFile.Groups["folder"].Value;
        var folderOnly = FolderOnly.Match(title);
        return folderOnly.Success ? folderOnly.Groups["folder"].Value : null;
    }
}
