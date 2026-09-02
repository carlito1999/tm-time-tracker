using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class ChannelSuggesterTests
{
    private static readonly string[] Channels =
        { "general", "sheeponline", "training-manager", "thecube-site", "ai-content-studio" };

    [Theory]
    [InlineData("SheepOnline New", "sheeponline")]
    [InlineData("Training Manager", "training-manager")]
    [InlineData("The Cube Site", "thecube-site")]
    public void Suggests_the_matching_channel(string projectName, string expected)
    {
        ChannelSuggester.Suggest(projectName, Channels).Should().Be(expected);
    }

    [Fact]
    public void Returns_null_when_nothing_matches()
    {
        ChannelSuggester.Suggest("Dynatag Codes", Channels).Should().BeNull();
    }

    // A wrong pre-selection posts to the wrong channel silently; no guess is safer.
    [Fact]
    public void Returns_null_rather_than_guessing_between_two_candidates()
    {
        var ambiguous = new[] { "sheeponline", "sheeponline-new" };
        ChannelSuggester.Suggest("SheepOnline New", ambiguous).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Returns_null_for_blank_project_name(string? projectName)
    {
        ChannelSuggester.Suggest(projectName, Channels).Should().BeNull();
    }

    [Fact]
    public void Empty_channel_list_yields_null()
    {
        ChannelSuggester.Suggest("SheepOnline New", Array.Empty<string>()).Should().BeNull();
    }
}
