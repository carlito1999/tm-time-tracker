using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TmTimeTracker.Slack;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace TmTimeTracker.Tests.Slack;

public class SlackApiClientTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Dispose();

    private sealed class FixedToken : ISlackTokenSource
    {
        private readonly string? _token;
        public FixedToken(string? token) => _token = token;
        public string? GetToken() => _token;
    }

    private SlackApiClient New(string? token = "xoxp-1") =>
        new(new HttpClient(), new FixedToken(token),
            NullLogger<SlackApiClient>.Instance, apiBaseOverride: _server.Url);

    [Fact]
    public async Task AuthTest_returns_identity()
    {
        _server.Given(Request.Create().WithPath("/auth.test").UsingPost()
                        .WithHeader("Authorization", "Bearer xoxp-1"))
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   ok = true, user_id = "U1", user = "lefteris", team = "thecube"
               }));

        var identity = await New().AuthTestAsync(CancellationToken.None);
        identity.UserId.Should().Be("U1");
        identity.UserName.Should().Be("lefteris");
        identity.TeamName.Should().Be("thecube");
    }

    // Slack signals failure with HTTP 200 and ok:false, so the envelope must be checked.
    [Fact]
    public async Task Not_ok_response_throws_with_the_slack_error_code()
    {
        _server.Given(Request.Create().WithPath("/auth.test").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                        .WithBodyAsJson(new { ok = false, error = "invalid_auth" }));

        var act = () => New().AuthTestAsync(CancellationToken.None);
        (await act.Should().ThrowAsync<SlackApiException>())
            .Which.SlackError.Should().Be("invalid_auth");
    }

    [Fact]
    public async Task Missing_token_throws_before_any_request()
    {
        var act = () => New(token: null).AuthTestAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<SlackApiException>())
            .Which.SlackError.Should().Be("not_configured");
        _server.LogEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task PostMessage_sends_channel_and_text_and_never_sends_as_user()
    {
        _server.Given(Request.Create().WithPath("/chat.postMessage").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                        .WithBodyAsJson(new { ok = true }));

        await New().PostMessageAsync("C1", "hello", CancellationToken.None);

        var body = _server.LogEntries.Single().RequestMessage.Body ?? "";
        body.Should().Contain("\"channel\":\"C1\"");
        body.Should().Contain("\"text\":\"hello\"");
        body.Should().NotContain("as_user");
    }

    [Fact]
    public async Task PostMessage_surfaces_channel_not_found()
    {
        _server.Given(Request.Create().WithPath("/chat.postMessage").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                        .WithBodyAsJson(new { ok = false, error = "channel_not_found" }));

        var act = () => New().PostMessageAsync("C-nope", "hi", CancellationToken.None);
        (await act.Should().ThrowAsync<SlackApiException>())
            .Which.SlackError.Should().Be("channel_not_found");
    }

    [Fact]
    public async Task ListConversations_follows_the_cursor()
    {
        _server.Given(Request.Create().WithPath("/conversations.list").UsingPost()
                        .WithBody(b => b != null && !b.Contains("cursor=")))
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   ok = true,
                   channels = new[] { new { id = "C1", name = "sheeponline" } },
                   response_metadata = new { next_cursor = "CUR2" }
               }));
        _server.Given(Request.Create().WithPath("/conversations.list").UsingPost()
                        .WithBody(b => b != null && b.Contains("cursor=CUR2")))
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   ok = true,
                   channels = new[] { new { id = "C2", name = "training-manager" } },
                   response_metadata = new { next_cursor = "" }
               }));

        var channels = await New().ListConversationsAsync(CancellationToken.None);

        channels.Select(c => c.Name).Should().BeEquivalentTo("sheeponline", "training-manager");
    }

    [Fact]
    public async Task ListConversations_requests_private_channels_too()
    {
        _server.Given(Request.Create().WithPath("/conversations.list").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                        .WithBodyAsJson(new { ok = true, channels = Array.Empty<object>() }));

        await New().ListConversationsAsync(CancellationToken.None);

        var body = _server.LogEntries.Single().RequestMessage.Body ?? "";
        Uri.UnescapeDataString(body).Should().Contain("public_channel,private_channel");
    }

    [Fact]
    public async Task Rate_limit_is_retried_once_after_honouring_retry_after()
    {
        _server.Given(Request.Create().WithPath("/chat.postMessage").UsingPost())
               .InScenario("ratelimit").WillSetStateTo("retried")
               .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.TooManyRequests)
                        .WithHeader("Retry-After", "0"));

        _server.Given(Request.Create().WithPath("/chat.postMessage").UsingPost())
               .InScenario("ratelimit").WhenStateIs("retried")
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { ok = true }));

        await New().PostMessageAsync("C1", "hi", CancellationToken.None);

        _server.LogEntries.Should().HaveCount(2);
    }
}
