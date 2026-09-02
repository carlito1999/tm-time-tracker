using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;
using TmTimeTracker.Services;
using TmTimeTracker.Tests.Data;
using Xunit;

namespace TmTimeTracker.Tests.Services;

public class JiraPollServiceTests
{
    private sealed class CapturingBus : IEventBus
    {
        public List<DomainEvent> Published { get; } = new();

        public ValueTask PublishAsync(DomainEvent evt, CancellationToken ct = default)
        {
            Published.Add(evt);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<DomainEvent> Subscribe(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow { get; } = new(2026, 9, 2, 11, 31, 0, DateTimeKind.Utc);
        public DateTimeOffset LocalNow => new(UtcNow.ToLocalTime());
    }

    private static Issue IssueWith(string status, string? summary) =>
        new("SN-296", new IssueFields(
            new IssueStatus(status, new StatusCategory("indeterminate", "In Progress")), summary));

    private static (JiraPollService Service, CapturingBus Bus) Build(
        ISqliteConnectionFactory factory, Issue issue)
    {
        var api = new Mock<IJiraIssueSource>();
        api.Setup(a => a.GetIssueAsync("SN-296", It.IsAny<CancellationToken>())).ReturnsAsync(issue);

        var bus = new CapturingBus();
        var service = new JiraPollService(
            new TicketTimeRepository(factory), api.Object, new ConfigRepository(factory),
            bus, new FixedClock(), NullLogger<JiraPollService>.Instance, new PollServiceGate());

        return (service, bus);
    }

    private static ISqliteConnectionFactory SeedCycle(int minutes, string lastSeenStatus)
    {
        var factory = SharedSqlite.NewInMemory();
        new DatabaseInitializer(factory).EnsureCreated();

        var tickets = new TicketTimeRepository(factory);
        var cycle = tickets.OpenOrCreateCycle("SN-296",
            new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc));
        for (var i = 0; i < minutes; i++) tickets.IncrementMinute(cycle.Id);
        tickets.UpdateStatusSnapshot(cycle.Id, lastSeenStatus,
            new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc));
        return factory;
    }

    [Fact]
    public async Task Transition_event_carries_summary_and_tracked_minutes()
    {
        var factory = SeedCycle(minutes: 3, lastSeenStatus: "In Progress");
        var (service, bus) = Build(factory, IssueWith("Review", "Fix Tolgee warning"));

        await service.PollOnce("Review", CancellationToken.None);

        var evt = bus.Published.OfType<JiraStatusTransition>().Single();
        evt.TicketKey.Should().Be("SN-296");
        evt.Summary.Should().Be("Fix Tolgee warning");
        evt.FromStatus.Should().Be("In Progress");
        evt.ToStatus.Should().Be("Review");
        evt.MinutesActive.Should().Be(3);
    }

    [Fact]
    public async Task Null_summary_is_carried_through_rather_than_failing()
    {
        var factory = SeedCycle(minutes: 1, lastSeenStatus: "In Progress");
        var (service, bus) = Build(factory, IssueWith("Review", null));

        await service.PollOnce("Review", CancellationToken.None);

        bus.Published.OfType<JiraStatusTransition>().Single().Summary.Should().BeNull();
    }

    // The edge-trigger is what removes any need for de-duplication downstream.
    [Fact]
    public async Task No_event_when_the_status_was_already_the_target()
    {
        var factory = SeedCycle(minutes: 5, lastSeenStatus: "Review");
        var (service, bus) = Build(factory, IssueWith("Review", "Already there"));

        await service.PollOnce("Review", CancellationToken.None);

        bus.Published.OfType<JiraStatusTransition>().Should().BeEmpty();
    }
}
