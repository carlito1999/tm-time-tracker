using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TmTimeTracker.Jira;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace TmTimeTracker.Tests.Jira;

public class JiraApiClientTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Dispose();

    private JiraApiClient New(IAccessTokenSource src) =>
        new(new HttpClient(), src, NullLogger<JiraApiClient>.Instance,
            apiBaseOverride: _server.Url + "/ex/jira");

    [Fact]
    public async Task GetIssue_uses_bearer_token_and_returns_issue()
    {
        var src = new Mock<IAccessTokenSource>();
        src.Setup(s => s.GetAccessTokenAsync(It.IsAny<CancellationToken>()))
           .ReturnsAsync(("at-1", "cloud-1"));

        _server.Given(Request.Create().WithPath("/ex/jira/cloud-1/rest/api/3/issue/TM-29").UsingGet()
                        .WithHeader("Authorization", "Bearer at-1"))
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   key = "TM-29",
                   fields = new { status = new { name = "In Progress",
                       statusCategory = new { key = "indeterminate", name = "In Progress" } } }
               }));

        var issue = await New(src.Object).GetIssueAsync("TM-29", CancellationToken.None);
        issue.Key.Should().Be("TM-29");
        issue.Fields.Status.Name.Should().Be("In Progress");
    }

    [Fact]
    public async Task On_401_refresh_is_requested_and_request_retried()
    {
        var src = new Mock<IAccessTokenSource>();
        src.SetupSequence(s => s.GetAccessTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(("stale", "cloud-1"))
            .ReturnsAsync(("fresh", "cloud-1"));
        src.Setup(s => s.ForceRefreshAsync(It.IsAny<CancellationToken>()))
           .Returns(Task.CompletedTask);

        _server.Given(Request.Create().WithPath("/ex/jira/cloud-1/rest/api/3/issue/TM-29")
                        .WithHeader("Authorization", "Bearer stale"))
               .RespondWith(Response.Create().WithStatusCode(401));
        _server.Given(Request.Create().WithPath("/ex/jira/cloud-1/rest/api/3/issue/TM-29")
                        .WithHeader("Authorization", "Bearer fresh"))
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   key = "TM-29",
                   fields = new { status = new { name = "Review",
                       statusCategory = new { key = "indeterminate", name = "Review" } } }
               }));

        var issue = await New(src.Object).GetIssueAsync("TM-29", CancellationToken.None);
        issue.Fields.Status.Name.Should().Be("Review");
        src.Verify(s => s.ForceRefreshAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PostWorklog_returns_worklog_id()
    {
        var src = new Mock<IAccessTokenSource>();
        src.Setup(s => s.GetAccessTokenAsync(It.IsAny<CancellationToken>()))
           .ReturnsAsync(("at-1", "cloud-1"));

        _server.Given(Request.Create().WithPath("/ex/jira/cloud-1/rest/api/3/issue/TM-29/worklog")
                                       .UsingPost())
               .RespondWith(Response.Create().WithStatusCode(201)
                   .WithBodyAsJson(new { id = "10001" }));

        var req = new WorklogRequest(
            TimeSpentSeconds: 1800,
            StartedIso: "2026-05-25T09:00:00.000+0300",
            Comment: new WorklogComment("doc", 1, new[] {
                new WorklogContent("paragraph", new[] { new WorklogTextNode("text", "hi") }) }));
        var resp = await New(src.Object).PostWorklogAsync("TM-29", req, CancellationToken.None);
        resp.Id.Should().Be("10001");
    }
}
