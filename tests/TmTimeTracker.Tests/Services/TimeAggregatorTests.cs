using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using TmTimeTracker.Services;
using TmTimeTracker.Tests.Data;
using Xunit;

namespace TmTimeTracker.Tests.Services;

public class TimeAggregatorTests
{
    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 5, 25, 9, 0, 0, DateTimeKind.Utc);
        public DateTimeOffset LocalNow => new(UtcNow, TimeSpan.Zero);
    }

    private static (TimeAggregator agg, FakeClock clock, TicketTimeRepository tickets, EventBus bus) Build()
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        var tickets = new TicketTimeRepository(ds);
        var bus = new EventBus();
        var clock = new FakeClock();
        var agg = new TimeAggregator(bus, tickets, clock, NullLogger<TimeAggregator>.Instance);
        return (agg, clock, tickets, bus);
    }

    [Fact]
    public async Task Increments_when_active_and_has_ticket()
    {
        var (agg, _, tickets, _) = Build();
        agg.ProcessEvent(new ActivityChanged(UserActivityState.Active, DateTime.UtcNow));
        agg.ProcessEvent(new BranchChanged("TM-29-x", "TM-29", DateTime.UtcNow));

        await agg.TickAsync();

        var open = tickets.GetAllOpen().Should().ContainSingle().Subject;
        open.TicketKey.Should().Be("TM-29");
        open.MinutesActive.Should().Be(1);
    }

    [Fact]
    public async Task Does_not_increment_when_idle()
    {
        var (agg, _, tickets, _) = Build();
        agg.ProcessEvent(new ActivityChanged(UserActivityState.Idle, DateTime.UtcNow));
        agg.ProcessEvent(new BranchChanged("TM-29-x", "TM-29", DateTime.UtcNow));

        await agg.TickAsync();
        tickets.GetAllOpen().Should().BeEmpty();
    }

    [Fact]
    public async Task Does_not_increment_when_no_ticket()
    {
        var (agg, _, tickets, _) = Build();
        agg.ProcessEvent(new ActivityChanged(UserActivityState.Active, DateTime.UtcNow));
        agg.ProcessEvent(new BranchChanged("main", null, DateTime.UtcNow));

        await agg.TickAsync();
        tickets.GetAllOpen().Should().BeEmpty();
    }

    [Fact]
    public async Task Switching_branch_opens_new_cycle_for_new_ticket()
    {
        var (agg, _, tickets, _) = Build();
        agg.ProcessEvent(new ActivityChanged(UserActivityState.Active, DateTime.UtcNow));

        agg.ProcessEvent(new BranchChanged("feature/TM-29", "TM-29", DateTime.UtcNow));
        await agg.TickAsync();

        agg.ProcessEvent(new BranchChanged("feature/TM-30", "TM-30", DateTime.UtcNow));
        await agg.TickAsync();
        await agg.TickAsync();

        var open = tickets.GetAllOpen();
        open.Should().HaveCount(2);
        open.Single(c => c.TicketKey == "TM-29").MinutesActive.Should().Be(1);
        open.Single(c => c.TicketKey == "TM-30").MinutesActive.Should().Be(2);
    }

    [Fact]
    public async Task Switching_back_resumes_existing_cycle()
    {
        var (agg, _, tickets, _) = Build();
        agg.ProcessEvent(new ActivityChanged(UserActivityState.Active, DateTime.UtcNow));

        agg.ProcessEvent(new BranchChanged("TM-29-x", "TM-29", DateTime.UtcNow));
        await agg.TickAsync();
        agg.ProcessEvent(new BranchChanged("TM-30-x", "TM-30", DateTime.UtcNow));
        await agg.TickAsync();
        agg.ProcessEvent(new BranchChanged("TM-29-x", "TM-29", DateTime.UtcNow));
        await agg.TickAsync();

        var open = tickets.GetAllOpen();
        open.Single(c => c.TicketKey == "TM-29").MinutesActive.Should().Be(2);
        open.Single(c => c.TicketKey == "TM-30").MinutesActive.Should().Be(1);
    }
}
