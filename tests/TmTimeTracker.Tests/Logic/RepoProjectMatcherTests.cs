using FluentAssertions;
using TmTimeTracker.Jira;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class RepoProjectMatcherTests
{
    private static readonly JiraProject TrainingManager = new("TM", "Training Manager");
    private static readonly JiraProject Storefront = new("SN", "Storefront");

    // The boards are named after the repos, but Jira shows a display name with a space and
    // capitals while the folder on disk is lowercase and hyphenated.
    [Theory]
    [InlineData(@"C:\projects\training-manager")]
    [InlineData(@"C:\projects\Training-Manager")]
    [InlineData(@"C:\projects\training_manager")]
    [InlineData(@"C:\projects\trainingmanager")]
    public void Matches_the_project_display_name_ignoring_case_and_separators(string repoPath)
    {
        RepoProjectMatcher.Match(repoPath, new[] { TrainingManager, Storefront })
            .Should().Be("TM");
    }

    [Fact]
    public void Matches_the_project_key_when_the_folder_is_named_after_it()
    {
        RepoProjectMatcher.Match(@"C:\projects\sn", new[] { TrainingManager, Storefront })
            .Should().Be("SN");
    }

    // A folder whose name is another project's key must not be stolen by that key match:
    // the display name is the stronger signal, so it wins.
    [Fact]
    public void Prefers_a_name_match_over_a_key_match()
    {
        var confusing = new JiraProject("STOREFRONT", "Something Else");
        RepoProjectMatcher.Match(@"C:\projects\storefront", new[] { confusing, Storefront })
            .Should().Be("SN");
    }

    [Fact]
    public void Returns_null_when_nothing_matches()
    {
        RepoProjectMatcher.Match(@"C:\projects\tm-time-tracker", new[] { TrainingManager })
            .Should().BeNull();
    }

    // Guessing between two equally good candidates would write an estimate onto a ticket in
    // the wrong project, so the matcher declines and leaves it for the user to map.
    [Fact]
    public void Returns_null_when_two_projects_match_equally()
    {
        var duplicate = new JiraProject("TM2", "training manager");
        RepoProjectMatcher.Match(@"C:\projects\training-manager", new[] { TrainingManager, duplicate })
            .Should().BeNull();
    }

    [Theory]
    [InlineData(@"C:\projects\training-manager\")]
    [InlineData(@"C:/projects/training-manager/")]
    public void Ignores_a_trailing_separator(string repoPath)
    {
        RepoProjectMatcher.Match(repoPath, new[] { TrainingManager }).Should().Be("TM");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Returns_null_for_a_missing_path(string? repoPath)
    {
        RepoProjectMatcher.Match(repoPath, new[] { TrainingManager }).Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_there_are_no_projects()
    {
        RepoProjectMatcher.Match(@"C:\projects\training-manager", Array.Empty<JiraProject>())
            .Should().BeNull();
    }
}
