using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

/// <summary>
/// SN-305 was estimated at 40+25+15 and reached Jira as 80m, which renders "1h 20m" - false
/// precision for a figure that is an explicit median guess. Estimates are read against a
/// quarter-hour grid, so the total is put on it before it is written.
/// </summary>
public class EstimateRoundingTests
{
    [Theory]
    [InlineData(80, 90)]     // SN-305: 40+25+15
    [InlineData(140, 150)]
    [InlineData(1, 15)]
    [InlineData(14, 15)]
    [InlineData(15, 15)]     // already on the grid, left alone
    [InlineData(16, 30)]
    [InlineData(75, 75)]
    [InlineData(90, 90)]
    [InlineData(91, 105)]
    public void Rounds_up_to_the_next_quarter_hour(int minutes, int expected)
    {
        EstimateRounding.CeilingToQuarterHour(minutes).Should().Be(expected);
    }

    // Ceiling rather than nearest, decided with the user: an estimate that shrinks on rounding
    // is worse than one that grows.
    [Fact]
    public void Never_rounds_a_real_estimate_down()
    {
        for (var m = 1; m <= 600; m++)
            EstimateRounding.CeilingToQuarterHour(m).Should().BeGreaterThanOrEqualTo(m);
    }

    [Fact]
    public void Never_adds_more_than_a_quarter_hour()
    {
        for (var m = 1; m <= 600; m++)
            EstimateRounding.CeilingToQuarterHour(m).Should().BeLessThan(m + 15);
    }

    [Fact]
    public void Always_lands_on_the_grid()
    {
        for (var m = 1; m <= 600; m++)
            (EstimateRounding.CeilingToQuarterHour(m) % 15).Should().Be(0);
    }

    // The sanity gate already rejects a phase below one minute, so this is defensive rather than
    // reachable - but rounding nothing up to fifteen minutes would invent work that was never
    // estimated.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    public void Leaves_nothing_to_round_when_there_are_no_minutes(int minutes, int expected)
    {
        EstimateRounding.CeilingToQuarterHour(minutes).Should().Be(expected);
    }
}
