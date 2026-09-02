using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace TmTimeTracker.Slack;

/// <summary>
/// Slack Web API client authenticating with a USER token (xoxp-), so messages are authored by the
/// user rather than a bot. That also means the token inherits the user's channel membership, so no
/// bot needs inviting into private channels.
/// </summary>
public sealed class SlackApiClient : ISlackPoster
{
    private const string DefaultApiBase = "https://slack.com/api";

    private readonly HttpClient _http;
    private readonly ISlackTokenSource _tokens;
    private readonly ILogger<SlackApiClient> _log;
    private readonly string _apiBase;

    public SlackApiClient(HttpClient http, ISlackTokenSource tokens,
        ILogger<SlackApiClient> log, string? apiBaseOverride = null)
    {
        _http = http; _tokens = tokens; _log = log;
        _apiBase = (apiBaseOverride ?? DefaultApiBase).TrimEnd('/');
    }

    public async Task<SlackIdentity> AuthTestAsync(CancellationToken ct)
    {
        var body = await SendAsync<AuthTestResponse>("auth.test", () => null, ct).ConfigureAwait(false);
        return new SlackIdentity(body.UserId ?? "", body.User ?? "", body.Team ?? "");
    }

    public async Task<IReadOnlyList<SlackConversation>> ListConversationsAsync(CancellationToken ct)
    {
        var all = new List<SlackConversation>();
        string? cursor = null;

        do
        {
            var pageCursor = cursor;
            var page = await SendAsync<ConversationsListResponse>("conversations.list", () =>
            {
                var form = new Dictionary<string, string>
                {
                    ["types"] = "public_channel,private_channel",
                    ["exclude_archived"] = "true",
                    ["limit"] = "200"
                };
                if (!string.IsNullOrEmpty(pageCursor)) form["cursor"] = pageCursor;
                return new FormUrlEncodedContent(form);
            }, ct).ConfigureAwait(false);

            foreach (var c in page.Channels ?? Array.Empty<ConversationDto>())
                all.Add(new SlackConversation(c.Id, c.Name));

            cursor = page.Metadata?.NextCursor;
        }
        while (!string.IsNullOrEmpty(cursor));

        return all;
    }

    public async Task PostMessageAsync(string channelId, string text, CancellationToken ct)
    {
        // as_user is deliberately never sent: it is legacy and returns as_user_not_supported on
        // modern apps. Authorship comes from the token type instead.
        await SendAsync<PostMessageResponse>("chat.postMessage",
            () => JsonContent.Create(new { channel = channelId, text }), ct).ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(string method, Func<HttpContent?> content, CancellationToken ct)
        where T : class, ISlackEnvelope
    {
        var token = _tokens.GetToken();
        if (string.IsNullOrWhiteSpace(token))
            throw new SlackApiException("not_configured");

        var response = await SendOnceAsync(method, content(), token, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
            _log.LogWarning("Slack rate-limited {Method}; retrying once after {Seconds}s",
                method, wait.TotalSeconds);
            response.Dispose();
            await Task.Delay(wait, ct).ConfigureAwait(false);
            // Content is rebuilt: an HttpContent already sent cannot be re-sent.
            response = await SendOnceAsync(method, content(), token, ct).ConfigureAwait(false);
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
                                             .ConfigureAwait(false)
                       ?? throw new SlackApiException("empty_response");

            // Slack signals failure with HTTP 200 and ok:false, so the envelope must always be checked.
            if (!body.Ok) throw new SlackApiException(body.Error ?? "unknown_error");

            return body;
        }
    }

    private Task<HttpResponseMessage> SendOnceAsync(
        string method, HttpContent? content, string token, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiBase}/{method}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (content is not null) request.Content = content;
        return _http.SendAsync(request, ct);
    }
}
