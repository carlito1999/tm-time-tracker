using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;

namespace TmTimeTracker.GitLab;

public interface IGitLabIssueSource
{
    Task<LinkedIssue?> FetchAsync(GitLabIssueRef link, CancellationToken ct);
}

/// <summary>
/// Reads a linked GitLab issue so the estimator can see what the ticket is actually about.
///
/// Tickets in the SN project are routinely a bare GitLab link and nothing else. SN-305's entire
/// description was a link to work item 377, and the estimate came back saying "the actual root
/// cause is unknown from the ticket alone" - behind that link were three named animals, a named
/// breeder, and the detail that narrows the whole search: the animal's details render fine and
/// only the ancestry overview is missing.
///
/// The daemon fetches rather than the estimation run. That run is launched with --restricted,
/// which removes both WebFetch and Bash, and WebFetch could not send an auth header even if it
/// were granted. Fetching here also keeps the token out of the prompt, out of the child's
/// context, and out of the raw_output this app stores on every ticket_estimate row forever.
///
/// Needs a Personal Access Token carrying read_api. The credential Git Credential Manager holds
/// for gitlab.com is git-transport only and answers 403 insufficient_scope to every REST call,
/// so it is not a fallback.
///
/// Every failure returns null and logs. A ticket whose link is unreachable, deleted, or actually
/// an epic must still be estimated from its Jira text alone.
/// </summary>
public sealed class GitLabApiClient : IGitLabIssueSource
{
    private const string DefaultApiBase = "https://gitlab.com/api/v4";

    // Matches EstimatePromptBuilder's own cap: a linked issue must not be able to outweigh the
    // ticket it is linked from.
    private const int MaxDescriptionChars = 8000;
    private const int MaxComments = 10;
    private const int MaxCommentChars = 1000;

    private readonly HttpClient _http;
    private readonly GitLabApiTokenRepository _credentials;
    private readonly ILogger<GitLabApiClient> _log;
    private readonly string _apiBase;

    private bool _warnedUnconfigured;

    public GitLabApiClient(
        HttpClient http,
        GitLabApiTokenRepository credentials,
        ILogger<GitLabApiClient> log,
        string? apiBaseOverride = null)
    {
        _http = http;
        _credentials = credentials;
        _log = log;
        _apiBase = (apiBaseOverride ?? DefaultApiBase).TrimEnd('/');
    }

    public async Task<LinkedIssue?> FetchAsync(GitLabIssueRef link, CancellationToken ct)
    {
        var token = _credentials.Get()?.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            // Once per process: an unconfigured token is a standing condition, not an event.
            if (!_warnedUnconfigured)
            {
                _warnedUnconfigured = true;
                _log.LogInformation(
                    "No GitLab token saved, so linked issues will not be read. Add one on the "
                    + "API tokens tab in Settings.");
            }
            return null;
        }

        // Groups nest, so the project path contains slashes and has to become one path segment.
        var project = Uri.EscapeDataString(link.ProjectPath);
        var issueUrl = $"{_apiBase}/projects/{project}/issues/{link.Iid}";

        var issue = await GetAsync<GitLabIssueDto>(issueUrl, token, ct).ConfigureAwait(false);
        if (issue is null) return null;

        var notes = await GetAsync<List<GitLabNoteDto>>($"{issueUrl}/notes", token, ct)
            .ConfigureAwait(false);

        return new LinkedIssue(
            link.Url,
            link.Iid,
            link.ProjectPath,
            (issue.Title ?? "").Trim(),
            Truncate(issue.Description, MaxDescriptionChars),
            Comments(notes));
    }

    private async Task<T?> GetAsync<T>(string url, string token, CancellationToken ct)
        where T : class
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("PRIVATE-TOKEN", token);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // The URL is safe to log: the token travels as a header and never appears in it.
                _log.LogInformation(
                    "GitLab returned {Status} for {Url}", (int)response.StatusCode, url);
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read {Url} from GitLab", url);
            return null;
        }
    }

    private static IReadOnlyList<string> Comments(List<GitLabNoteDto>? notes)
    {
        if (notes is null) return Array.Empty<string>();

        var kept = new List<string>();
        foreach (var note in notes)
        {
            if (note.System) continue;
            if (string.IsNullOrWhiteSpace(note.Body)) continue;

            var author = string.IsNullOrWhiteSpace(note.Author?.Name) ? "someone" : note.Author!.Name;
            kept.Add($"{author}: {Truncate(note.Body, MaxCommentChars)}");
            if (kept.Count == MaxComments) break;
        }

        return kept;
    }

    private static string Truncate(string? value, int max)
    {
        var text = (value ?? "").Trim();
        return text.Length <= max ? text : text[..max] + "\n\n[truncated]";
    }
}
