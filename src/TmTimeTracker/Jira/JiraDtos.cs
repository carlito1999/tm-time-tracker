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

// Optional members carry defaults and stay last so existing construction sites keep compiling.
// Description is a JsonElement because Jira v3 returns Atlassian Document Format - a nested
// tree, not a string. Flatten it with AdfText.
public sealed record IssueFields(
    [property: JsonPropertyName("status")] IssueStatus Status,
    [property: JsonPropertyName("summary")] string? Summary = null,
    [property: JsonPropertyName("timetracking")] JiraTimeTracking? TimeTracking = null,
    [property: JsonPropertyName("description")] System.Text.Json.JsonElement? Description = null,
    [property: JsonPropertyName("attachment")] JiraAttachment[]? Attachments = null);

/// <summary>
/// A file on the issue. Tickets here are often nothing but a screenshot - the description ADF
/// carries a media node with no text at all - so the attachments are the only place the actual
/// content lives.
/// </summary>
public sealed record JiraAttachment(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("mimeType")] string? MimeType,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("content")] string? Content)
{
    public bool IsImage =>
        MimeType is not null &&
        MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}

// originalEstimateSeconds is the field gate 4 compares against: Jira accepts a write expressed
// in its own duration syntax but reports the stored value back in seconds.
public sealed record JiraTimeTracking(
    [property: JsonPropertyName("originalEstimateSeconds")] int? OriginalEstimateSeconds);

public sealed record JiraSearchResponse(
    [property: JsonPropertyName("issues")] Issue[]? Issues);

public sealed record JiraProject(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name);

public sealed record JiraProjectPage(
    [property: JsonPropertyName("values")] JiraProject[] Values);

// Id is optional and last so existing construction sites keep compiling. It is needed because
// the development-information endpoint keys on the numeric issue id, not the issue key.
public sealed record Issue(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("fields")] IssueFields Fields,
    [property: JsonPropertyName("id")] string? Id = null);

// Shape of /rest/dev-status/1.0/issue/detail?applicationType=bitbucket&dataType=pullrequest
public sealed record DevStatusResponse(
    [property: JsonPropertyName("detail")] DevStatusDetail[]? Detail)
{
    public static readonly DevStatusResponse Empty = new((DevStatusDetail[]?)null);
}

public sealed record DevStatusDetail(
    [property: JsonPropertyName("pullRequests")] DevStatusPullRequest[]? PullRequests,
    [property: JsonPropertyName("branches")] DevStatusBranch[]? Branches);

public sealed record DevStatusBranch(
    [property: JsonPropertyName("lastCommit")] DevStatusCommit? LastCommit);

public sealed record DevStatusCommit(
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("authorTimestamp")] string? AuthorTimestamp = null);

public sealed record DevStatusPullRequest(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("repositoryName")] string? RepositoryName,
    [property: JsonPropertyName("lastUpdate")] string? LastUpdate,
    [property: JsonPropertyName("source")] DevStatusPullRequestRef? Source = null);

/// <summary>
/// The branch a pull request was opened from. dev-status has always sent this; it went unread
/// until the selector needed it to tell one ticket's pull requests from another's.
/// </summary>
public sealed record DevStatusPullRequestRef(
    [property: JsonPropertyName("branch")] string? Branch);

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
