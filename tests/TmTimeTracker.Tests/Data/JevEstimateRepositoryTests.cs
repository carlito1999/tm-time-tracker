using FluentAssertions;
using TmTimeTracker.Data;
using Xunit;

namespace TmTimeTracker.Tests.Data;

public class JevEstimateRepositoryTests
{
    private static JevEstimateRepository Build()
    {
        var factory = SharedSqlite.NewInMemory();
        new DatabaseInitializer(factory).EnsureCreated();
        return new JevEstimateRepository(factory);
    }

    private static readonly DateTime At = new(2026, 9, 24, 7, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Knows_nothing_about_a_ticket_it_never_estimated()
    {
        Build().Find("TM-1").Should().BeNull();
    }

    // The raw answer is kept whole: a refit reads it back rather than asking Jev again.
    [Fact]
    public void Round_trips_the_answer_and_the_prediction()
    {
        var repo = Build();

        repo.Save("TM-1", "typesafe/jev-1.13-20260917", """{"m60":1.0}""", 60, 60, 0.9, 74, At);

        repo.Find("TM-1").Should().Be(new JevEstimateRow(
            "TM-1", "typesafe/jev-1.13-20260917", """{"m60":1.0}""", 60, 60, 0.9, 74, At));
    }

    [Fact]
    public void A_second_estimate_of_the_same_ticket_replaces_the_first()
    {
        var repo = Build();
        repo.Save("TM-1", "m", "{}", 60, 60, 0.9, 74, At);

        repo.Save("TM-1", "m", "{}", 120, 90, 0.4, 101, At.AddHours(1));

        repo.Find("TM-1")!.PredictedMinutes.Should().Be(101);
    }
}
