using System.Globalization;
using System.Text.RegularExpressions;

namespace TmTimeTracker.Logic;

/// <summary>A pull request as reported by the host, before interpretation.</summary>
public sealed record PullRequestCandidate(
    string? Id,
    string? Title,
    string? Status,
    string? Url,
    string? RepositoryName,
    string? LastUpdate,
    string? SourceBranch = null);

public static class PullRequestSelector
{
    /// <summary>
    /// Picks the pull request a notification should link to: the newest one belonging to this
    /// ticket, preferring an OPEN one.
    ///
    /// "Newest" is the highest id, not the most recent update. Bitbucket ids are monotonic per
    /// repository, so the freshest pull request always carries the biggest number, whereas
    /// lastUpdate moves whenever anyone comments. Ordering by lastUpdate is how SN-291 announced
    /// pull request 353 on 2026-09-04: a push to the superseded 353 had bumped it above 367.
    ///
    /// Candidates that do not reference the ticket are dropped rather than ranked. Jira's
    /// dev-status returns other tickets' pull requests under an issue - SN-246's 297 and SN-295's
    /// 358 both came back under SN-291 - and one of those held the newest timestamp in the whole
    /// payload. Reporting nothing lets the caller warn the user; reporting a stranger's pull
    /// request quietly posts the wrong link.
    /// </summary>
    public static PullRequestCandidate? Best(
        IEnumerable<PullRequestCandidate>? candidates, string ticketKey)
    {
        var usable = (candidates ?? Enumerable.Empty<PullRequestCandidate>())
            .Where(c => !string.IsNullOrWhiteSpace(c.Url))
            .Where(c => References(c, ticketKey))
            .ToList();
        if (usable.Count == 0) return null;

        var open = usable
            .Where(c => string.Equals(c.Status, "OPEN", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return (open.Count > 0 ? open : usable)
            .OrderByDescending(Number)
            .ThenByDescending(UpdatedAt)
            .First();
    }

    /// <summary>
    /// Whether a pull request belongs to this ticket, by its source branch or its title.
    ///
    /// The boundaries matter: without the trailing guard, SN-29 matches branch
    /// SN-291-350-individual-breeders and every ticket becomes a prefix of its own successors.
    /// </summary>
    private static bool References(PullRequestCandidate candidate, string ticketKey)
    {
        if (string.IsNullOrWhiteSpace(ticketKey)) return false;

        var pattern = $@"(?<![A-Za-z0-9]){Regex.Escape(ticketKey)}(?![0-9])";
        return Mentions(candidate.SourceBranch) || Mentions(candidate.Title);

        bool Mentions(string? text) =>
            !string.IsNullOrWhiteSpace(text) &&
            Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Non-numeric ids sort below every numeric one rather than winning by accident. Bitbucket
    /// always sends a number; only the dev-status fallback can produce anything else.
    /// </summary>
    private static long Number(PullRequestCandidate candidate) =>
        long.TryParse(candidate.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : long.MinValue;

    private static DateTimeOffset UpdatedAt(PullRequestCandidate candidate) =>
        DateTimeOffset.TryParse(candidate.LastUpdate, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
}
