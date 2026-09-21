using FluentAssertions;
using TmTimeTracker.Data;
using Xunit;

namespace TmTimeTracker.Tests.Data;

public class HourActivityRepositoryTests
{
    private static readonly DateTime Nine = new(2026, 9, 21, 9, 0, 0);
    private static readonly DateTime Ten = new(2026, 9, 21, 10, 0, 0);

    private static HourActivityRepository Build()
    {
        var factory = SharedSqlite.NewInMemory();
        new DatabaseInitializer(factory).EnsureCreated();
        return new HourActivityRepository(factory);
    }

    [Fact]
    public void Reads_nothing_back_from_an_empty_ledger()
    {
        Build().GetBetween(Nine, Ten).Should().BeEmpty();
    }

    [Fact]
    public void Credits_a_single_minute()
    {
        var repo = Build();

        repo.CreditMinute(Nine, @"C:\projects\training-manager", "TM-1");

        var row = repo.GetBetween(Nine, Ten).Single();
        row.HourStart.Should().Be(Nine);
        row.RepoPath.Should().Be(@"C:\projects\training-manager");
        row.TicketKey.Should().Be("TM-1");
        row.Minutes.Should().Be(1);
    }

    // TimeAggregator ticks once a minute, so the same (hour, repo, ticket) is credited up to 60
    // times. The upsert has to add rather than replace or every hour would read as one minute.
    [Fact]
    public void Accumulates_repeated_minutes_in_the_same_hour()
    {
        var repo = Build();

        for (var i = 0; i < 17; i++)
            repo.CreditMinute(Nine, @"C:\repo", "TM-1");

        repo.GetBetween(Nine, Ten).Single().Minutes.Should().Be(17);
    }

    // A minute on main has no ticket. It still has to be recorded, because the report's Repo
    // column should fill for that hour even when the Ticket column cannot.
    [Fact]
    public void Stores_a_ticketless_minute_under_an_empty_key()
    {
        var repo = Build();

        repo.CreditMinute(Nine, @"C:\repo", "");

        repo.GetBetween(Nine, Ten).Single().TicketKey.Should().BeEmpty();
    }

    // Claude sessions earn minutes in several repos at once, so one hour routinely holds more
    // than one repo. They must not collapse into each other.
    [Fact]
    public void Keeps_two_repos_in_the_same_hour_apart()
    {
        var repo = Build();

        repo.CreditMinute(Nine, @"C:\sheeponline-new", "SN-1");
        repo.CreditMinute(Nine, @"C:\training-manager", "TM-1");

        repo.GetBetween(Nine, Ten).Should().HaveCount(2);
    }

    [Fact]
    public void Keeps_two_tickets_in_the_same_repo_and_hour_apart()
    {
        var repo = Build();

        repo.CreditMinute(Nine, @"C:\repo", "TM-1");
        repo.CreditMinute(Nine, @"C:\repo", "TM-2");

        repo.GetBetween(Nine, Ten).Select(r => r.TicketKey).Should().BeEquivalentTo("TM-1", "TM-2");
    }

    // Windows paths are case-insensitive and reach the aggregator as whatever the resolver
    // produced, so two spellings of one repo must land on one row rather than double the hour.
    [Fact]
    public void Treats_two_spellings_of_a_path_as_one_repo()
    {
        var repo = Build();

        repo.CreditMinute(Nine, @"C:\projects\Training-Manager", "TM-1");
        repo.CreditMinute(Nine, @"c:\PROJECTS\training-manager", "TM-1");

        repo.GetBetween(Nine, Ten).Single().Minutes.Should().Be(2);
    }

    // The export asks for a date range. The upper bound is exclusive so a caller can pass the
    // start of the next day without dragging that day's first hour in.
    [Fact]
    public void Reads_only_the_hours_inside_the_range()
    {
        var repo = Build();
        repo.CreditMinute(Nine.AddHours(-1), @"C:\repo", "TM-0");
        repo.CreditMinute(Nine, @"C:\repo", "TM-1");
        repo.CreditMinute(Ten, @"C:\repo", "TM-2");

        repo.GetBetween(Nine, Ten).Select(r => r.TicketKey).Should().Equal("TM-1");
    }

    // hour_start is stored as text, so the range filter only works if the format sorts
    // lexicographically. A year boundary is where a sloppy format gives itself away.
    [Fact]
    public void Orders_hours_correctly_across_a_year_boundary()
    {
        var repo = Build();
        repo.CreditMinute(new DateTime(2026, 12, 31, 23, 0, 0), @"C:\repo", "TM-OLD");
        repo.CreditMinute(new DateTime(2027, 1, 1, 8, 0, 0), @"C:\repo", "TM-NEW");

        repo.GetBetween(new DateTime(2026, 12, 1, 0, 0, 0), new DateTime(2027, 2, 1, 0, 0, 0))
            .Select(r => r.TicketKey).Should().Equal("TM-OLD", "TM-NEW");
    }

    // Three months of history is the whole retention promise: older hours go, newer hours stay.
    [Fact]
    public void Prunes_only_the_hours_older_than_the_cutoff()
    {
        var repo = Build();
        var cutoff = new DateTime(2026, 6, 23, 0, 0, 0);
        repo.CreditMinute(cutoff.AddHours(-1), @"C:\repo", "TM-OLD");
        repo.CreditMinute(cutoff.AddHours(1), @"C:\repo", "TM-NEW");

        var deleted = repo.PruneOlderThan(cutoff);

        deleted.Should().Be(1);
        repo.GetBetween(new DateTime(2026, 1, 1, 0, 0, 0), new DateTime(2027, 1, 1, 0, 0, 0))
            .Select(r => r.TicketKey).Should().Equal("TM-NEW");
    }
}
