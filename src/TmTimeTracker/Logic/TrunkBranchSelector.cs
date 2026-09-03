using System.Globalization;
using System.Text.RegularExpressions;

namespace TmTimeTracker.Logic;

/// <param name="Name">Short remote ref, e.g. "origin/dev-01-09-2026".</param>
/// <param name="CommittedAt">Last commit on the branch.</param>
public sealed record BranchCandidate(string Name, DateTime CommittedAt);

/// <summary>
/// Chooses which trunk branch a repo's estimates should run against.
///
/// The tracked repos cut trunk snapshots stamped with a European date - main-03-09-2026,
/// dev-01-09-2026, dev-27-08-2026. That date is the team's own ordering, and it is what to sort
/// on rather than the commit date. The two genuinely disagree: dev-31-08-2026 was last committed
/// on the 30th and dev-07-07-2026 back in June. Worse, a hotfix pushed to an old snapshot gives
/// it the newest commit date, which would make commit-date ordering select a stale branch.
///
/// A dated snapshot also beats an undated branch outright, because an undated long-lived branch
/// is usually a stub - the "mainTraining" branch that motivated all this holds one .gitignore
/// and would otherwise win on recency.
///
/// Repos with no dated snapshots (a plain master or main) fall back to commit date, which is the
/// right answer when there is only one trunk.
/// </summary>
public static class TrunkBranchSelector
{
    // Four-digit year first: "01-09-2026" would otherwise match the two-digit form as "01-09-20".
    private static readonly Regex FourDigitYear =
        new(@"(?<!\d)(\d{2})-(\d{2})-(\d{4})(?!\d)", RegexOptions.Compiled);

    private static readonly Regex TwoDigitYear =
        new(@"(?<!\d)(\d{2})-(\d{2})-(\d{2})(?!\d)", RegexOptions.Compiled);

    public static string? Select(IReadOnlyList<BranchCandidate> candidates)
    {
        if (candidates.Count == 0) return null;

        var dated = candidates
            .Select(c => (Candidate: c, Stamp: DateInName(c.Name)))
            .Where(x => x.Stamp is not null)
            .ToList();

        if (dated.Count > 0)
            return dated
                .OrderByDescending(x => x.Stamp!.Value)
                .ThenByDescending(x => x.Candidate.CommittedAt)
                .First().Candidate.Name;

        return candidates.OrderByDescending(c => c.CommittedAt).First().Name;
    }

    /// <summary>
    /// The DD-MM-YYYY stamp inside a branch name, or null when there isn't a valid one. The date
    /// need not end the name: "main-03-09-2026-updated" is a real branch.
    /// </summary>
    public static DateTime? DateInName(string name)
    {
        return Parse(FourDigitYear.Match(name), century: 0)
            ?? Parse(TwoDigitYear.Match(name), century: 2000);
    }

    private static DateTime? Parse(Match match, int century)
    {
        if (!match.Success) return null;

        var day = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var year = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) + century;

        // Rejects the likes of dev-45-99-2026 rather than letting a nonsense name win.
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
            return null;

        return new DateTime(year, month, day);
    }
}
