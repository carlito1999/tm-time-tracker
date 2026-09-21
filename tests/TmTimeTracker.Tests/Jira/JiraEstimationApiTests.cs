using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TmTimeTracker.Jira;
using TmTimeTracker.Logic;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace TmTimeTracker.Tests.Jira;

public class JiraEstimationApiTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Dispose();

    private JiraApiClient New()
    {
        var src = new Mock<IAccessTokenSource>();
        src.Setup(s => s.GetAccessTokenAsync(It.IsAny<CancellationToken>()))
           .ReturnsAsync(("at-1", "cloud-1"));
        return new JiraApiClient(new HttpClient(), src.Object,
            NullLogger<JiraApiClient>.Instance, apiBaseOverride: _server.Url + "/ex/jira");
    }

    private static object IssuePayload(string key, int? estimateSeconds = null) => new
    {
        key,
        id = "10001",
        fields = new
        {
            summary = "Add a retry to the poll loop",
            status = new
            {
                name = "To Do",
                statusCategory = new { key = "new", name = "To Do" }
            },
            timetracking = estimateSeconds is null
                ? new { }
                : (object)new { originalEstimateSeconds = estimateSeconds.Value }
        }
    };

    [Fact]
    public async Task Searches_with_jql_and_returns_the_issues()
    {
        _server.Given(Request.Create().WithPath("/ex/jira/cloud-1/rest/api/3/search/jql").UsingPost()
                        .WithHeader("Authorization", "Bearer at-1"))
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   issues = new[] { IssuePayload("TM-1"), IssuePayload("TM-2") }
               }));

        var issues = await New().SearchIssuesAsync("project = TM", CancellationToken.None);

        issues.Select(i => i.Key).Should().BeEquivalentTo("TM-1", "TM-2");
    }

    // Atlassian replaced /rest/api/3/search with /rest/api/3/search/jql. Instances differ in
    // when they cut over, so a rejection of the new path falls back rather than failing.
    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    public async Task Falls_back_to_the_legacy_search_endpoint(int status)
    {
        _server.Given(Request.Create().WithPath("/ex/jira/cloud-1/rest/api/3/search/jql").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(status));
        _server.Given(Request.Create().WithPath("/ex/jira/cloud-1/rest/api/3/search").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   issues = new[] { IssuePayload("TM-9") }
               }));

        var issues = await New().SearchIssuesAsync("project = TM", CancellationToken.None);

        issues.Single().Key.Should().Be("TM-9");
    }

    [Fact]
    public async Task Returns_no_issues_when_the_search_matches_nothing()
    {
        _server.Given(Request.Create().WithPath("/ex/jira/cloud-1/rest/api/3/search/jql").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                    .WithBodyAsJson(new { issues = Array.Empty<object>() }));

        (await New().SearchIssuesAsync("project = TM", CancellationToken.None))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Writes_the_original_estimate()
    {
        _server.Given(Request.Create().WithPath("/ex/jira/cloud-1/rest/api/3/issue/TM-1").UsingPut()
                        .WithHeader("Authorization", "Bearer at-1"))
               .RespondWith(Response.Create().WithStatusCode(204));

        await New().SetOriginalEstimateAsync("TM-1", 140, CancellationToken.None);

        var log = _server.LogEntries.Single(e => e.RequestMessage.Method == "PUT");
        var body = log.RequestMessage.Body!;
        body.Should().Contain("timetracking").And.Contain("140m");
    }

    [Fact]
    public async Task Surfaces_a_failed_estimate_write()
    {
        _server.Given(Request.Create().WithPath("/ex/jira/cloud-1/rest/api/3/issue/TM-1").UsingPut())
               .RespondWith(Response.Create().WithStatusCode(400)
                    .WithBody("""{"errors":{"timetracking":"Field cannot be set"}}"""));

        var write = async () =>
            await New().SetOriginalEstimateAsync("TM-1", 140, CancellationToken.None);

        await write.Should().ThrowAsync<HttpRequestException>();
    }

    // Deserialising timetracking is not enough: Jira omits any field the request did not ask
    // for, so gate 4 would compare against a permanently absent value.
    [Fact]
    public async Task Asks_jira_for_the_fields_the_estimator_needs()
    {
        _server.Given(Request.Create().WithPath("/ex/jira/cloud-1/rest/api/3/issue/TM-1").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200)
                    .WithBodyAsJson(IssuePayload("TM-1")));

        await New().GetIssueAsync("TM-1", CancellationToken.None);

        var url = _server.LogEntries.Single().RequestMessage.Url;
        url.Should().Contain("timetracking").And.Contain("description");
    }

    // Gate 4 compares against what Jira actually stored, so the read has to ask for the field.
    [Fact]
    public async Task Reads_back_the_stored_original_estimate()
    {
        _server.Given(Request.Create().WithPath("/ex/jira/cloud-1/rest/api/3/issue/TM-1").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200)
                    .WithBodyAsJson(IssuePayload("TM-1", estimateSeconds: 8400)));

        var issue = await New().GetIssueAsync("TM-1", CancellationToken.None);

        issue.Fields.TimeTracking!.OriginalEstimateSeconds.Should().Be(8400);
    }

    [Fact]
    public async Task Reports_no_estimate_when_the_field_is_empty()
    {
        _server.Given(Request.Create().WithPath("/ex/jira/cloud-1/rest/api/3/issue/TM-1").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200)
                    .WithBodyAsJson(IssuePayload("TM-1")));

        var issue = await New().GetIssueAsync("TM-1", CancellationToken.None);

        issue.Fields.TimeTracking?.OriginalEstimateSeconds.Should().BeNull();
    }

    [Fact]
    public async Task Reads_the_description_as_flattened_text()
    {
        _server.Given(Request.Create().WithPath("/ex/jira/cloud-1/rest/api/3/issue/TM-1").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   key = "TM-1",
                   fields = new
                   {
                       status = new { name = "To Do", statusCategory = new { key = "new", name = "To Do" } },
                       description = new
                       {
                           type = "doc",
                           content = new[]
                           {
                               new
                               {
                                   type = "paragraph",
                                   content = new[] { new { type = "text", text = "Retry twice." } }
                               }
                           }
                       }
                   }
               }));

        var issue = await New().GetIssueAsync("TM-1", CancellationToken.None);

        AdfText.Flatten(issue.Fields.Description).Should().Be("Retry twice.");
    }
}
