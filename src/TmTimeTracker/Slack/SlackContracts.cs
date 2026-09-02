using System.Text.Json.Serialization;

namespace TmTimeTracker.Slack;

/// <summary>Supplies the Slack user token. Indirection exists so tests need no database.</summary>
public interface ISlackTokenSource
{
    string? GetToken();
}

/// <summary>Narrow write seam so the notifier can be tested without an HTTP client.</summary>
public interface ISlackPoster
{
    Task PostMessageAsync(string channelId, string text, CancellationToken ct);
}

public sealed record SlackIdentity(string UserId, string UserName, string TeamName);

public sealed record SlackConversation(string Id, string Name);

public sealed class SlackApiException : Exception
{
    /// <summary>The raw Slack error code, e.g. "invalid_auth" or "channel_not_found".</summary>
    public string SlackError { get; }

    public SlackApiException(string slackError)
        : base($"Slack API returned an error: {slackError}") => SlackError = slackError;
}

internal interface ISlackEnvelope
{
    bool Ok { get; }
    string? Error { get; }
}

internal sealed record AuthTestResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("user_id")] string? UserId,
    [property: JsonPropertyName("user")] string? User,
    [property: JsonPropertyName("team")] string? Team) : ISlackEnvelope;

internal sealed record ConversationDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);

internal sealed record ResponseMetadata(
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

internal sealed record ConversationsListResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("channels")] ConversationDto[]? Channels,
    [property: JsonPropertyName("response_metadata")] ResponseMetadata? Metadata) : ISlackEnvelope;

internal sealed record PostMessageResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string? Error) : ISlackEnvelope;
