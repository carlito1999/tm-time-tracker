using FluentAssertions;
using TmTimeTracker.Configuration;
using TmTimeTracker.Jira;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace TmTimeTracker.Tests.Jira;

public class JiraOAuthClientTests : IDisposable
{
    private readonly WireMockServer _server;

    public JiraOAuthClientTests()
    {
        _server = WireMockServer.Start();
    }

    public void Dispose() => _server.Dispose();

    private JiraOAuthClient NewClient() => new(
        new HttpClient { BaseAddress = new Uri(_server.Url!) },
        new AtlassianSecrets
        {
            OAuthClientId = "client-id",
            OAuthClientSecret = "client-secret",
            RedirectUri = "http://localhost:53682/callback"
        },
        oauthBaseOverride: _server.Url!);

    [Fact]
    public async Task ExchangeCode_returns_tokens()
    {
        _server.Given(Request.Create().WithPath("/oauth/token").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithBodyAsJson(new
                   {
                       access_token = "at",
                       refresh_token = "rt",
                       expires_in = 3600,
                       scope = "read:jira-work"
                   }));

        var resp = await NewClient().ExchangeCodeAsync("auth-code", CancellationToken.None);

        resp.AccessToken.Should().Be("at");
        resp.RefreshToken.Should().Be("rt");
        resp.ExpiresInSeconds.Should().Be(3600);
    }

    [Fact]
    public async Task Refresh_returns_new_tokens()
    {
        _server.Given(Request.Create().WithPath("/oauth/token").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithBodyAsJson(new
                   {
                       access_token = "new-at",
                       refresh_token = "new-rt",
                       expires_in = 3600,
                       scope = "read:jira-work"
                   }));

        var resp = await NewClient().RefreshAsync("old-rt", CancellationToken.None);
        resp.AccessToken.Should().Be("new-at");
        resp.RefreshToken.Should().Be("new-rt");
    }

    [Fact]
    public void BuildAuthorizationUrl_includes_all_required_params()
    {
        var url = NewClient().BuildAuthorizationUrl(state: "xyz");
        url.Should().Contain("audience=api.atlassian.com");
        url.Should().Contain("client_id=client-id");
        url.Should().Contain("read%3Ajira-work");
        url.Should().Contain("write%3Ajira-work");
        url.Should().Contain("offline_access");
        url.Should().Contain("state=xyz");
        url.Should().Contain("response_type=code");
        url.Should().Contain("prompt=consent");
    }
}
