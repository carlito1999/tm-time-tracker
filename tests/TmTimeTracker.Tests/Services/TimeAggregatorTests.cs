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
    private const string RepoA = @"C:\projects\sheepsonline";
    private const string RepoB = @"C:\projects\training-manager";

    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 9, 3, 9, 0, 0, DateTimeKind.Utc);
        public DateTimeOffset LocalNow => new(UtcNow, TimeSpan.Zero);
    }

    /// <summary>
    /// Stands in for RepoActivityMonitor. Tests set the credit set directly. `humanActive` is
    /// recorded so the idle tests can prove the aggregator passes its activity state through, and
    /// by default the fake mirrors the real monitor by dropping everything when the human is idle.
    /// Claude-stream tests clear <see cref="SuppressWhenIdle"/> to model a repo that keeps
    /// accruing with nobody at the desk.
    /// </summary>
    private sealed class FakeSource : IRepoActivitySource
    {
        public List<RepoActivity> Next = new();
        public bool? LastHumanActive;
        public bool SuppressWhenIdle = true;

        public IReadOnlyList<RepoActivity> Sample(DateTime lastTickUtc, bool humanActive)
        {
            LastHumanActive = humanActive;
            if (!humanActive && SuppressWhenIdle) return Array.Empty<RepoActivity>();
            return Next.ToList();
        }
    }

    private static (TimeAggregator agg, FakeSource src, TicketTimeRepository tickets,
        HourActivityRepository hours) Build()
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        var tickets = new TicketTimeRepository(ds);
        var hours = new HourActivityRepository(ds);
        var src = new FakeSource();
        var agg = new TimeAggregator(new EventBus(), src, tickets, hours, new FakeClock(),
            NullLogger<TimeAggregator>.Instance);
        agg.ProcessEvent(new ActivityChanged(UserActivityState.Active, DateTime.UtcNow));
        return (agg, src, tickets, hours);
    }

    private static RepoActivity On(string repo, string branch, string? ticket) =>
        new(repo, branch, ticket);

    private static int Minutes(TicketTimeRepository t, string key) =>
        t.GetAllOpen().SingleOrDefault(c => c.TicketKey == key)?.MinutesActive ?? 0;

    // ---- single-repo behaviour, preserved from the original suite ---------------------------

    [Fact]
    public async Task Increments_when_active_and_has_ticket()
    {
        var (agg, src, tickets, _) = Build();
        src.Next = new() { On(RepoA, "TM-29-x", "TM-29") };

        await agg.TickAsync();

        var open = tickets.GetAllOpen().Should().ContainSingle().Subject;
        open.TicketKey.Should().Be("TM-29");
        open.MinutesActive.Should().Be(1);
    }

    [Fact]
    public async Task Does_not_increment_when_idle()
    {
        var (agg, src, tickets, _) = Build();
        agg.ProcessEvent(new ActivityChanged(UserActivityState.Idle, DateTime.UtcNow));
        src.Next = new() { On(RepoA, "TM-29-x", "TM-29") };

        await agg.TickAsync();

        src.LastHumanActive.Should().BeFalse("the aggregator must pass idle state to the source");
        tickets.GetAllOpen().Should().BeEmpty();
    }

    [Fact]
    public async Task Does_not_increment_when_no_ticket()
    {
        var (agg, src, tickets, _) = Build();
        src.Next = new() { On(RepoA, "main", null) };

        await agg.TickAsync();
        tickets.GetAllOpen().Should().BeEmpty();
    }

    [Fact]
    public async Task Switching_branch_opens_new_cycle_for_new_ticket()
    {
        var (agg, src, tickets, _) = Build();

        src.Next = new() { On(RepoA, "feature/TM-29", "TM-29") };
        await agg.TickAsync();

        src.Next = new() { On(RepoA, "feature/TM-30", "TM-30") };
        await agg.TickAsync();
        await agg.TickAsync();

        tickets.GetAllOpen().Should().HaveCount(2);
        Minutes(tickets, "TM-29").Should().Be(1);
        Minutes(tickets, "TM-30").Should().Be(2);
    }

    [Fact]
    public async Task Switching_back_resumes_existing_cycle()
    {
        var (agg, src, tickets, _) = Build();

        src.Next = new() { On(RepoA, "TM-29-x", "TM-29") };
        await agg.TickAsync();
        src.Next = new() { On(RepoA, "TM-30-x", "TM-30") };
        await agg.TickAsync();
        src.Next = new() { On(RepoA, "TM-29-x", "TM-29") };
        await agg.TickAsync();

        Minutes(tickets, "TM-29").Should().Be(2);
        Minutes(tickets, "TM-30").Should().Be(1);
    }

    // Time spent on a branch with no ticket in its name used to be dropped on the floor. It is
    // now held and credited to whatever ticket branch you switch to next - the "I started before
    // I branched" case.
    [Fact]
    public async Task Carries_unattributed_time_onto_the_next_ticket_branch()
    {
        var (agg, src, tickets, _) = Build();
        src.Next = new() { On(RepoA, "main", null) };
        for (var i = 0; i < 3; i++) await agg.TickAsync();
        tickets.GetAllOpen().Should().BeEmpty("nothing is written until a ticket is known");

        src.Next = new() { On(RepoA, "TM-29-x", "TM-29") };
        await agg.TickAsync();

        Minutes(tickets, "TM-29").Should().Be(4, "3 carried from main plus 1 of its own");
    }

    // The dangerous case: switching between two real tickets must never move time, or bouncing
    // back and forth would launder one ticket's hours onto the other.
    [Fact]
    public async Task Does_not_move_time_between_two_ticket_branches()
    {
        var (agg, src, tickets, _) = Build();

        src.Next = new() { On(RepoA, "TM-101-a", "TM-101") };
        await agg.TickAsync();
        await agg.TickAsync();

        src.Next = new() { On(RepoA, "TM-202-b", "TM-202") };
        await agg.TickAsync();

        Minutes(tickets, "TM-101").Should().Be(2);
        Minutes(tickets, "TM-202").Should().Be(1);
    }

    // A whole morning on main is not "I forgot to branch", it is unrelated work. Capping keeps it
    // from being billed to whichever ticket happens to come next.
    [Fact]
    public async Task Caps_the_carried_time()
    {
        var (agg, src, tickets, _) = Build();
        src.Next = new() { On(RepoA, "main", null) };
        for (var i = 0; i < 45; i++) await agg.TickAsync();

        src.Next = new() { On(RepoA, "TM-29-x", "TM-29") };
        await agg.TickAsync();

        Minutes(tickets, "TM-29").Should().Be(31, "30 capped carry plus this tick's own minute");
    }

    [Fact]
    public async Task Carries_the_buffer_only_once()
    {
        var (agg, src, tickets, _) = Build();
        src.Next = new() { On(RepoA, "main", null) };
        for (var i = 0; i < 3; i++) await agg.TickAsync();

        src.Next = new() { On(RepoA, "TM-1-a", "TM-1") };
        await agg.TickAsync();
        src.Next = new() { On(RepoA, "TM-2-b", "TM-2") };
        await agg.TickAsync();

        Minutes(tickets, "TM-1").Should().Be(4, "3 carried plus its own minute");
        Minutes(tickets, "TM-2").Should().Be(1, "the buffer was already spent");
    }

    // Idle minutes are not work, on main or anywhere else.
    [Fact]
    public async Task Does_not_buffer_idle_time_on_main()
    {
        var (agg, src, tickets, _) = Build();
        agg.ProcessEvent(new ActivityChanged(UserActivityState.Idle, DateTime.UtcNow));
        src.Next = new() { On(RepoA, "main", null) };
        for (var i = 0; i < 5; i++) await agg.TickAsync();

        agg.ProcessEvent(new ActivityChanged(UserActivityState.Active, DateTime.UtcNow));
        src.Next = new() { On(RepoA, "TM-29-x", "TM-29") };
        await agg.TickAsync();

        Minutes(tickets, "TM-29").Should().Be(1, "idle minutes on main are not work");
    }

    // ---- concurrent behaviour, new ----------------------------------------------------------

    [Fact]
    public async Task Credits_two_repos_in_the_same_minute()
    {
        var (agg, src, tickets, _) = Build();
        src.Next = new() { On(RepoA, "SN-299-x", "SN-299"), On(RepoB, "TM-30-y", "TM-30") };

        await agg.TickAsync();
        await agg.TickAsync();

        Minutes(tickets, "SN-299").Should().Be(2);
        Minutes(tickets, "TM-30").Should().Be(2);
    }

    [Fact]
    public async Task Credits_a_ticket_once_even_if_two_repos_are_on_it()
    {
        var (agg, src, tickets, _) = Build();
        src.Next = new() { On(RepoA, "TM-30-x", "TM-30"), On(RepoB, "TM-30-y", "TM-30") };

        await agg.TickAsync();

        Minutes(tickets, "TM-30").Should().Be(1, "one cycle exists per key, so credit it once");
    }

    // The reported bug: minutes banked on one repo's main used to land on whatever ticket branch
    // was checked out next, in any repo.
    [Fact]
    public async Task Banked_time_never_carries_across_repos()
    {
        var (agg, src, tickets, _) = Build();
        src.Next = new() { On(RepoA, "main", null) };
        for (var i = 0; i < 10; i++) await agg.TickAsync();

        src.Next = new() { On(RepoB, "TM-30-y", "TM-30") };
        await agg.TickAsync();

        Minutes(tickets, "TM-30").Should().Be(1, "repo A's banked time is not repo B's work");
    }

    [Fact]
    public async Task Banked_time_carries_within_its_own_repo_while_another_repo_is_active()
    {
        var (agg, src, tickets, _) = Build();
        src.Next = new() { On(RepoA, "main", null), On(RepoB, "TM-30-y", "TM-30") };
        for (var i = 0; i < 5; i++) await agg.TickAsync();

        Minutes(tickets, "TM-30").Should().Be(5);

        src.Next = new() { On(RepoA, "SN-299-x", "SN-299"), On(RepoB, "TM-30-y", "TM-30") };
        await agg.TickAsync();

        Minutes(tickets, "SN-299").Should().Be(6, "5 banked in repo A plus its own minute");
        Minutes(tickets, "TM-30").Should().Be(6, "repo B was unaffected throughout");
    }

    [Fact]
    public async Task Caps_each_repos_buffer_independently()
    {
        var (agg, src, tickets, _) = Build();
        src.Next = new() { On(RepoA, "main", null), On(RepoB, "develop", null) };
        for (var i = 0; i < 45; i++) await agg.TickAsync();

        src.Next = new() { On(RepoA, "SN-299-x", "SN-299"), On(RepoB, "TM-30-y", "TM-30") };
        await agg.TickAsync();

        Minutes(tickets, "SN-299").Should().Be(31);
        Minutes(tickets, "TM-30").Should().Be(31);
    }

    // Claude streams are not gated on the user being at the desk.
    [Fact]
    public async Task Credits_a_Claude_repo_while_the_human_is_idle()
    {
        var (agg, src, tickets, _) = Build();
        agg.ProcessEvent(new ActivityChanged(UserActivityState.Idle, DateTime.UtcNow));
        src.SuppressWhenIdle = false;   // the real monitor still returns Claude repos when idle
        src.Next = new() { On(RepoB, "TM-30-y", "TM-30") };

        await agg.TickAsync();
        await agg.TickAsync();

        Minutes(tickets, "TM-30").Should().Be(2);
    }

    [Fact]
    public async Task A_throwing_source_costs_the_tick_but_not_the_process()
    {
        var (_, _, tickets, hours) = Build();
        var boom = new ThrowingSource();
        var agg2 = new TimeAggregator(new EventBus(), boom, tickets, hours, new FakeClock(),
            NullLogger<TimeAggregator>.Instance);
        agg2.ProcessEvent(new ActivityChanged(UserActivityState.Active, DateTime.UtcNow));

        await agg2.Invoking(a => a.TickAsync()).Should().NotThrowAsync();
        tickets.GetAllOpen().Should().BeEmpty();
    }

    private sealed class ThrowingSource : IRepoActivitySource
    {
        public IReadOnlyList<RepoActivity> Sample(DateTime lastTickUtc, bool humanActive) =>
            throw new InvalidOperationException("probe exploded");
    }

    /// <summary>Throws on its first call only, and records the cursor it was handed each time.</summary>
    private sealed class ThrowsOnceSource : IRepoActivitySource
    {
        public int Calls;
        public DateTime LastSeenCursor;
        public List<RepoActivity> Next = new();

        public IReadOnlyList<RepoActivity> Sample(DateTime lastTickUtc, bool humanActive)
        {
            Calls++;
            LastSeenCursor = lastTickUtc;
            if (Calls == 1) throw new InvalidOperationException("probe exploded");
            return Next.ToList();
        }
    }

    // A failed sample must not eat a minute. The cursor bounds which Claude writes count as fresh,
    // so advancing it past a minute nobody looked at would drop that minute's work for good.
    [Fact]
    public async Task A_failed_sample_does_not_advance_the_activity_cursor()
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        var tickets = new TicketTimeRepository(ds);
        var clock = new FakeClock();
        var src = new ThrowsOnceSource { Next = { On(RepoB, "TM-30-y", "TM-30") } };
        var agg = new TimeAggregator(new EventBus(), src, tickets, new HourActivityRepository(ds),
            clock, NullLogger<TimeAggregator>.Instance);
        agg.ProcessEvent(new ActivityChanged(UserActivityState.Active, DateTime.UtcNow));
        var cursorAtStart = clock.UtcNow;

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        await agg.TickAsync();          // this one throws inside

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        await agg.TickAsync();

        src.LastSeenCursor.Should().Be(cursorAtStart,
            "the failed minute was never sampled, so its writes must still be in range");
        Minutes(tickets, "TM-30").Should().Be(1);
    }

    // ---- the hour ledger the weekly report reads ---------------------------------------------

    // FakeClock pins LocalNow to a zero offset, so the hour these land in is 09:00 on any machine.
    private static readonly DateTime LedgerHour = new(2026, 9, 3, 9, 0, 0);

    private static IReadOnlyList<HourActivityRow> Ledger(HourActivityRepository h) =>
        h.GetBetween(LedgerHour, LedgerHour.AddHours(1));

    [Fact]
    public async Task Records_the_credited_minute_in_the_hour_ledger()
    {
        var (agg, src, _, hours) = Build();
        src.Next = new() { On(RepoA, "TM-29-x", "TM-29") };

        await agg.TickAsync();

        var row = Ledger(hours).Should().ContainSingle().Subject;
        row.RepoPath.Should().Be(RepoA);
        row.TicketKey.Should().Be("TM-29");
        row.Minutes.Should().Be(1);
    }

    // The ledger records the raw sample, before attribution. A minute on main is banked in memory
    // and reaches ticket_time only later under the next ticket, but the report has to show that
    // the hour was spent in that repo - so this is the one place the two stores disagree.
    [Fact]
    public async Task Records_a_ticketless_minute_under_its_repo_though_no_ticket_is_billed()
    {
        var (agg, src, tickets, hours) = Build();
        src.Next = new() { On(RepoA, "main", null) };

        await agg.TickAsync();

        tickets.GetAllOpen().Should().BeEmpty();
        var row = Ledger(hours).Should().ContainSingle().Subject;
        row.RepoPath.Should().Be(RepoA);
        row.TicketKey.Should().BeEmpty();
    }

    [Fact]
    public async Task Records_every_repo_of_a_concurrent_sample()
    {
        var (agg, src, _, hours) = Build();
        src.Next = new() { On(RepoA, "TM-29-x", "TM-29"), On(RepoB, "SN-5-y", "SN-5") };

        await agg.TickAsync();

        Ledger(hours).Select(r => r.RepoPath).Should().BeEquivalentTo(RepoA, RepoB);
    }

    // An idle minute is not work, and the report must not invent one.
    [Fact]
    public async Task Records_nothing_while_the_human_is_idle()
    {
        var (agg, src, _, hours) = Build();
        src.Next = new() { On(RepoA, "TM-29-x", "TM-29") };
        agg.ProcessEvent(new ActivityChanged(UserActivityState.Idle, DateTime.UtcNow));

        await agg.TickAsync();

        Ledger(hours).Should().BeEmpty();
    }

    // Sixty ticks in one hour must read as sixty minutes, not one row overwritten sixty times.
    [Fact]
    public async Task Accumulates_successive_ticks_into_one_hour_row()
    {
        var (agg, src, _, hours) = Build();
        src.Next = new() { On(RepoA, "TM-29-x", "TM-29") };

        for (var i = 0; i < 5; i++) await agg.TickAsync();

        Ledger(hours).Should().ContainSingle().Which.Minutes.Should().Be(5);
    }
}
