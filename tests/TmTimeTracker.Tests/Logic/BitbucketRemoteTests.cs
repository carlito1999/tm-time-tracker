using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class BitbucketRemoteTests
{
    [Theory]
    // The userinfo prefix is what Bitbucket's own "Clone" button produces, so it is the common case
    // rather than an edge one - and mistaking it for part of the path yields a nonsense workspace.
    [InlineData("https://lefteris3@bitbucket.org/thecubeee/sheeponline-new.git", "thecubeee", "sheeponline-new")]
    [InlineData("https://bitbucket.org/thecubeee/sheeponline-new.git", "thecubeee", "sheeponline-new")]
    [InlineData("https://bitbucket.org/thecubeee/sheeponline-new", "thecubeee", "sheeponline-new")]
    [InlineData("git@bitbucket.org:thecubeee/sheeponline-new.git", "thecubeee", "sheeponline-new")]
    [InlineData("ssh://git@bitbucket.org/thecubeee/sheeponline-new.git", "thecubeee", "sheeponline-new")]
    public void Reads_workspace_and_slug_from_a_bitbucket_remote(string url, string workspace, string slug)
    {
        var repo = BitbucketRemote.Parse(url);

        repo.Should().Be(new BitbucketRepo(workspace, slug));
    }

    [Theory]
    [InlineData("https://github.com/carlito1999/tm-time-tracker.git")]   // this very repo
    [InlineData("git@github.com:carlito1999/tm-time-tracker.git")]
    [InlineData("https://bitbucket.example.com/scm/proj/repo.git")]      // Bitbucket Server, different API
    [InlineData("")]
    [InlineData(null)]
    public void Ignores_anything_that_is_not_bitbucket_cloud(string? url)
    {
        BitbucketRemote.Parse(url).Should().BeNull();
    }
}
