using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TmTimeTracker.Jira;
using TmTimeTracker.Services;
using Xunit;

namespace TmTimeTracker.Tests.Services;

public class JiraPullRequestSourceTests
{
    private const string RawUuidUrl =
        "https://bitbucket.org/{824edde7-0312-45b7-8816-3a50102f2058}/" +
        "{7730b682-bec1-40da-80ca-781c54d17cd8}/pull-requests/360";

    private static DevStatusResponse Response(
        DevStatusPullRequest[]? pullRequests, DevStatusBranch[]? branches = null) =>
        new(new[] { new DevStatusDetail(pullRequests, branches) });

    private static DevStatusPullRequest Pr(
        string id = "360", string status = "OPEN", string? url = RawUuidUrl) =>
        new(id, $"SN-296-372: enhance Tolgee caching", status, url,
            "sheeponline-new", "2026-09-02T09:11:27.585+0000");

    private static DevStatusBranch Branch(string commitUrl) =>
        new(new DevStatusCommit(commitUrl));

    private static JiraPullRequestSource New(Mock<IDevStatusSource> devStatus) =>
        new(devStatus.Object, NullLogger<JiraPullRequestSource>.Instance);

    private static Mock<IDevStatusSource> Returning(DevStatusResponse response)
    {
        var mock = new Mock<IDevStatusSource>();
        mock.Setup(d => d.GetPullRequestDetailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
        return mock;
    }

    [Fact]
    public async Task Maps_the_pull_request_and_rebuilds_a_readable_url()
    {
        var devStatus = Returning(Response(
            new[] { Pr() },
            new[] { Branch("https://bitbucket.org/thecubeee/sheeponline-new/commits/6d639e7") }));

        var result = await New(devStatus).GetBestAsync("28814", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Url.Should().Be("https://bitbucket.org/thecubeee/sheeponline-new/pull-requests/360");
        result.Title.Should().Be("SN-296-372: enhance Tolgee caching");
        result.Status.Should().Be("OPEN");
    }

    [Fact]
    public async Task Returns_the_uuid_url_when_no_commit_url_is_available()
    {
        var result = await New(Returning(Response(new[] { Pr() })))
            .GetBestAsync("28814", CancellationToken.None);

        result!.Url.Should().StartWith("https://bitbucket.org/%7B");
    }

    [Fact]
    public async Task Returns_null_when_the_issue_has_no_pull_requests()
    {
        var result = await New(Returning(Response(Array.Empty<DevStatusPullRequest>())))
            .GetBestAsync("28814", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_when_the_response_has_no_detail()
    {
        var result = await New(Returning(new DevStatusResponse(Array.Empty<DevStatusDetail>())))
            .GetBestAsync("28814", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_without_calling_the_endpoint_when_the_issue_id_is_missing()
    {
        var devStatus = Returning(Response(new[] { Pr() }));

        var result = await New(devStatus).GetBestAsync(null, CancellationToken.None);

        result.Should().BeNull();
        devStatus.Verify(d => d.GetPullRequestDetailAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The endpoint is undocumented and its scope contract unpublished, so an auth failure
    // must degrade to "no PR known" rather than take down the notification.
    [Fact]
    public async Task A_failing_endpoint_returns_null_rather_than_throwing()
    {
        var devStatus = new Mock<IDevStatusSource>();
        devStatus.Setup(d => d.GetPullRequestDetailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new HttpRequestException("401 Unauthorized"));

        var result = await New(devStatus).GetBestAsync("28814", CancellationToken.None);

        result.Should().BeNull();
    }
}
