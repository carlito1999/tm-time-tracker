using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

/// <summary>
/// The tracked repos cut trunk snapshots named with a European date - main-03-09-2026,
/// dev-01-09-2026, dev-27-08-2026. That date is the team's own ordering and is the thing to
/// sort on: commit dates disagree with it in practice (dev-31-08-2026 was last committed on
/// the 30th, dev-07-07-2026 back in June), and a hotfix pushed to an old snapshot would make
/// commit-date ordering pick a stale branch.
/// </summary>
public class TrunkBranchSelectorTests
{
    private static BranchCandidate B(string name, string committed) =>
        new(name, DateTime.Parse(committed));

    [Fact]
    public void Picks_the_branch_whose_name_carries_the_latest_date()
    {
        var picked = TrunkBranchSelector.Select(new[]
        {
            B("origin/dev-01-09-2026", "2026-09-01"),
            B("origin/dev-31-08-2026", "2026-08-30"),
            B("origin/dev-06-08-2026", "2026-08-03")
        });

        picked.Should().Be("origin/dev-01-09-2026");
    }

    // The date is European: 01-09-2026 is September, and must beat 03-01-2026 in January.
    [Fact]
    public void Reads_the_date_as_day_month_year()
    {
        var picked = TrunkBranchSelector.Select(new[]
        {
            B("origin/dev-03-01-2026", "2026-01-03"),
            B("origin/dev-01-09-2026", "2026-09-01")
        });

        picked.Should().Be("origin/dev-01-09-2026");
    }

    // The live case: a hotfix pushed to an old snapshot gives it the newest commit date, but it
    // is still an old snapshot.
    [Fact]
    public void Ignores_a_late_commit_on_an_older_snapshot()
    {
        var picked = TrunkBranchSelector.Select(new[]
        {
            B("origin/dev-06-08-2026", "2026-09-03"),
            B("origin/dev-01-09-2026", "2026-09-01")
        });

        picked.Should().Be("origin/dev-01-09-2026");
    }

    // Real branch: "main-03-09-2026-updated" sits alongside "main-03-09-2026".
    [Fact]
    public void Breaks_a_tie_on_the_same_name_date_by_commit_date()
    {
        var picked = TrunkBranchSelector.Select(new[]
        {
            B("origin/main-03-09-2026", "2026-09-02"),
            B("origin/main-03-09-2026-updated", "2026-09-03")
        });

        picked.Should().Be("origin/main-03-09-2026-updated");
    }

    // A repo with a plain trunk and no dated snapshots.
    [Fact]
    public void Falls_back_to_commit_date_when_no_name_carries_a_date()
    {
        var picked = TrunkBranchSelector.Select(new[]
        {
            B("origin/master", "2026-09-03"),
            B("origin/main", "2026-08-20")
        });

        picked.Should().Be("origin/master");
    }

    /// <summary>
    /// A dated snapshot is the deliberate trunk marker, so it wins over an undated branch even
    /// when the undated one was committed to more recently - that is usually a long-lived stub
    /// like the "mainTraining" branch that started all this.
    /// </summary>
    [Fact]
    public void Prefers_a_dated_snapshot_over_an_undated_branch()
    {
        var picked = TrunkBranchSelector.Select(new[]
        {
            B("origin/mainTraining", "2026-09-03"),
            B("origin/dev-01-09-2026", "2026-09-01")
        });

        picked.Should().Be("origin/dev-01-09-2026");
    }

    [Fact]
    public void Rejects_an_impossible_date_in_a_name()
    {
        var picked = TrunkBranchSelector.Select(new[]
        {
            B("origin/dev-45-99-2026", "2026-09-03"),
            B("origin/dev-01-09-2026", "2026-09-01")
        });

        picked.Should().Be("origin/dev-01-09-2026");
    }

    [Fact]
    public void Accepts_a_two_digit_year()
    {
        var picked = TrunkBranchSelector.Select(new[]
        {
            B("origin/dev-01-09-26", "2026-09-01"),
            B("origin/dev-06-08-26", "2026-08-06")
        });

        picked.Should().Be("origin/dev-01-09-26");
    }

    [Fact]
    public void Returns_null_when_there_is_nothing_to_choose_from()
    {
        TrunkBranchSelector.Select(Array.Empty<BranchCandidate>()).Should().BeNull();
    }
}
