using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TmTimeTracker.Jira;
using TmTimeTracker.Services;
using Xunit;

namespace TmTimeTracker.Tests.Services;

public class JiraPullRequestSourceTests
{
    private const string Ticket = "SN-296";

    private const string RawUuidUrl =
        "https://bitbucket.org/{824edde7-0312-45b7-8816-3a50102f2058}/" +
        "{7730b682-bec1-40da-80ca-781c54d17cd8}/pull-requests/360";

    private static DevStatusResponse Response(
        DevStatusPullRequest[]? pullRequests, DevStatusBranch[]? branches = null) =>
        new(new[] { new DevStatusDetail(pullRequests, branches) });

    private static DevStatusPullRequest Pr(
        string id = "360", string status = "OPEN", string? url = RawUuidUrl,
        string title = "SN-296-372: enhance Tolgee caching",
        string? branch = "SN-296-372-tolgee-caching",
        string lastUpdate = "2026-09-02T09:11:27.585+0000") =>
        new(id, title, status, url, "sheeponline-new", lastUpdate,
            branch is null ? null : new DevStatusPullRequestRef(branch));

    private static DevStatusBranch Branch(string commitUrl, string? authorTimestamp = null) =>
        new(new DevStatusCommit(commitUrl, authorTimestamp));

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

        var result = await New(devStatus).GetSnapshotAsync("28814", Ticket, CancellationToken.None);

        result.PullRequest.Should().NotBeNull();
        result.PullRequest!.Url.Should().Be("https://bitbucket.org/thecubeee/sheeponline-new/pull-requests/360");
        result.PullRequest!.Title.Should().Be("SN-296-372: enhance Tolgee caching");
        result.PullRequest!.Status.Should().Be("OPEN");
    }

    [Fact]
    public async Task Returns_the_uuid_url_when_no_commit_url_is_available()
    {
        var result = await New(Returning(Response(new[] { Pr() })))
            .GetSnapshotAsync("28814", Ticket, CancellationToken.None);

        result.PullRequest!.Url.Should().StartWith("https://bitbucket.org/%7B");
    }

    [Fact]
    public async Task Returns_null_when_the_issue_has_no_pull_requests()
    {
        var result = await New(Returning(Response(Array.Empty<DevStatusPullRequest>())))
            .GetSnapshotAsync("28814", Ticket, CancellationToken.None);

        result.PullRequest.Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_when_the_response_has_no_detail()
    {
        var result = await New(Returning(new DevStatusResponse(Array.Empty<DevStatusDetail>())))
            .GetSnapshotAsync("28814", Ticket, CancellationToken.None);

        result.PullRequest.Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_without_calling_the_endpoint_when_the_issue_id_is_missing()
    {
        var devStatus = Returning(Response(new[] { Pr() }));

        var result = await New(devStatus).GetSnapshotAsync(null, Ticket, CancellationToken.None);

        result.PullRequest.Should().BeNull();
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

        var result = await New(devStatus).GetSnapshotAsync("28814", Ticket, CancellationToken.None);

        result.PullRequest.Should().BeNull();
    }

    // The warning deadline is measured from the last commit, so the snapshot must surface it
    // even when no pull request exists yet - which is exactly the case it exists for.
    [Fact]
    public async Task Reports_the_latest_commit_time_even_with_no_pull_request()
    {
        var devStatus = Returning(Response(
            Array.Empty<DevStatusPullRequest>(),
            new[]
            {
                Branch("https://bitbucket.org/thecubeee/sheeponline-new/commits/aaa",
                       "2026-09-02T11:43:54.000+0000"),
                Branch("https://bitbucket.org/thecubeee/sheeponline-new/commits/bbb",
                       "2026-09-02T09:00:00.000+0000")
            }));

        var result = await New(devStatus).GetSnapshotAsync("28848", Ticket, CancellationToken.None);

        result.PullRequest.Should().BeNull();
        result.LastCommitUtc.Should().Be(
            new DateTimeOffset(2026, 9, 2, 11, 43, 54, TimeSpan.Zero));
    }

    // dev-status files other tickets' pull requests under an issue: SN-246's 297 and SN-295's 358
    // both came back under SN-291 on 2026-09-04, and 297 held the newest timestamp of the payload.
    [Fact]
    public async Task Does_not_report_another_tickets_pull_request()
    {
        var devStatus = Returning(Response(new[]
        {
            Pr(id: "297", title: "SN-246-273: feat: scrapie notifs",
               branch: "SN-246-273-notifs-&-email", lastUpdate: "2026-09-04T05:06:25.025+0000"),
        }));

        var result = await New(devStatus).GetSnapshotAsync("28611", "SN-291", CancellationToken.None);

        result.PullRequest.Should().BeNull();
    }

    [Fact]
    public async Task Last_commit_time_is_null_when_no_branch_reports_one()
    {
        var result = await New(Returning(Response(Array.Empty<DevStatusPullRequest>())))
            .GetSnapshotAsync("28848", Ticket, CancellationToken.None);

        result.LastCommitUtc.Should().BeNull();
    }
}
