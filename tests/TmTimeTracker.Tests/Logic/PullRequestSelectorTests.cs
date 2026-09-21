using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class PullRequestSelectorTests
{
    private const string Ticket = "SN-291";
    private const string Branch = "SN-291-350-individual-breeders";

    private static PullRequestCandidate Pr(
        string id,
        string status,
        string lastUpdate,
        string? branch = Branch,
        string? title = null,
        string? url = "https://bitbucket.org/x/y/pull-requests/1") =>
        new(id, title ?? $"PR {id}", status, url, "sheeponline-new", lastUpdate, branch);

    // The bug this whole selector was rewritten for. On 2026-09-04 all three of these were OPEN at
    // once - the branch is re-PR'd against each new main-DD-MM-YYYY snapshot, so the old pull
    // requests never close - and 353 was announced because it had been commented on most recently.
    [Fact]
    public void Prefers_the_highest_id_when_a_branch_has_several_open_pull_requests()
    {
        var best = PullRequestSelector.Best(new[]
        {
            Pr("352", "OPEN", "2026-09-04T09:00:00.000+0000"),
            Pr("353", "OPEN", "2026-09-04T08:00:00.000+0000"),
            Pr("367", "OPEN", "2026-09-03T07:00:00.000+0000"),
        }, Ticket);

        best!.Id.Should().Be("367");
    }

    // Jira's dev-status returns pull requests from other tickets under this issue: PR 297 on
    // branch SN-246-273-notifs-&-email came back under SN-291, with the newest timestamp of the
    // whole payload. Announcing another ticket's pull request is worse than announcing nothing.
    [Fact]
    public void Ignores_pull_requests_belonging_to_another_ticket()
    {
        var best = PullRequestSelector.Best(new[]
        {
            Pr("999", "OPEN", "2026-09-04T09:00:00.000+0000", branch: "SN-246-273-notifs-&-email"),
            Pr("367", "OPEN", "2026-09-04T05:00:00.000+0000"),
        }, Ticket);

        best!.Id.Should().Be("367");
    }

    [Fact]
    public void Matches_the_ticket_key_only_on_a_boundary()
    {
        PullRequestSelector.Best(new[] { Pr("367", "OPEN", "2026-09-04T05:00:00.000+0000") }, "SN-29")
            .Should().BeNull();
    }

    [Fact]
    public void Matches_the_ticket_key_in_the_title_when_no_branch_is_known()
    {
        var best = PullRequestSelector.Best(new[]
        {
            Pr("367", "OPEN", "2026-09-04T05:00:00.000+0000",
               branch: null, title: "SN-291 350 individual breeders"),
        }, Ticket);

        best!.Id.Should().Be("367");
    }

    // Silence plus the worker's urgent toast beats a confident wrong link. Falling back to the
    // unfiltered list here is exactly what would let another ticket's pull request through.
    [Fact]
    public void Returns_null_when_nothing_references_the_ticket()
    {
        PullRequestSelector.Best(new[]
        {
            Pr("999", "OPEN", "2026-09-04T09:00:00.000+0000", branch: "SN-246-273-notifs-&-email"),
        }, Ticket).Should().BeNull();
    }

    // A reworked ticket keeps its old merged and declined PRs forever, and those can carry a
    // higher id than the live one when the rework was PR'd first.
    [Fact]
    public void Prefers_an_open_pull_request_over_a_merged_one_with_a_higher_id()
    {
        var best = PullRequestSelector.Best(new[]
        {
            Pr("360", "OPEN",   "2026-09-01T09:00:00.000+0000"),
            Pr("361", "MERGED", "2026-09-02T09:00:00.000+0000"),
        }, Ticket);

        best!.Id.Should().Be("360");
    }

    [Fact]
    public void Falls_back_to_the_highest_id_when_none_are_open()
    {
        var best = PullRequestSelector.Best(new[]
        {
            Pr("356", "DECLINED", "2026-09-02T09:00:00.000+0000"),
            Pr("355", "MERGED",   "2026-09-01T09:00:00.000+0000"),
        }, Ticket);

        best!.Id.Should().Be("356");
    }

    [Fact]
    public void Status_match_is_case_insensitive()
    {
        PullRequestSelector.Best(new[]
        {
            Pr("360", "open",   "2026-09-01T09:00:00.000+0000"),
            Pr("361", "MERGED", "2026-09-02T09:00:00.000+0000"),
        }, Ticket)!.Id.Should().Be("360");
    }

    [Fact]
    public void Ignores_candidates_without_a_url()
    {
        var best = PullRequestSelector.Best(new[]
        {
            Pr("361", "OPEN",   "2026-09-03T09:00:00.000+0000", url: null),
            Pr("355", "MERGED", "2026-09-01T09:00:00.000+0000"),
        }, Ticket);

        best!.Id.Should().Be("355");
    }

    // Bitbucket ids are always numeric; this only guards the dev-status fallback path, where the
    // id is whatever Jira decided to put in the field.
    [Fact]
    public void Falls_back_to_the_newest_update_when_ids_are_not_numeric()
    {
        var best = PullRequestSelector.Best(new[]
        {
            Pr("#a", "OPEN", "2026-09-01T09:00:00.000+0000", title: "SN-291 one"),
            Pr("#b", "OPEN", "2026-09-02T09:00:00.000+0000", title: "SN-291 two"),
        }, Ticket);

        best!.Id.Should().Be("#b");
    }

    [Fact]
    public void A_numeric_id_beats_a_non_numeric_one()
    {
        var best = PullRequestSelector.Best(new[]
        {
            Pr("#a", "OPEN", "2026-09-09T09:00:00.000+0000", title: "SN-291 one"),
            Pr("12", "OPEN", "2026-09-01T09:00:00.000+0000"),
        }, Ticket);

        best!.Id.Should().Be("12");
    }

    [Fact]
    public void Returns_null_for_no_candidates()
    {
        PullRequestSelector.Best(Array.Empty<PullRequestCandidate>(), Ticket).Should().BeNull();
        PullRequestSelector.Best(null, Ticket).Should().BeNull();
    }
}
