using FluentAssertions;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class WorklogDescriptionBuilderTests
{
    private static StoredRememberEntry E(string time, string date, string body) =>
        new(0, "TM-29", time, date, body, "x.md", null);

    [Fact]
    public void Joins_entries_as_bullets_with_time_prefix()
    {
        var entries = new[]
        {
            E("09:00", "2026-05-25", "first body"),
            E("10:15", "2026-05-25", "second body line 1\nsecond body line 2"),
        };
        var result = WorklogDescriptionBuilder.Build(entries);
        result.Should().Be(
            "- [09:00] first body\n" +
            "- [10:15] second body line 1\n  second body line 2");
    }

    [Fact]
    public void Empty_entries_produces_default_text()
    {
        WorklogDescriptionBuilder.Build(Array.Empty<StoredRememberEntry>())
            .Should().Be("(no .remember/ entries captured)");
    }

    [Fact]
    public void Sorts_by_date_then_time()
    {
        var entries = new[]
        {
            E("10:15", "2026-05-25", "later"),
            E("09:00", "2026-05-25", "earlier"),
            E("17:00", "2026-05-24", "yesterday"),
        };
        var result = WorklogDescriptionBuilder.Build(entries);
        result.Should().Be(
            "- [2026-05-24 17:00] yesterday\n" +
            "- [09:00] earlier\n" +
            "- [10:15] later");
    }

    [Fact]
    public void Truncates_with_marker_when_over_byte_limit()
    {
        var hugeBody = new string('x', 35_000);
        var entries = new[] { E("09:00", "2026-05-25", hugeBody) };
        var result = WorklogDescriptionBuilder.Build(entries);
        result.Length.Should().BeLessThan(33_000);
        result.Should().EndWith("…(truncated)");
    }
}
