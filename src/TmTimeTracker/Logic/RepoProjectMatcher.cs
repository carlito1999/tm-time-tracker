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

        // Tiers, strongest first. A tier is consulted only when the previous one found nothing,
        // and a tier that finds several candidates stops the search rather than falling through
        // to a looser rule - several matches means less certainty, not more.
        var tiers = new Func<JiraProject, bool>[]
        {
            p => Normalise(p.Name) == folder,
            p => Normalise(p.Key) == folder,
            p => Extends(Normalise(p.Name), folder)
        };

        foreach (var tier in tiers)
        {
            var keys = projects.Where(tier).Select(p => p.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (keys.Count == 1) return keys[0];
            if (keys.Count > 1) return null;
        }

        return null;
    }

    /// <summary>
    /// One name is the other's beginning: the folder "dynatag" against the project
    /// "Dynatag-Codes", or a folder "dynatag-codes" against a project "Dynatag".
    ///
    /// This tier runs last for a reason. "Sheeponline" and "SheepOnline New" are both real
    /// projects here, and a repo called "sheeponline-new" extends the first while exactly
    /// matching the second - resolving exact names first is what stops the older board winning.
    /// </summary>
    private static bool Extends(string projectName, string folder) =>
        projectName.Length > 0 &&
        (projectName.StartsWith(folder, StringComparison.Ordinal) ||
         folder.StartsWith(projectName, StringComparison.Ordinal));


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
