using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class JiraProjectKeyTests
{
    [Theory]
    [InlineData("SN-296", "SN")]
    [InlineData("TM-51", "TM")]
    [InlineData("ABC_D-12", "ABC_D")]
    [InlineData("CORE_API-42", "CORE_API")]
    [InlineData("V2-9", "V2")]
    public void Extracts_project_key(string ticket, string expected)
    {
        JiraProjectKey.From(ticket).Should().Be(expected);
    }

    // Takes a ticket key, not a branch name - TicketKeyExtractor runs first and
    // reduces "SN-296-372-Tolgee-warning" to "SN-296".
    [Theory]
    [InlineData("nokey")]
    [InlineData("SN-")]
    [InlineData("-296")]
    [InlineData("sn-296")]
    [InlineData("A-1")]
    [InlineData("SN-296-372")]
    [InlineData("")]
    [InlineData(null)]
    public void Returns_null_for_malformed_input(string? ticket)
    {
        JiraProjectKey.From(ticket).Should().BeNull();
    }

    [Fact]
    public void Trims_surrounding_whitespace()
    {
        JiraProjectKey.From("  SN-296  ").Should().Be("SN");
    }
}
