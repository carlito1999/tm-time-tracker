using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TmTimeTracker.Data;
using TmTimeTracker.GitLab;
using TmTimeTracker.Logic;
using TmTimeTracker.Tests.Data;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace TmTimeTracker.Tests.GitLab;

/// <summary>
/// The estimation run cannot follow a link itself - --restricted removes WebFetch and Bash, and
/// WebFetch could not send a PRIVATE-TOKEN header even if it were granted - so the daemon fetches
/// and the token never enters the prompt.
/// </summary>
public class GitLabApiClientTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Dispose();

    private static readonly GitLabIssueRef Link = new(
        "si-bv/stamboekonline", 377,
        "https://gitlab.com/si-bv/stamboekonline/-/work_items/377");

    private sealed class PassthroughProtector : TmTimeTracker.Platform.ITokenProtector
    {
        public byte[] Protect(string value) => System.Text.Encoding.UTF8.GetBytes(value);
        public string Unprotect(byte[] value) => System.Text.Encoding.UTF8.GetString(value);
    }

    private GitLabApiClient New(bool withToken = true)
    {
        var factory = SharedSqlite.NewInMemory();
        new DatabaseInitializer(factory).EnsureCreated();
        var tokens = new GitLabApiTokenRepository(factory, new PassthroughProtector());
        if (withToken) tokens.Save("lefteris31", "glpat-test");

        return new GitLabApiClient(
            new HttpClient(), tokens, NullLogger<GitLabApiClient>.Instance,
            apiBaseOverride: _server.Url);
    }

    // WireMock matches on the DECODED path, so the stub uses the unescaped form. That the
    // request actually goes out percent-encoded is asserted separately, against Url.
    private const string IssuePath = "/projects/si-bv/stamboekonline/issues/377";

    private void StubIssue(object body, int status = 200) =>
        _server.Given(Request.Create().WithPath(IssuePath).UsingGet())
               .RespondWith(Response.Create().WithStatusCode(status).WithBodyAsJson(body));

    private void StubNotes(object body, int status = 200) =>
        _server.Given(Request.Create().WithPath(IssuePath + "/notes").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(status).WithBodyAsJson(body));

    private static object Issue(string title = "Ancestry is not shown for some animals",
                                string description = "not the ancestry overview") =>
        new { iid = 377, title, description, state = "opened" };

    [Fact]
    public async Task Reads_the_title_and_description()
    {
        StubIssue(Issue());
        StubNotes(Array.Empty<object>());

        var issue = await New().FetchAsync(Link, CancellationToken.None);

        issue.Should().NotBeNull();
        issue!.Title.Should().Be("Ancestry is not shown for some animals");
        issue.Description.Should().Be("not the ancestry overview");
        issue.Iid.Should().Be(377);
        issue.ProjectPath.Should().Be("si-bv/stamboekonline");
        issue.Url.Should().Be("https://gitlab.com/si-bv/stamboekonline/-/work_items/377");
    }

    // GitLab groups nest, so the project path contains slashes and must be percent-encoded into
    // a single path segment. An unencoded path resolves to a different endpoint entirely.
    [Fact]
    public async Task Percent_encodes_the_nested_project_path()
    {
        StubIssue(Issue());
        StubNotes(Array.Empty<object>());

        await New().FetchAsync(Link, CancellationToken.None);

        // Url, not Path: WireMock decodes Path, so asserting there would pass even if the
        // client sent a bare slash - and a bare slash reaches a different GitLab endpoint.
        _server.LogEntries.Should().Contain(e =>
            e.RequestMessage.Url.Contains("si-bv%2Fstamboekonline"));
    }

    [Fact]
    public async Task Sends_the_token_as_a_private_token_header()
    {
        StubIssue(Issue());
        StubNotes(Array.Empty<object>());

        await New().FetchAsync(Link, CancellationToken.None);

        _server.LogEntries.Should().Contain(e =>
            e.RequestMessage.Headers!.ContainsKey("PRIVATE-TOKEN") &&
            e.RequestMessage.Headers["PRIVATE-TOKEN"].Contains("glpat-test"));
    }

    // GitLab's own activity entries - "changed the description", label changes - are noise in an
    // estimation prompt and would crowd out the human discussion.
    [Fact]
    public async Task Keeps_human_comments_and_drops_system_notes()
    {
        StubIssue(Issue());
        StubNotes(new object[]
        {
            new { body = "I can reproduce it on BOV", system = false, author = new { name = "Aart" } },
            new { body = "changed the description", system = true, author = new { name = "Aart" } }
        });

        var issue = await New().FetchAsync(Link, CancellationToken.None);

        issue!.Comments.Should().ContainSingle()
             .Which.Should().Be("Aart: I can reproduce it on BOV");
    }

    [Fact]
    public async Task Returns_null_when_no_token_is_configured()
    {
        StubIssue(Issue());
        StubNotes(Array.Empty<object>());

        var issue = await New(withToken: false).FetchAsync(Link, CancellationToken.None);

        issue.Should().BeNull();
    }

    // A work_items URL can name a Task or an Epic, which /issues/:iid does not serve. That must
    // cost the ticket its linked context, never its estimate.
    [Fact]
    public async Task Returns_null_rather_than_throwing_when_the_issue_is_not_found()
    {
        StubIssue(new { message = "404 Not found" }, status: 404);

        var issue = await New().FetchAsync(Link, CancellationToken.None);

        issue.Should().BeNull();
    }

    [Fact]
    public async Task Still_returns_the_issue_when_the_notes_call_fails()
    {
        StubIssue(Issue());
        StubNotes(new { message = "403 Forbidden" }, status: 403);

        var issue = await New().FetchAsync(Link, CancellationToken.None);

        issue.Should().NotBeNull();
        issue!.Comments.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_null_when_gitlab_is_unreachable()
    {
        _server.Stop();

        var issue = await New().FetchAsync(Link, CancellationToken.None);

        issue.Should().BeNull();
    }

    // A linked issue must not be able to outweigh the ticket it is linked from.
    [Fact]
    public async Task Truncates_a_very_long_description()
    {
        StubIssue(Issue(description: new string('x', 20000)));
        StubNotes(Array.Empty<object>());

        var issue = await New().FetchAsync(Link, CancellationToken.None);

        issue!.Description.Length.Should().BeLessThan(9000);
        issue.Description.Should().EndWith("[truncated]");
    }

    [Fact]
    public async Task Caps_how_many_comments_it_carries()
    {
        StubIssue(Issue());
        StubNotes(Enumerable.Range(1, 50)
            .Select(i => (object)new { body = $"comment {i}", system = false, author = new { name = "Aart" } })
            .ToArray());

        var issue = await New().FetchAsync(Link, CancellationToken.None);

        issue!.Comments.Should().HaveCount(10);
    }

    [Fact]
    public async Task Ignores_a_note_with_an_empty_body()
    {
        StubIssue(Issue());
        StubNotes(new object[]
        {
            new { body = "   ", system = false, author = new { name = "Aart" } },
            new { body = "real", system = false, author = new { name = "Aart" } }
        });

        var issue = await New().FetchAsync(Link, CancellationToken.None);

        issue!.Comments.Should().ContainSingle().Which.Should().Be("Aart: real");
    }

    [Fact]
    public async Task Tolerates_an_issue_with_a_null_description()
    {
        StubIssue(new { iid = 377, title = "t", description = (string?)null });
        StubNotes(Array.Empty<object>());

        var issue = await New().FetchAsync(Link, CancellationToken.None);

        issue.Should().NotBeNull();
        issue!.Description.Should().BeEmpty();
    }
}
