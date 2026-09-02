using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class PullRequestSelectorTests
{
    private static PullRequestCandidate Pr(
        string id, string status, string lastUpdate, string? url = "https://bitbucket.org/x/y/pull-requests/1") =>
        new(id, $"PR {id}", status, url, "sheeponline-new", lastUpdate);

    // A reworked ticket keeps its old merged and declined PRs forever; linking to one
    // of those instead of the live PR would be actively misleading.
    [Fact]
    public void Prefers_an_open_pull_request_over_a_newer_merged_one()
    {
        var best = PullRequestSelector.Best(new[]
        {
            Pr("360", "OPEN",   "2026-09-01T09:00:00.000+0000"),
            Pr("355", "MERGED", "2026-09-02T09:00:00.000+0000"),
        });

        best!.Id.Should().Be("360");
    }

    [Fact]
    public void Picks_the_most_recently_updated_open_pull_request()
    {
        var best = PullRequestSelector.Best(new[]
        {
            Pr("360", "OPEN", "2026-09-01T09:00:00.000+0000"),
            Pr("361", "OPEN", "2026-09-02T09:00:00.000+0000"),
        });

        best!.Id.Should().Be("361");
    }

    [Fact]
    public void Falls_back_to_the_most_recent_when_none_are_open()
    {
        var best = PullRequestSelector.Best(new[]
        {
            Pr("355", "MERGED",   "2026-09-01T09:00:00.000+0000"),
            Pr("356", "DECLINED", "2026-09-02T09:00:00.000+0000"),
        });

        best!.Id.Should().Be("356");
    }

    [Fact]
    public void Status_match_is_case_insensitive()
    {
        PullRequestSelector.Best(new[]
        {
            Pr("360", "open",   "2026-09-01T09:00:00.000+0000"),
            Pr("355", "MERGED", "2026-09-02T09:00:00.000+0000"),
        })!.Id.Should().Be("360");
    }

    [Fact]
    public void Ignores_candidates_without_a_url()
    {
        var best = PullRequestSelector.Best(new[]
        {
            Pr("360", "OPEN", "2026-09-03T09:00:00.000+0000", url: null),
            Pr("355", "MERGED", "2026-09-01T09:00:00.000+0000"),
        });

        best!.Id.Should().Be("355");
    }

    [Fact]
    public void Unparseable_timestamps_lose_to_parseable_ones()
    {
        PullRequestSelector.Best(new[]
        {
            Pr("360", "OPEN", "not-a-date"),
            Pr("361", "OPEN", "2026-09-02T09:00:00.000+0000"),
        })!.Id.Should().Be("361");
    }

    [Fact]
    public void Returns_null_for_no_candidates()
    {
        PullRequestSelector.Best(Array.Empty<PullRequestCandidate>()).Should().BeNull();
        PullRequestSelector.Best(null).Should().BeNull();
    }
}
