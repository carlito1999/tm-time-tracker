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

    // Time spent on a branch with no ticket in its name used to be dropped on the floor. It is now
    // held and credited to whatever ticket branch you switch to next - the "I started before I
    // branched" case.
    [Fact]
    public async Task Carries_unattributed_time_onto_the_next_ticket_branch()
    {
        var (agg, _, tickets, _) = Build();
        agg.ProcessEvent(new ActivityChanged(UserActivityState.Active, DateTime.UtcNow));
        agg.ProcessEvent(new BranchChanged("main", null, DateTime.UtcNow));

        for (var i = 0; i < 3; i++) await agg.TickAsync();
        tickets.GetAllOpen().Should().BeEmpty("nothing is written until a ticket is known");

        agg.ProcessEvent(new BranchChanged("TM-29-x", "TM-29", DateTime.UtcNow));
        await agg.TickAsync();

        var open = tickets.GetAllOpen().Should().ContainSingle().Subject;
        open.TicketKey.Should().Be("TM-29");
        open.MinutesActive.Should().Be(4, "3 carried from main plus 1 of its own");
    }

    // The dangerous case: switching between two real tickets must never move time, or bouncing
    // back and forth would launder one ticket's hours onto the other.
    [Fact]
    public async Task Does_not_move_time_between_two_ticket_branches()
    {
        var (agg, _, tickets, _) = Build();
        agg.ProcessEvent(new ActivityChanged(UserActivityState.Active, DateTime.UtcNow));

        agg.ProcessEvent(new BranchChanged("TM-101-a", "TM-101", DateTime.UtcNow));
        await agg.TickAsync();
        await agg.TickAsync();

        agg.ProcessEvent(new BranchChanged("TM-202-b", "TM-202", DateTime.UtcNow));
        await agg.TickAsync();

        tickets.GetAllOpen().Single(c => c.TicketKey == "TM-101").MinutesActive.Should().Be(2);
        tickets.GetAllOpen().Single(c => c.TicketKey == "TM-202").MinutesActive.Should().Be(1);
    }

    // A whole morning on main is not "I forgot to branch", it is unrelated work. Capping keeps it
    // from being billed to whichever ticket happens to come next.
    [Fact]
    public async Task Caps_the_carried_time()
    {
        var (agg, _, tickets, _) = Build();
        agg.ProcessEvent(new ActivityChanged(UserActivityState.Active, DateTime.UtcNow));
        agg.ProcessEvent(new BranchChanged("main", null, DateTime.UtcNow));

        for (var i = 0; i < 45; i++) await agg.TickAsync();

        agg.ProcessEvent(new BranchChanged("TM-29-x", "TM-29", DateTime.UtcNow));

        tickets.GetAllOpen().Single().MinutesActive.Should().Be(30);
    }

    [Fact]
    public async Task Carries_the_buffer_only_once()
    {
        var (agg, _, tickets, _) = Build();
        agg.ProcessEvent(new ActivityChanged(UserActivityState.Active, DateTime.UtcNow));
        agg.ProcessEvent(new BranchChanged("main", null, DateTime.UtcNow));
        for (var i = 0; i < 3; i++) await agg.TickAsync();

        agg.ProcessEvent(new BranchChanged("TM-1-a", "TM-1", DateTime.UtcNow));
        agg.ProcessEvent(new BranchChanged("TM-2-b", "TM-2", DateTime.UtcNow));

        tickets.GetAllOpen().Single(c => c.TicketKey == "TM-1").MinutesActive.Should().Be(3);
        tickets.GetAllOpen().Where(c => c.TicketKey == "TM-2").Should().BeEmpty();
    }

    // Idle minutes are not work, on main or anywhere else.
    [Fact]
    public async Task Does_not_buffer_idle_time_on_main()
    {
        var (agg, _, tickets, _) = Build();
        agg.ProcessEvent(new ActivityChanged(UserActivityState.Idle, DateTime.UtcNow));
        agg.ProcessEvent(new BranchChanged("main", null, DateTime.UtcNow));
        for (var i = 0; i < 5; i++) await agg.TickAsync();

        agg.ProcessEvent(new ActivityChanged(UserActivityState.Active, DateTime.UtcNow));
        agg.ProcessEvent(new BranchChanged("TM-29-x", "TM-29", DateTime.UtcNow));

        tickets.GetAllOpen().Should().BeEmpty();
    }
}
