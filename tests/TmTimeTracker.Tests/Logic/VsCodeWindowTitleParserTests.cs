using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class VsCodeWindowTitleParserTests
{
    [Theory]
    [InlineData("foo.cs - my-repo - Visual Studio Code", "my-repo")]
    [InlineData("Program.cs - tm-time-tracker - Visual Studio Code", "tm-time-tracker")]
    [InlineData("● foo.cs - my-repo - Visual Studio Code", "my-repo")]
    [InlineData("my-repo - Visual Studio Code", "my-repo")]
    [InlineData("foo.cs - sub/dir/leaf - Visual Studio Code", "sub/dir/leaf")]
    public void Extracts_workspace_folder(string title, string expected)
    {
        VsCodeWindowTitleParser.Parse(title).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Notepad")]
    [InlineData("foo.cs - my-repo - Cursor")]
    [InlineData("Visual Studio Code")]
    public void Returns_null_for_non_matching_titles(string title)
    {
        VsCodeWindowTitleParser.Parse(title).Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_null_input()
    {
        VsCodeWindowTitleParser.Parse(null!).Should().BeNull();
    }
}
