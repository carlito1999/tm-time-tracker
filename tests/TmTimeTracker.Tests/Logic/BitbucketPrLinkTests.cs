using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class BitbucketPrLinkTests
{
    private const string RawUuidUrl =
        "https://bitbucket.org/{824edde7-0312-45b7-8816-3a50102f2058}/" +
        "{7730b682-bec1-40da-80ca-781c54d17cd8}/pull-requests/360";

    private const string CommitUrl =
        "https://bitbucket.org/thecubeee/sheeponline-new/commits/6d639e7200aafcba1d3ce12ed660a684e4d8d1c7";

    [Fact]
    public void Rebuilds_a_readable_url_from_a_matching_commit_url()
    {
        BitbucketPrLink.Build(RawUuidUrl, "360", "sheeponline-new", new[] { CommitUrl })
            .Should().Be("https://bitbucket.org/thecubeee/sheeponline-new/pull-requests/360");
    }

    [Fact]
    public void Falls_back_to_the_raw_url_with_braces_encoded_when_no_commit_url_is_available()
    {
        BitbucketPrLink.Build(RawUuidUrl, "360", "sheeponline-new", null)
            .Should().Be(
                "https://bitbucket.org/%7B824edde7-0312-45b7-8816-3a50102f2058%7D/" +
                "%7B7730b682-bec1-40da-80ca-781c54d17cd8%7D/pull-requests/360");
    }

    // An issue can span repositories; stitching one repo's workspace onto another's
    // name would produce a confident-looking 404.
    [Fact]
    public void Does_not_borrow_a_workspace_from_a_different_repository()
    {
        var otherRepoCommit = "https://bitbucket.org/otherworkspace/training-manager/commits/abc123";

        BitbucketPrLink.Build(RawUuidUrl, "360", "sheeponline-new", new[] { otherRepoCommit })
            .Should().StartWith("https://bitbucket.org/%7B");
    }

    [Fact]
    public void Picks_the_commit_url_belonging_to_the_right_repository()
    {
        var urls = new[]
        {
            "https://bitbucket.org/otherworkspace/training-manager/commits/abc123",
            CommitUrl
        };

        BitbucketPrLink.Build(RawUuidUrl, "360", "sheeponline-new", urls)
            .Should().Be("https://bitbucket.org/thecubeee/sheeponline-new/pull-requests/360");
    }

    [Theory]
    [InlineData(null, "sheeponline-new")]
    [InlineData("360", null)]
    public void Falls_back_when_the_pieces_for_a_pretty_url_are_missing(string? prId, string? repo)
    {
        BitbucketPrLink.Build(RawUuidUrl, prId, repo, new[] { CommitUrl })
            .Should().StartWith("https://bitbucket.org/%7B");
    }

    [Fact]
    public void An_already_readable_url_is_left_alone()
    {
        const string pretty = "https://bitbucket.org/thecubeee/sheeponline-new/pull-requests/360";

        BitbucketPrLink.Build(pretty, null, null, null).Should().Be(pretty);
    }

    [Fact]
    public void Null_and_blank_commit_urls_are_skipped_without_throwing()
    {
        BitbucketPrLink.Build(RawUuidUrl, "360", "sheeponline-new", new string?[] { null, "", CommitUrl })
            .Should().Be("https://bitbucket.org/thecubeee/sheeponline-new/pull-requests/360");
    }
}
