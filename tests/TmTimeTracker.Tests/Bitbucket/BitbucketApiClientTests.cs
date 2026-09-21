using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TmTimeTracker.Bitbucket;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using TmTimeTracker.Tests.Data;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace TmTimeTracker.Tests.Bitbucket;

public class BitbucketApiClientTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Dispose();

    private static readonly BitbucketRepo Repo = new("thecubeee", "sheeponline-new");

    private sealed class PassthroughProtector : TmTimeTracker.Platform.ITokenProtector
    {
        public byte[] Protect(string value) => System.Text.Encoding.UTF8.GetBytes(value);
        public string Unprotect(byte[] value) => System.Text.Encoding.UTF8.GetString(value);
    }

    private BitbucketApiClient New(bool withToken = true)
    {
        var factory = SharedSqlite.NewInMemory();
        new DatabaseInitializer(factory).EnsureCreated();
        var tokens = new BitbucketApiTokenRepository(factory, new PassthroughProtector());
        if (withToken) tokens.Save("lefteris@thecube.dev", "scoped-token");

        return new BitbucketApiClient(
            new HttpClient(), tokens, NullLogger<BitbucketApiClient>.Instance,
            apiBaseOverride: _server.Url);
    }

    private static object Page(params object[] values) => new { values };

    private static object Pr(
        long id, string state = "OPEN", string branch = "SN-291-350-individual-breeders") => new
        {
            id,
            title = $"PR {id}",
            state,
            updated_on = "2026-09-04T05:04:17.554000+00:00",
            source = new { branch = new { name = branch } },
            links = new { html = new { href = $"https://bitbucket.org/thecubeee/sheeponline-new/pull-requests/{id}" } }
        };

    [Fact]
    public async Task Maps_a_pull_request_to_a_candidate()
    {
        _server.Given(Request.Create().WithPath("/repositories/thecubeee/sheeponline-new/pullrequests").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Page(Pr(367))));

        var result = await New().GetPullRequestsAsync(Repo, "SN-291", CancellationToken.None);

        var pr = result.Should().ContainSingle().Subject;
        pr.Id.Should().Be("367");
        pr.Status.Should().Be("OPEN");
        pr.SourceBranch.Should().Be("SN-291-350-individual-breeders");
        pr.Url.Should().Be("https://bitbucket.org/thecubeee/sheeponline-new/pull-requests/367");
        pr.RepositoryName.Should().Be("sheeponline-new");
    }

    // Scoping the query to the ticket's branches is what stops the whole repository being pulled
    // down, and is the reason another ticket's pull request can never reach the selector.
    [Fact]
    public async Task Asks_only_for_pull_requests_on_branches_carrying_the_ticket_key()
    {
        _server.Given(Request.Create()
                        .WithPath("/repositories/thecubeee/sheeponline-new/pullrequests")
                        .WithParam("q", new WireMock.Matchers.WildcardMatcher("*SN-291*"))
                        .UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Page(Pr(367))));

        var result = await New().GetPullRequestsAsync(Repo, "SN-291", CancellationToken.None);

        result.Should().ContainSingle();
    }

    // The old pull requests on a re-PR'd branch stay OPEN, but a merged one still has to come back
    // so the selector can fall back to it when nothing is open.
    [Fact]
    public async Task Returns_pull_requests_in_every_state()
    {
        _server.Given(Request.Create().WithPath("/repositories/thecubeee/sheeponline-new/pullrequests").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200)
                        .WithBodyAsJson(Page(Pr(367), Pr(353, state: "MERGED"))));

        var result = await New().GetPullRequestsAsync(Repo, "SN-291", CancellationToken.None);

        result.Select(p => p.Id).Should().BeEquivalentTo("367", "353");
    }

    [Fact]
    public async Task Authenticates_with_the_stored_credential()
    {
        // "lefteris@thecube.dev:scoped-token" base64-encoded.
        _server.Given(Request.Create().WithPath("/repositories/thecubeee/sheeponline-new/pullrequests")
                        .WithHeader("Authorization", "Basic bGVmdGVyaXNAdGhlY3ViZS5kZXY6c2NvcGVkLXRva2Vu")
                        .UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Page(Pr(367))));

        var result = await New().GetPullRequestsAsync(Repo, "SN-291", CancellationToken.None);

        result.Should().ContainSingle();
    }

    // A user who never set the Bitbucket token must fall through to the dev-status path rather
    // than have the announcement blow up.
    [Fact]
    public async Task Returns_empty_without_calling_bitbucket_when_no_token_is_stored()
    {
        var result = await New(withToken: false)
            .GetPullRequestsAsync(Repo, "SN-291", CancellationToken.None);

        result.Should().BeEmpty();
        _server.LogEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_empty_rather_than_throwing_when_bitbucket_fails()
    {
        _server.Given(Request.Create().WithPath("/repositories/thecubeee/sheeponline-new/pullrequests").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(401));

        var result = await New().GetPullRequestsAsync(Repo, "SN-291", CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Skips_pull_requests_without_a_browsable_url()
    {
        _server.Given(Request.Create().WithPath("/repositories/thecubeee/sheeponline-new/pullrequests").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   values = new object[] { new { id = 367, title = "PR 367", state = "OPEN" } }
               }));

        var result = await New().GetPullRequestsAsync(Repo, "SN-291", CancellationToken.None);

        result.Should().BeEmpty();
    }
}
