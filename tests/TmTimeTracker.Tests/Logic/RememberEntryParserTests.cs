using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class RememberEntryParserTests
{
    [Fact]
    public void Parses_single_entry_with_ticket_tag()
    {
        var md = "## 07:20 | TM-29-1808-add-worklog\n" +
                 "Started on the worklog endpoint.\nGot the auth working.\n";

        var entries = RememberEntryParser.Parse(md, "today-2026-05-25.md");

        entries.Should().HaveCount(1);
        var e = entries[0];
        e.TimeOfDay.Should().Be("07:20");
        e.ContextTag.Should().Be("TM-29-1808-add-worklog");
        e.TicketKey.Should().Be("TM-29");
        e.Body.Should().Be("Started on the worklog endpoint.\nGot the auth working.");
        e.SourceFile.Should().Be("today-2026-05-25.md");
    }

    [Fact]
    public void Parses_multiple_entries()
    {
        var md =
            "## 09:00 | TM-29\nbody one\n" +
            "## 10:15 | TM-30-something\nbody two line A\nbody two line B\n";

        var entries = RememberEntryParser.Parse(md, "today-2026-05-25.md");

        entries.Should().HaveCount(2);
        entries[0].TicketKey.Should().Be("TM-29");
        entries[0].Body.Should().Be("body one");
        entries[1].TicketKey.Should().Be("TM-30");
        entries[1].Body.Should().Be("body two line A\nbody two line B");
    }

    [Fact]
    public void Skips_entry_without_ticket_in_tag()
    {
        var md = "## 09:00 | random meeting\nnotes\n";
        RememberEntryParser.Parse(md, "x.md").Should().BeEmpty();
    }

    [Fact]
    public void Header_without_pipe_is_ignored()
    {
        var md = "## not an entry header\nbody\n## 09:00 | TM-29\nreal body\n";
        var entries = RememberEntryParser.Parse(md, "x.md");
        entries.Should().HaveCount(1);
        entries[0].Body.Should().Be("real body");
    }

    [Fact]
    public void Empty_body_is_allowed()
    {
        var md = "## 09:00 | TM-29\n";
        var entries = RememberEntryParser.Parse(md, "x.md");
        entries.Should().HaveCount(1);
        entries[0].Body.Should().BeEmpty();
    }

    [Fact]
    public void Trims_trailing_blank_lines_in_body()
    {
        var md = "## 09:00 | TM-29\nbody\n\n\n";
        RememberEntryParser.Parse(md, "x.md")[0].Body.Should().Be("body");
    }

    [Fact]
    public void Empty_or_null_input_returns_empty()
    {
        RememberEntryParser.Parse("", "x.md").Should().BeEmpty();
        RememberEntryParser.Parse(null!, "x.md").Should().BeEmpty();
    }
}
