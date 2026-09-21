namespace TmTimeTracker.Logic;

/// <param name="Name">Short remote ref, e.g. "origin/dev-01-09-2026".</param>
/// <param name="CommittedAt">Last commit on the branch.</param>
public sealed record BranchCandidate(string Name, DateTime CommittedAt);

/// <summary>
/// Chooses which trunk branch a repo's estimates should run against.
///
/// Two steps, because a repo's trunk is a convention rather than a fixed name.
///
/// First identify the family the repo actually uses. Branch names are grouped by their leading
/// word - main, master, dev - and the family with the most branches wins, because a repo that
/// cuts dated snapshots repeats its prefix many times over. This is what rules out a one-off
/// stub: "mainTraining" is its own family of one and loses to a "dev" family holding a dozen
/// snapshots, even though the stub is the branch origin/HEAD points at.
///
/// Then pick the newest branch inside that family by commit date, so a rolling convention keeps
/// working when the next snapshot is cut, with no configuration to update.
///
/// Grouping on the leading word is what keeps "main" and "mainTraining" apart: the split is at
/// the first non-letter, so "main-03-09-2026-updated" is family "main" while "mainTraining" is
/// its own.
/// </summary>
public static class TrunkBranchSelector
{
    public static string? Select(IReadOnlyList<BranchCandidate> candidates)
    {
        if (candidates.Count == 0) return null;

        var family = candidates
            .GroupBy(c => Family(c.Name), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            // A tie between two equally sized families goes to the one worked on most recently.
            .ThenByDescending(g => g.Max(c => c.CommittedAt))
            .First();

        return family.OrderByDescending(c => c.CommittedAt).First().Name;
    }

    /// <summary>
    /// The leading word of a branch name, after any remote prefix: "origin/dev-01-09-2026" gives
    /// "dev" and "origin/main-03-09-2026-updated" gives "main". A name that is all letters is its
    /// own family, which is how a stub like "mainTraining" stays separate from "main".
    /// </summary>
    public static string Family(string name)
    {
        var slash = name.LastIndexOf('/');
        var branch = slash >= 0 ? name[(slash + 1)..] : name;

        var end = 0;
        while (end < branch.Length && char.IsLetter(branch[end])) end++;

        return end == 0 ? branch : branch[..end];
    }
}
