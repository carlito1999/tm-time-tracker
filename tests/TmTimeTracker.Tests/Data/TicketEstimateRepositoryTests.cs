using FluentAssertions;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Data;

public class TicketEstimateRepositoryTests
{
    private static readonly DateTime Now = new(2026, 9, 3, 11, 0, 0, DateTimeKind.Utc);

    private static TicketEstimateRepository Build()
    {
        var factory = SharedSqlite.NewInMemory();
        new DatabaseInitializer(factory).EnsureCreated();
        return new TicketEstimateRepository(factory);
    }

    private static TicketEstimate Estimate() =>
        new(90, 30, 20, "medium", "Touches the poll loop and its tests.");

    [Fact]
    public void Finds_nothing_for_an_unknown_ticket()
    {
        Build().Find("TM-1").Should().BeNull();
    }

    [Fact]
    public void Queues_a_ticket_as_pending_with_no_attempts()
    {
        var repo = Build();

        repo.QueueIfMissing("TM-1", @"C:\projects\training-manager");

        var row = repo.Find("TM-1")!;
        row.Status.Should().Be(EstimateStatus.Pending);
        row.Attempts.Should().Be(0);
        row.RepoPath.Should().Be(@"C:\projects\training-manager");
    }

    // The 5-minute loop re-queues everything it sees, so queueing must never reset progress -
    // otherwise the attempt cap can never be reached and a bad ticket retries forever.
    [Fact]
    public void Queueing_an_existing_ticket_preserves_its_attempts()
    {
        var repo = Build();
        repo.QueueIfMissing("TM-1", @"C:\repo");
        repo.RecordAttempt("TM-1");

        repo.QueueIfMissing("TM-1", @"C:\repo");

        repo.Find("TM-1")!.Attempts.Should().Be(1);
    }

    [Fact]
    public void Records_attempts_cumulatively()
    {
        var repo = Build();
        repo.QueueIfMissing("TM-1", @"C:\repo");

        repo.RecordAttempt("TM-1").Should().Be(1);
        repo.RecordAttempt("TM-1").Should().Be(2);
        repo.Find("TM-1")!.Attempts.Should().Be(2);
    }

    [Fact]
    public void Stores_a_finished_estimate()
    {
        var repo = Build();
        repo.QueueIfMissing("TM-1", @"C:\repo");

        repo.MarkDone("TM-1", Estimate(), "{\"raw\":true}", Now);

        var row = repo.Find("TM-1")!;
        row.Status.Should().Be(EstimateStatus.Done);
        row.ImplementationMinutes.Should().Be(90);
        row.TestingMinutes.Should().Be(30);
        row.ReviewMinutes.Should().Be(20);
        row.Confidence.Should().Be("medium");
        row.Rationale.Should().Be("Touches the poll loop and its tests.");
        row.RawOutput.Should().Be("{\"raw\":true}");
        row.EstimatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Stores_the_gate_that_rejected_a_run()
    {
        var repo = Build();
        repo.QueueIfMissing("TM-1", @"C:\repo");

        repo.MarkFailed("TM-1", EstimateGate.Sanity, "the rationale was empty or a placeholder.");

        var row = repo.Find("TM-1")!;
        row.Status.Should().Be(EstimateStatus.Failed);
        row.FailedGate.Should().Be(nameof(EstimateGate.Sanity));
        row.Error.Should().Contain("placeholder");
    }

    [Fact]
    public void Marks_a_ticket_that_already_had_an_estimate()
    {
        var repo = Build();
        repo.QueueIfMissing("TM-1", @"C:\repo");

        repo.MarkSkippedExisting("TM-1");

        repo.Find("TM-1")!.Status.Should().Be(EstimateStatus.SkippedExisting);
    }

    // Notification dedupe survives a restart because it lives in the row, not in memory.
    [Fact]
    public void Remembers_that_the_user_was_already_warned()
    {
        var repo = Build();
        repo.QueueIfMissing("TM-1", @"C:\repo");
        repo.Find("TM-1")!.WarnedAtUtc.Should().BeNull();

        repo.MarkWarned("TM-1", Now);

        repo.Find("TM-1")!.WarnedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Lists_every_known_ticket()
    {
        var repo = Build();
        repo.QueueIfMissing("TM-1", @"C:\repo");
        repo.QueueIfMissing("TM-2", @"C:\repo");

        repo.GetAll().Select(r => r.TicketKey).Should().BeEquivalentTo("TM-1", "TM-2");
    }

    // A ticket that failed twice must not be picked up again by the next sweep, and a ticket
    // that succeeded must not be re-estimated - both are terminal.
    [Theory]
    [InlineData(EstimateStatus.Done, true)]
    [InlineData(EstimateStatus.Failed, true)]
    [InlineData(EstimateStatus.SkippedExisting, true)]
    [InlineData(EstimateStatus.Pending, false)]
    public void Knows_which_states_are_terminal(string status, bool terminal)
    {
        EstimateStatus.IsTerminal(status).Should().Be(terminal);
    }
}
