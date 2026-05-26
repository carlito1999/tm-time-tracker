using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class ClaudeProjectSlugTests
{
    [Theory]
    // Empirically observed on Lefteris' machine 2026-05-25.
    [InlineData(@"c:\projects\tm-time-tracker",  "c--projects-tm-time-tracker")]
    [InlineData(@"C:\projects\training-manager", "C--projects-training-manager")]
    [InlineData(@"C:\Users\TSEGKOS\.local\bin",  "C--Users-TSEGKOS--local-bin")]
    [InlineData(@"C:\Users\TSEGKOS",             "C--Users-TSEGKOS")]
    public void Matches_observed_claude_code_folder_names(string path, string expected)
    {
        ClaudeProjectSlug.FromPath(path).Should().Be(expected);
    }

    [Fact]
    public void Handles_forward_slashes()
    {
        ClaudeProjectSlug.FromPath("c:/projects/a").Should().Be("c--projects-a");
    }

    [Fact]
    public void Returns_empty_for_empty_input()
    {
        ClaudeProjectSlug.FromPath("").Should().Be("");
    }

    [Fact]
    public void Returns_empty_for_null_input()
    {
        ClaudeProjectSlug.FromPath(null!).Should().Be("");
    }
}
