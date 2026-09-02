using System.Globalization;

namespace TmTimeTracker.Logic;

/// <summary>A pull request as reported by Jira's development information, before interpretation.</summary>
public sealed record PullRequestCandidate(
    string? Id,
    string? Title,
    string? Status,
    string? Url,
    string? RepositoryName,
    string? LastUpdate);

public static class PullRequestSelector
{
    /// <summary>
    /// Picks the pull request a notification should link to: an OPEN one if any exist, otherwise
    /// the most recent of whatever remains. Within either group the most recently updated wins.
    ///
    /// Preferring OPEN matters because a ticket that has been reworked carries its old merged or
    /// declined pull requests forever, and linking to a declined one would be actively misleading.
    /// </summary>
    public static PullRequestCandidate? Best(IEnumerable<PullRequestCandidate>? candidates)
    {
        var usable = (candidates ?? Enumerable.Empty<PullRequestCandidate>())
            .Where(c => !string.IsNullOrWhiteSpace(c.Url))
            .ToList();
        if (usable.Count == 0) return null;

        var open = usable
            .Where(c => string.Equals(c.Status, "OPEN", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return (open.Count > 0 ? open : usable)
            .OrderByDescending(UpdatedAt)
            .First();
    }

    private static DateTimeOffset UpdatedAt(PullRequestCandidate candidate) =>
        DateTimeOffset.TryParse(candidate.LastUpdate, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
}
