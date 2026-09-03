using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

/// <summary>
/// The tracked repos cut dated trunk snapshots - main-03-09-2026-updated, dev-01-09-2026,
/// dev-27-08-2026 - so the trunk is a convention, not a fixed name. The family a repo repeats
/// identifies it, and git's own commit dates order it.
/// </summary>
public class TrunkBranchSelectorTests
{
    private static BranchCandidate B(string name, string committed) =>
        new(name, DateTime.Parse(committed));

    [Fact]
    public void Picks_the_newest_branch_in_the_family()
    {
        var picked = TrunkBranchSelector.Select(new[]
        {
            B("origin/dev-06-08-2026", "2026-08-03"),
            B("origin/dev-01-09-2026", "2026-09-01"),
            B("origin/dev-31-08-2026", "2026-08-30")
        });

        picked.Should().Be("origin/dev-01-09-2026");
    }

    /// <summary>
    /// The live case. training-manager has a dozen dev-* snapshots plus "mainTraining", a stub
    /// holding one .gitignore that origin/HEAD points at. The repeated family identifies the
    /// real trunk, so the stub cannot win however recently it was touched.
    /// </summary>
    [Fact]
    public void Ignores_a_one_off_branch_when_another_family_repeats()
    {
        var picked = TrunkBranchSelector.Select(new[]
        {
            B("origin/mainTraining", "2026-09-03"),
            B("origin/dev-01-09-2026", "2026-09-01"),
            B("origin/dev-31-08-2026", "2026-08-30"),
            B("origin/dev-06-08-2026", "2026-08-03")
        });

        picked.Should().Be("origin/dev-01-09-2026");
    }

    // sheeponline-new: many main-* snapshots alongside a single old master.
    [Fact]
    public void Chooses_the_repeated_family_over_a_lone_traditional_trunk()
    {
        var picked = TrunkBranchSelector.Select(new[]
        {
            B("origin/master", "2026-06-01"),
            B("origin/main-03-09-2026-updated", "2026-09-03"),
            B("origin/main-03-09-2026", "2026-09-02"),
            B("origin/main-01-09-2026", "2026-09-01")
        });

        picked.Should().Be("origin/main-03-09-2026-updated");
    }

    // A repo with a single conventional trunk and no snapshots at all.
    [Fact]
    public void Copes_with_a_repo_that_has_one_plain_trunk()
    {
        TrunkBranchSelector.Select(new[] { B("origin/master", "2026-09-03") })
            .Should().Be("origin/master");
    }

    [Fact]
    public void Breaks_a_tie_between_equal_families_on_recency()
    {
        var picked = TrunkBranchSelector.Select(new[]
        {
            B("origin/main-01-01-2026", "2026-01-01"),
            B("origin/dev-01-09-2026", "2026-09-01")
        });

        picked.Should().Be("origin/dev-01-09-2026");
    }

    [Fact]
    public void Returns_null_when_there_is_nothing_to_choose_from()
    {
        TrunkBranchSelector.Select(Array.Empty<BranchCandidate>()).Should().BeNull();
    }

    // The split is at the first non-letter, which is what keeps the stub out of the main family.
    [Theory]
    [InlineData("origin/dev-01-09-2026", "dev")]
    [InlineData("origin/main-03-09-2026-updated", "main")]
    [InlineData("origin/master", "master")]
    [InlineData("origin/mainTraining", "mainTraining")]
    [InlineData("main", "main")]
    [InlineData("origin/2026-release", "2026-release")]
    public void Groups_branches_by_their_leading_word(string name, string family)
    {
        TrunkBranchSelector.Family(name).Should().Be(family);
    }
}
