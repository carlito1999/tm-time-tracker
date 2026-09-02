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

    private static (ReviewSlackNotifier Notifier, Mock<ISlackPoster> Poster) Build(
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

        var notifier = new ReviewSlackNotifier(
            new Mock<IEventBus>().Object, channels, poster.Object, resolver.Object,
            pullRequests.Object, NullLogger<ReviewSlackNotifier>.Instance);

        return (notifier, poster);
    }

    [Fact]
    public async Task Posts_the_rendered_template_to_the_mapped_channel()
    {
        var (notifier, poster) = Build();

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
        var (notifier, poster) = Build(siteUrl: null);

        await notifier.HandleAsync(Event(), CancellationToken.None);

        poster.Verify(p => p.PostMessageAsync("C1", "Done SN-296 2h 17m {URL}",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Missing_summary_leaves_that_placeholder_literal_too()
    {
        var (notifier, poster) = Build(template: "{TICKET}: {SUMMARY}");

        await notifier.HandleAsync(Event(summary: null), CancellationToken.None);

        poster.Verify(p => p.PostMessageAsync("C1", "SN-296: {SUMMARY}",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Silent_when_the_project_has_no_channel()
    {
        var (notifier, poster) = Build(configured: false);

        await notifier.HandleAsync(Event(), CancellationToken.None);

        poster.Verify(p => p.PostMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Silent_when_the_ticket_key_is_malformed()
    {
        var (notifier, poster) = Build();

        await notifier.HandleAsync(Event("garbage"), CancellationToken.None);

        poster.Verify(p => p.PostMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_unmapped_project_does_not_borrow_another_projects_channel()
    {
        var (notifier, poster) = Build();

        await notifier.HandleAsync(Event("TM-51"), CancellationToken.None);

        poster.Verify(p => p.PostMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Substitutes_pull_request_variables_when_a_pr_is_linked()
    {
        var (notifier, poster) = Build(
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

    // The dev-status endpoint is undocumented, so "no PR" must be an ordinary outcome:
    // the message still sends and the placeholder stays visible.
    [Fact]
    public async Task Leaves_pr_placeholders_literal_when_no_pull_request_is_linked()
    {
        var (notifier, poster) = Build(template: "{TICKET} {PR_URL}", pullRequest: null);

        await notifier.HandleAsync(Event(), CancellationToken.None);

        poster.Verify(p => p.PostMessageAsync("C1", "SN-296 {PR_URL}",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // Notifications are a side-channel and must never disturb time tracking.
    [Fact]
    public async Task A_failing_slack_call_does_not_propagate()
    {
        var (notifier, poster) = Build();
        poster.Setup(p => p.PostMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
              .ThrowsAsync(new SlackApiException("channel_not_found"));

        var act = () => notifier.HandleAsync(Event(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
