using FluentAssertions;
using TmTimeTracker.Data;
using Xunit;

namespace TmTimeTracker.Tests.Data;

public class TicketTimeRepositoryTests
{
    private static (TicketTimeRepository repo, ISqliteConnectionFactory ds) New()
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        return (new TicketTimeRepository(ds), ds);
    }

    [Fact]
    public void OpenOrCreate_creates_new_row_when_none_exists()
    {
        var (repo, _) = New();
        var cycle = repo.OpenOrCreateCycle("TM-29", DateTime.UtcNow);
        cycle.TicketKey.Should().Be("TM-29");
        cycle.MinutesActive.Should().Be(0);
        cycle.SubmittedAt.Should().BeNull();
    }

    [Fact]
    public void OpenOrCreate_returns_existing_open_row_on_second_call()
    {
        var (repo, _) = New();
        var first = repo.OpenOrCreateCycle("TM-29", DateTime.UtcNow);
        var second = repo.OpenOrCreateCycle("TM-29", DateTime.UtcNow.AddHours(1));
        second.Id.Should().Be(first.Id);
    }

    [Fact]
    public void IncrementMinute_adds_one_minute()
    {
        var (repo, _) = New();
        var cycle = repo.OpenOrCreateCycle("TM-29", DateTime.UtcNow);
        repo.IncrementMinute(cycle.Id);
        repo.IncrementMinute(cycle.Id);
        var reloaded = repo.GetById(cycle.Id);
        reloaded!.MinutesActive.Should().Be(2);
    }

    [Fact]
    public void MarkSubmitted_closes_cycle_and_opens_new_one_next_time()
    {
        var (repo, _) = New();
        var first = repo.OpenOrCreateCycle("TM-29", DateTime.UtcNow);
        repo.MarkSubmitted(first.Id, worklogId: "wl-1",
            submittedMinutes: 30, submittedAtUtc: DateTime.UtcNow);

        var next = repo.OpenOrCreateCycle("TM-29", DateTime.UtcNow.AddMinutes(1));
        next.Id.Should().NotBe(first.Id);
    }

    [Fact]
    public void GetAllOpen_returns_only_unsubmitted_rows()
    {
        var (repo, _) = New();
        var a = repo.OpenOrCreateCycle("TM-29", DateTime.UtcNow);
        var b = repo.OpenOrCreateCycle("TM-30", DateTime.UtcNow);
        repo.MarkSubmitted(b.Id, "wl-1", 10, DateTime.UtcNow);

        var open = repo.GetAllOpen().Select(c => c.Id).ToList();
        open.Should().ContainSingle().Which.Should().Be(a.Id);
    }

    [Fact]
    public void UpdateStatusSnapshot_persists_last_seen_status()
    {
        var (repo, _) = New();
        var c = repo.OpenOrCreateCycle("TM-29", DateTime.UtcNow);
        var now = DateTime.UtcNow;
        repo.UpdateStatusSnapshot(c.Id, "In Progress", now);

        var reloaded = repo.GetById(c.Id)!;
        reloaded.LastSeenStatus.Should().Be("In Progress");
        reloaded.LastPolled.Should().BeCloseTo(now, TimeSpan.FromSeconds(1));
    }
}
