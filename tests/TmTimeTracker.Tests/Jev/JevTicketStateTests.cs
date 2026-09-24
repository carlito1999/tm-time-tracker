using System.Text.Json;
using FluentAssertions;
using TmTimeTracker.Jev;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Jev;

/// <summary>
/// What Jev reads about a ticket. Jev's accuracy drops as unrelated material piles into the
/// state, so this carries the ticket's own words and nothing else - no key, no repo, no code.
/// </summary>
public class JevTicketStateTests
{
    private static JsonElement Json(object state) =>
        JsonSerializer.SerializeToElement(state);

    private static readonly LinkedIssue Linked = new(
        "https://gitlab.com/si-bv/stamboekonline/-/work_items/377", 377, "si-bv/stamboekonline",
        "Ancestry is not shown", "The overview is empty for three animals",
        new[] { "Anna: happens on production only" });

    [Fact]
    public void Carries_the_summary_and_the_description()
    {
        var state = Json(JevTicketState.Build("Fix login", "It returns 500", [], []));

        state.GetProperty("summary").GetString().Should().Be("Fix login");
        state.GetProperty("description").GetString().Should().Be("It returns 500");
    }

    // Jev cannot open an attachment, but knowing a screenshot exists tells it the description is
    // not the whole story.
    [Fact]
    public void Names_the_attachments()
    {
        var state = Json(JevTicketState.Build("s", "d", ["error.png", "data.xlsx"], []));

        state.GetProperty("attachments").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("error.png", "data.xlsx");
    }

    // An SN ticket is often nothing but a GitLab link, so the linked issue is the ticket.
    [Fact]
    public void Includes_the_linked_issue_text()
    {
        var state = Json(JevTicketState.Build("s", "d", [], [Linked]));

        var issue = state.GetProperty("linked_issues")[0];
        issue.GetProperty("title").GetString().Should().Be("Ancestry is not shown");
        issue.GetProperty("description").GetString().Should().Be("The overview is empty for three animals");
        issue.GetProperty("comments")[0].GetString().Should().Be("Anna: happens on production only");
    }

    [Fact]
    public void Leaves_out_sections_that_are_empty()
    {
        var state = Json(JevTicketState.Build("Fix login", "  ", [], []));

        state.TryGetProperty("description", out _).Should().BeFalse();
        state.TryGetProperty("attachments", out _).Should().BeFalse();
        state.TryGetProperty("linked_issues", out _).Should().BeFalse();
    }

    [Fact]
    public void Truncates_a_description_long_enough_to_crowd_out_the_rest()
    {
        var state = Json(JevTicketState.Build("s", new string('x', 20_000), [], []));

        var description = state.GetProperty("description").GetString()!;
        description.Length.Should().BeLessThan(20_000);
        description.Should().EndWith("[truncated]");
    }
}
