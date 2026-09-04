using System.Text.Json.Serialization;

namespace TmTimeTracker.GitLab;

public sealed class GitLabIssueDto
{
    [JsonPropertyName("iid")] public long Iid { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
}

public sealed class GitLabNoteDto
{
    [JsonPropertyName("body")] public string? Body { get; set; }

    /// <summary>
    /// True for GitLab's own activity entries - "changed the description", label changes and so
    /// on. They are noise in an estimation prompt and would crowd out the human discussion.
    /// </summary>
    [JsonPropertyName("system")] public bool System { get; set; }

    [JsonPropertyName("author")] public GitLabAuthorDto? Author { get; set; }
}

public sealed class GitLabAuthorDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
}
