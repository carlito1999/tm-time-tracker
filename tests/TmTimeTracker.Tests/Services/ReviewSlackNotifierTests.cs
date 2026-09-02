using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TmTimeTracker.Data;
using TmTimeTracker.Services;
using TmTimeTracker.Slack;
using TmTimeTracker.Tests.Data;
using Xunit;

namespace TmTimeTracker.Tests.Services;

public class ReviewSlackNotifierTests
{
    private static JiraStatusTransition Event(string ticket = "SN-296", string? summary = "Fix Tolgee warning") =>
        new(ticket, summary, "In Progress", "Review", 137,
            new DateTime(2026, 9, 2, 11, 31, 0, DateTimeKind.Utc), IssueId: "28814");

    private sealed class MutableClock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 9, 2, 11, 31, 0, DateTimeKind.Utc);
        public DateTimeOffset LocalNow => new(UtcNow.ToLocalTime());
    }

    private static (ReviewSlackNotifier Notifier, Mock<ISlackPoster> Poster,
                    Mock<IPullRequestSource> PullRequests, MutableClock Clock) Build(
        string? siteUrl = "https://tcubeee.atlassian.net",
        bool configured = true,
        string template = "Done {TICKET} {HOURS} {URL}",
        PullRequestInfo? pullRequest = null)
    {
        var factory = SharedSqlite.NewInMemory();
        new DatabaseInitializer(factory).EnsureCreated();

        var channels = new SlackChannelRepository(factory);
        if (configured)
            channels.Upsert(new SlackChannelMapping("SN", "C1", "sheeponline", template));

        var poster = new Mock<ISlackPoster>();
        var resolver = new Mock<IJiraSiteResolver>();
        resolver.Setup(r => r.GetSiteUrlAsync(It.IsAny<CancellationToken>())).ReturnsAsync(siteUrl);

        var pullRequests = new Mock<IPullRequestSource>();
        pullRequests.Setup(p => p.GetBestAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(pullRequest);

        var clock = new MutableClock();

        var notifier = new ReviewSlackNotifier(
            new Mock<IEventBus>().Object, channels, poster.Object, resolver.Object,
            pullRequests.Object, clock, NullLogger<ReviewSlackNotifier>.Instance);

        return (notifier, poster, pullRequests, clock);
    }

    [Fact]
    public async Task Posts_the_rendered_template_to_the_mapped_channel()
    {
        var (notifier, poster, _, _) = Build();

        await notifier.HandleAsync(Event(), CancellationToken.None);

        poster.Verify(p => p.PostMessageAsync("C1",
            "Done SN-296 2h 17m https://tcubeee.atlassian.net/browse/SN-296",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // Spec 6.2: an unavailable value is omitted, so the placeholder stays visible
    // instead of leaving a blank that reads as a formatting bug.
    [Fact]
    public async Task Unresolved_site_url_leaves_the_placeholder_literal()
    {
        var (notifier, poster, _, _) = Build(siteUrl: null);

        await notifier.HandleAsync(Event(), CancellationToken.None);

        poster.Verify(p => p.PostMessageAsync("C1", "Done SN-296 2h 17m {URL}",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Missing_summary_leaves_that_placeholder_literal_too()
    {
        var (notifier, poster, _, _) = Build(template: "{TICKET}: {SUMMARY}");

        await notifier.HandleAsync(Event(summary: null), CancellationToken.None);

        poster.Verify(p => p.PostMessageAsync("C1", "SN-296: {SUMMARY}",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Silent_when_the_project_has_no_channel()
    {
        var (notifier, poster, _, _) = Build(configured: false);

        await notifier.HandleAsync(Event(), CancellationToken.None);

        poster.Verify(p => p.PostMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Silent_when_the_ticket_key_is_malformed()
    {
        var (notifier, poster, _, _) = Build();

        await notifier.HandleAsync(Event("garbage"), CancellationToken.None);

        poster.Verify(p => p.PostMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_unmapped_project_does_not_borrow_another_projects_channel()
    {
        var (notifier, poster, _, _) = Build();

        await notifier.HandleAsync(Event("TM-51"), CancellationToken.None);

        poster.Verify(p => p.PostMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Substitutes_pull_request_variables_when_a_pr_is_linked()
    {
        var (notifier, poster, _, _) = Build(
            template: "{TICKET} {PR_STATUS} {PR_TITLE} {PR_URL}",
            pullRequest: new PullRequestInfo(
                "https://bitbucket.org/thecubeee/sheeponline-new/pull-requests/360",
                "SN-296-372: enhance Tolgee caching", "OPEN"));

        await notifier.HandleAsync(Event(), CancellationToken.None);

        poster.Verify(p => p.PostMessageAsync("C1",
            "SN-296 OPEN SN-296-372: enhance Tolgee caching " +
            "https://bitbucket.org/thecubeee/sheeponline-new/pull-requests/360",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // Jira ingests a pull request moments after it is opened, so a ticket dragged to Review
    // promptly would otherwise always lose the race and post a literal {PR_URL}.
    [Fact]
    public async Task Holds_the_notification_when_the_template_wants_a_pr_and_none_is_known_yet()
    {
        var (notifier, poster, _, _) = Build(template: "{TICKET} {PR_URL}", pullRequest: null);

        var outcome = await notifier.HandleAsync(Event(), CancellationToken.None);

        outcome.Should().Be(NotificationOutcome.Deferred);
        notifier.PendingCount.Should().Be(1);
        poster.Verify(p => p.PostMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Sends_the_held_notification_once_the_pull_request_appears()
    {
        var (notifier, poster, pullRequests, _) =
            Build(template: "{TICKET} {PR_URL}", pullRequest: null);

        await notifier.HandleAsync(Event(), CancellationToken.None);

        pullRequests.Setup(p => p.GetBestAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new PullRequestInfo(
                        "https://bitbucket.org/thecubeee/sheeponline-new/pull-requests/361",
                        "SN-279-357: refactor user matching", "OPEN"));

        var outcome = await notifier.HandleAsync(Event(), CancellationToken.None);

        outcome.Should().Be(NotificationOutcome.Sent);
        notifier.PendingCount.Should().Be(0);
        poster.Verify(p => p.PostMessageAsync("C1",
            "SN-296 https://bitbucket.org/thecubeee/sheeponline-new/pull-requests/361",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // Holding forever would silently swallow every ticket that legitimately has no PR.
    [Fact]
    public async Task Gives_up_after_the_maximum_wait_rather_than_holding_forever()
    {
        var (notifier, poster, _, clock) =
            Build(template: "{TICKET} {PR_URL}", pullRequest: null);

        await notifier.HandleAsync(Event(), CancellationToken.None);
        clock.UtcNow = clock.UtcNow.AddMinutes(11);

        var outcome = await notifier.HandleAsync(Event(), CancellationToken.None);

        outcome.Should().Be(NotificationOutcome.Expired);
        notifier.PendingCount.Should().Be(0);
        poster.Verify(p => p.PostMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // The gate must only apply to templates that actually ask for pull-request data.
    [Fact]
    public async Task Sends_immediately_when_the_template_does_not_mention_a_pull_request()
    {
        var (notifier, poster, _, _) = Build(template: "{TICKET} done", pullRequest: null);

        var outcome = await notifier.HandleAsync(Event(), CancellationToken.None);

        outcome.Should().Be(NotificationOutcome.Sent);
        poster.Verify(p => p.PostMessageAsync("C1", "SN-296 done",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // Notifications are a side-channel and must never disturb time tracking.
    [Fact]
    public async Task A_failing_slack_call_does_not_propagate()
    {
        var (notifier, poster, _, _) = Build();
        poster.Setup(p => p.PostMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
              .ThrowsAsync(new SlackApiException("channel_not_found"));

        var act = () => notifier.HandleAsync(Event(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
