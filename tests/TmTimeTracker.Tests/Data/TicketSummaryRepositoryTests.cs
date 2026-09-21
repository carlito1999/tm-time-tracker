using FluentAssertions;
using TmTimeTracker.Data;
using Xunit;

namespace TmTimeTracker.Tests.Data;

public class TicketSummaryRepositoryTests
{
    private static TicketSummaryRepository Build()
    {
        var factory = SharedSqlite.NewInMemory();
        new DatabaseInitializer(factory).EnsureCreated();
        return new TicketSummaryRepository(factory);
    }

    [Fact]
    public void Reads_nothing_for_a_ticket_it_has_never_seen()
    {
        Build().GetMany(new[] { "TM-1" }).Should().BeEmpty();
    }

    [Fact]
    public void Round_trips_a_summary()
    {
        var repo = Build();

        repo.Upsert("TM-1", "Add handling for benign Moodle errors in RestClient");

        repo.GetMany(new[] { "TM-1" })["TM-1"]
            .Should().Be("Add handling for benign Moodle errors in RestClient");
    }

    // A summary is edited in Jira long after the first poll saw it, and the report should show
    // the ticket as it reads now rather than as it read in June.
    [Fact]
    public void Overwrites_an_earlier_summary()
    {
        var repo = Build();
        repo.Upsert("TM-1", "old wording");

        repo.Upsert("TM-1", "new wording");

        repo.GetMany(new[] { "TM-1" })["TM-1"].Should().Be("new wording");
    }

    // The export looks up every key in the week at once rather than a query per row.
    [Fact]
    public void Reads_several_tickets_in_one_call()
    {
        var repo = Build();
        repo.Upsert("TM-1", "first");
        repo.Upsert("SN-2", "second");

        repo.GetMany(new[] { "TM-1", "SN-2" }).Should().HaveCount(2);
    }

    // Tickets that earned time before this cache existed have no row. The report falls back to
    // the bare key for those, so a missing key must be an absence, never a throw.
    [Fact]
    public void Leaves_out_keys_it_has_no_row_for()
    {
        var repo = Build();
        repo.Upsert("TM-1", "first");

        var found = repo.GetMany(new[] { "TM-1", "TM-NEVER-SEEN" });

        found.Should().ContainKey("TM-1");
        found.Should().NotContainKey("TM-NEVER-SEEN");
    }

    [Fact]
    public void Reads_nothing_when_asked_for_no_keys()
    {
        Build().GetMany(Array.Empty<string>()).Should().BeEmpty();
    }
}
