using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class TicketKeyExtractorTests
{
    [Theory]
    [InlineData("TM-29-1808-add-worklog", "TM-29")]
    [InlineData("feature/TM-31", "TM-31")]
    [InlineData("bugfix/TM-7-fix-typo", "TM-7")]
    [InlineData("TM-100", "TM-100")]
    [InlineData("user/lefteris/TM-42-poc", "TM-42")]
    public void Extracts_first_TM_token(string branch, string expected)
    {
        TicketKeyExtractor.Extract(branch).Should().Be(expected);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("master")]
    [InlineData("feature/no-ticket")]
    [InlineData("")]
    [InlineData("TM-")]
    [InlineData("XX-29")]
    public void Returns_null_when_no_match(string branch)
    {
        TicketKeyExtractor.Extract(branch).Should().BeNull();
    }

    [Fact]
    public void Returns_first_match_when_multiple_present()
    {
        TicketKeyExtractor.Extract("TM-29-merge-from-TM-30").Should().Be("TM-29");
    }

    [Fact]
    public void Null_input_returns_null()
    {
        TicketKeyExtractor.Extract(null!).Should().BeNull();
    }
}
