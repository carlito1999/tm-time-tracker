using TmTimeTracker.Jira;

namespace TmTimeTracker.Logic;

/// <summary>
/// Maps a tracked repo folder to the Jira project whose board covers it.
///
/// The boards are named after the repos, but the two spellings never match literally: Jira
/// shows a display name ("Training Manager") while the folder on disk is lowercase and
/// hyphenated ("training-manager"). Both sides are therefore reduced to letters and digits
/// before comparison.
///
/// The display name is matched before the key, because a folder named after one project's key
/// would otherwise be stolen by that key even when another project's name matches it exactly.
///
/// Ambiguity returns null rather than a guess. Picking one of two equally good candidates
/// would write an estimate onto a ticket in the wrong project - a silent, wrong Jira edit is
/// far worse than an unmapped repo the user has to map by hand.
/// </summary>
public static class RepoProjectMatcher
{
    public static string? Match(string? repoPath, IReadOnlyList<JiraProject> projects)
    {
        var folder = FolderName(repoPath);
        if (folder.Length == 0 || projects.Count == 0) return null;

        return Single(projects.Where(p => Normalise(p.Name) == folder))
            ?? Single(projects.Where(p => Normalise(p.Key) == folder));
    }

    private static string? Single(IEnumerable<JiraProject> matches)
    {
        var keys = matches.Select(p => p.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return keys.Count == 1 ? keys[0] : null;
    }

    private static string FolderName(string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath)) return "";
        // A trailing separator would make GetFileName return an empty string.
        var trimmed = repoPath.TrimEnd('/', '\\');
        return Normalise(Path.GetFileName(trimmed));
    }

    private static string Normalise(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }
}
