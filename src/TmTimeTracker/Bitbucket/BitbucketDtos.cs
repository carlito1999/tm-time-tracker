using System.Text.Json.Serialization;

namespace TmTimeTracker.Bitbucket;

/// <summary>
/// The slice of Bitbucket's pull request representation the announcement needs. Everything else in
/// a very large payload is left unread, and <c>fields</c> on the request keeps it off the wire.
/// </summary>
public sealed record BitbucketPullRequestPage(
    [property: JsonPropertyName("values")] BitbucketPullRequest[]? Values);

public sealed record BitbucketPullRequest(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("updated_on")] string? UpdatedOn,
    [property: JsonPropertyName("source")] BitbucketEndpoint? Source,
    [property: JsonPropertyName("links")] BitbucketLinks? Links);

public sealed record BitbucketEndpoint(
    [property: JsonPropertyName("branch")] BitbucketBranch? Branch);

public sealed record BitbucketBranch(
    [property: JsonPropertyName("name")] string? Name);

public sealed record BitbucketLinks(
    [property: JsonPropertyName("html")] BitbucketLink? Html);

public sealed record BitbucketLink(
    [property: JsonPropertyName("href")] string? Href);
