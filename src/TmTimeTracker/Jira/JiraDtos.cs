using System.Text.Json.Serialization;

namespace TmTimeTracker.Jira;

public sealed record AtlassianResource(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("scopes")] string[] Scopes);

public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresInSeconds,
    [property: JsonPropertyName("scope")] string Scope);

public sealed record IssueStatus(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("statusCategory")] StatusCategory Category);

public sealed record StatusCategory(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name);

public sealed record IssueFields(
    [property: JsonPropertyName("status")] IssueStatus Status);

public sealed record Issue(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("fields")] IssueFields Fields);

public sealed record WorklogRequest(
    [property: JsonPropertyName("timeSpentSeconds")] int TimeSpentSeconds,
    [property: JsonPropertyName("started")] string StartedIso,
    [property: JsonPropertyName("comment")] WorklogComment Comment);

public sealed record WorklogComment(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("content")] WorklogContent[] Content);

public sealed record WorklogContent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("content")] WorklogTextNode[] Content);

public sealed record WorklogTextNode(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text);

public sealed record WorklogResponse(
    [property: JsonPropertyName("id")] string Id);
