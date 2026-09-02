using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;
using TmTimeTracker.Services;
using TmTimeTracker.Slack;
using TmTimeTracker.Tests.Data;
using TmTimeTracker.UI;
using Xunit;

namespace TmTimeTracker.Tests.Services;

public class PrAnnouncementWorkerTests
{
    private const string PrUrl = "https://bitbucket.org/thecubeee/sheeponline-new/pull-requests/362";

    private sealed class MutableClock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
        public DateTimeOffset LocalNow => new(UtcNow.ToLocalTime());
    }

    private sealed class Harness
    {
        public required PrAnnouncementWorker Worker { get; init; }
        public required PrAnnouncementRepository Announcements { get; init; }
        public required TicketTimeRepository Tickets { get; init; }
        public required Mock<ISlackPoster> Slack { get; init; }
        public required Mock<IUserNotifier> Notifier { get; init; }
        public required Mock<IPullRequestSource> PullRequests { get; init; }
        public required MutableClock Clock { get; init; }
    }

    private static Harness Build(
        DevInfoSnapshot? snapshot = null,
        string template = "{TICKET} {PR_URL}",
        bool channelConfigured = true)
    {
        var factory = SharedSqlite.NewInMemory();
        new DatabaseInitializer(factory).EnsureCreated();

        var config = new ConfigRepository(factory);
        config.SetIfMissing(new AppConfig(600, 90, "repo", "remember", "In Progress", "Review"));

        var channels = new SlackChannelRepository(factory);
        if (channelConfigured)
            channels.Upsert(new SlackChannelMapping("SN", "C1", "sheeponline", template));

        var pullRequests = new Mock<IPullRequestSource>();
        pullRequests.Setup(p => p.GetSnapshotAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(snapshot ?? DevInfoSnapshot.Empty);

        var site = new Mock<IJiraSiteResolver>();
        site.Setup(s => s.GetSiteUrlAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://tcubeee.atlassian.net");

        var issues = new Mock<IJiraIssueSource>();
        issues.Setup(i => i.GetIssueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new Issue("SN-298",
                  new IssueFields(new IssueStatus("Review", new StatusCategory("indeterminate", "In Progress")),
                      "257 Less important but convenient"), "28848"));

        var slack = new Mock<ISlackPoster>();
        var notifier = new Mock<IUserNotifier>();
        var clock = new MutableClock();
        var announcements = new PrAnnouncementRepository(factory);
        var tickets = new TicketTimeRepository(factory);

        var worker = new PrAnnouncementWorker(
            new Mock<IEventBus>().Object, announcements, channels, tickets, config,
            issues.Object, pullRequests.Object, site.Object, slack.Object, notifier.Object,
            clock, NullLogger<PrAnnouncementWorker>.Instance);

        return new Harness
        {
            Worker = worker, Announcements = announcements, Tickets = tickets,
            Slack = slack, Notifier = notifier, PullRequests = pullRequests, Clock = clock
        };
    }

    private static void QueueTicket(Harness h, string ticket = "SN-298") =>
        h.Announcements.QueueIfMissing(new PrAnnouncement(
            ticket, "28848", "257 Less important but convenient", "In Progress", "Review",
            42, h.Clock.UtcNow, h.Clock.UtcNow, 0, null, null, null));

    private static void PutInReview(Harness h, string ticket = "SN-298", string status = "Review")
    {
        var cycle = h.Tickets.OpenOrCreateCycle(ticket, new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc));
        h.Tickets.UpdateStatusSnapshot(cycle.Id, status, new DateTime(2026, 9, 2, 9, 0, 0, DateTimeKind.Utc));
    }

    private static DevInfoSnapshot WithPr(DateTimeOffset? lastCommit = null) =>
        new(new PullRequestInfo(PrUrl, "SN-298-257: refactor", "OPEN"), lastCommit);

    [Fact]
    public async Task Announces_and_records_it_once_the_pull_request_is_known()
    {
        var h = Build(WithPr());
        QueueTicket(h);

        var outcome = await h.Worker.TryAnnounceAsync("SN-298", CancellationToken.None);

        outcome.Should().Be(AnnouncementOutcome.Announced);
        h.Slack.Verify(s => s.PostMessageAsync("C1", $"SN-298 {PrUrl}",
            It.IsAny<CancellationToken>()), Times.Once);
        h.Announcements.Find("SN-298")!.IsAnnounced.Should().BeTrue();
    }

    // The whole point of the durable queue: never announce the same ticket twice.
    [Fact]
    public async Task Does_not_announce_a_ticket_that_was_already_announced()
    {
        var h = Build(WithPr());
        QueueTicket(h);
        await h.Worker.TryAnnounceAsync("SN-298", CancellationToken.None);

        var outcome = await h.Worker.TryAnnounceAsync("SN-298", CancellationToken.None);

        outcome.Should().Be(AnnouncementOutcome.NotApplicable);
        h.Slack.Verify(s => s.PostMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Waits_instead_of_posting_when_the_pull_request_is_not_known_yet()
    {
        var h = Build(DevInfoSnapshot.Empty);
        QueueTicket(h);

        var outcome = await h.Worker.TryAnnounceAsync("SN-298", CancellationToken.None);

        outcome.Should().Be(AnnouncementOutcome.Waiting);
        h.Slack.Verify(s => s.PostMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        h.Announcements.Find("SN-298")!.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task Posts_immediately_when_the_template_never_mentions_a_pull_request()
    {
        var h = Build(DevInfoSnapshot.Empty, template: "{TICKET} moved to {TO}");
        QueueTicket(h);

        var outcome = await h.Worker.TryAnnounceAsync("SN-298", CancellationToken.None);

        outcome.Should().Be(AnnouncementOutcome.Announced);
        h.Slack.Verify(s => s.PostMessageAsync("C1", "SN-298 moved to Review",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Warns_when_no_pull_request_appears_within_ten_minutes_of_the_last_commit()
    {
        var lastCommit = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var h = Build(new DevInfoSnapshot(null, lastCommit));
        QueueTicket(h);
        h.Clock.UtcNow = lastCommit.UtcDateTime.AddMinutes(11);

        await h.Worker.TryAnnounceAsync("SN-298", CancellationToken.None);

        h.Notifier.Verify(b => b.Show(
            It.Is<string>(t => t.Contains("SN-298")), It.IsAny<string>(), true),
            Times.Once);
        h.Announcements.Find("SN-298")!.HasWarned.Should().BeTrue();
    }

    [Fact]
    public async Task Does_not_warn_before_the_deadline()
    {
        var lastCommit = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var h = Build(new DevInfoSnapshot(null, lastCommit));
        QueueTicket(h);
        h.Clock.UtcNow = lastCommit.UtcDateTime.AddMinutes(9);

        await h.Worker.TryAnnounceAsync("SN-298", CancellationToken.None);

        h.Notifier.Verify(b => b.Show(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);
    }

    // Polling continues after the warning, so the warning must not repeat every 30 seconds.
    [Fact]
    public async Task Warns_only_once_per_ticket()
    {
        var lastCommit = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var h = Build(new DevInfoSnapshot(null, lastCommit));
        QueueTicket(h);
        h.Clock.UtcNow = lastCommit.UtcDateTime.AddMinutes(11);

        await h.Worker.TryAnnounceAsync("SN-298", CancellationToken.None);
        h.Clock.UtcNow = h.Clock.UtcNow.AddMinutes(5);
        await h.Worker.TryAnnounceAsync("SN-298", CancellationToken.None);

        h.Notifier.Verify(b => b.Show(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Once);
    }

    [Fact]
    public async Task Keeps_waiting_when_the_last_commit_time_is_unknown()
    {
        var h = Build(DevInfoSnapshot.Empty);
        QueueTicket(h);
        h.Clock.UtcNow = h.Clock.UtcNow.AddHours(5);

        var outcome = await h.Worker.TryAnnounceAsync("SN-298", CancellationToken.None);

        outcome.Should().Be(AnnouncementOutcome.Waiting);
        h.Notifier.Verify(b => b.Show(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);
    }

    // Recovery path: a restart mid-wait must not lose the announcement.
    [Fact]
    public async Task Discovers_a_ticket_already_sitting_in_review()
    {
        var h = Build(WithPr());
        PutInReview(h);

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Slack.Verify(s => s.PostMessageAsync("C1", $"SN-298 {PrUrl}",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Forgets_a_ticket_that_has_left_review_so_it_can_announce_again_later()
    {
        var h = Build(WithPr());
        QueueTicket(h);
        PutInReview(h, status: "In Progress");

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Announcements.Find("SN-298").Should().BeNull();
    }

    [Fact]
    public async Task Drops_a_ticket_whose_project_has_no_channel_rather_than_polling_forever()
    {
        var h = Build(DevInfoSnapshot.Empty, channelConfigured: false);
        QueueTicket(h);

        var outcome = await h.Worker.TryAnnounceAsync("SN-298", CancellationToken.None);

        outcome.Should().Be(AnnouncementOutcome.NotApplicable);
        h.Announcements.Find("SN-298").Should().BeNull();
    }
}
