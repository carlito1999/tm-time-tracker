using FluentAssertions;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Data;

public class RememberEntryRepositoryTests
{
    private static (RememberEntryRepository repo, ISqliteConnectionFactory ds) New()
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        return (new RememberEntryRepository(ds), ds);
    }

    private static RememberEntry E(string ticket, string time, string body, string file) =>
        new(time, $"{ticket}-tag", ticket, body, file);

    [Fact]
    public void Upsert_inserts_new_entry()
    {
        var (repo, _) = New();
        repo.UpsertMany(new[] { E("TM-29", "09:00", "started", "today-2026-05-25.md") },
            entryDate: "2026-05-25");
        var got = repo.GetForTicketAndDate("TM-29", "2026-05-25");
        got.Should().ContainSingle();
        got[0].Body.Should().Be("started");
    }

    [Fact]
    public void Upsert_is_idempotent_on_repeated_calls()
    {
        var (repo, _) = New();
        var e = new[] { E("TM-29", "09:00", "started", "today-2026-05-25.md") };
        repo.UpsertMany(e, "2026-05-25");
        repo.UpsertMany(e, "2026-05-25");
        repo.GetForTicketAndDate("TM-29", "2026-05-25").Should().ContainSingle();
    }

    [Fact]
    public void GetForTicketAndDate_returns_only_matching_rows()
    {
        var (repo, _) = New();
        repo.UpsertMany(new[]
        {
            E("TM-29", "09:00", "a", "today-2026-05-25.md"),
            E("TM-30", "10:00", "b", "today-2026-05-25.md"),
        }, "2026-05-25");
        repo.UpsertMany(new[]
        {
            E("TM-29", "09:00", "yesterday-a", "today-2026-05-24.md"),
        }, "2026-05-24");

        repo.GetForTicketAndDate("TM-29", "2026-05-25")
            .Should().ContainSingle()
            .Which.Body.Should().Be("a");
    }

    [Fact]
    public void TagConsumed_links_entry_to_worklog_row()
    {
        var (repo, _) = New();
        repo.UpsertMany(new[] { E("TM-29", "09:00", "a", "f.md") }, "2026-05-25");
        var entry = repo.GetForTicketAndDate("TM-29", "2026-05-25").Single();

        repo.TagConsumed(new[] { entry.Id }, ticketTimeRowId: 42);
        var after = repo.GetForTicketAndDate("TM-29", "2026-05-25").Single();
        after.ConsumedInWorklog.Should().Be(42);
    }
}
