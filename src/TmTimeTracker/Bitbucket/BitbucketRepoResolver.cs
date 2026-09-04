using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using TmTimeTracker.Platform;

namespace TmTimeTracker.Bitbucket;

/// <summary>
/// Works out which Bitbucket repositories a ticket's pull request could live in.
///
/// An announcement knows only a ticket key, and Bitbucket is addressed by workspace and slug. The
/// route between them already exists: the ticket's project key maps to tracked repo paths, and
/// each path's git remote names the repository. No per-repo configuration is involved, so a repo
/// that is cloned and tracked is queryable straight away.
/// </summary>
public sealed class BitbucketRepoResolver
{
    private readonly RepoProjectRepository _projects;
    private readonly IGitRemoteProbe _remotes;

    public BitbucketRepoResolver(RepoProjectRepository projects, IGitRemoteProbe remotes)
    {
        _projects = projects;
        _remotes = remotes;
    }

    public IReadOnlyList<BitbucketRepo> ReposFor(string ticketKey)
    {
        var project = JiraProjectKey.From(ticketKey);
        if (project is null) return Array.Empty<BitbucketRepo>();

        return _projects.GetAll()
            .Where(m => string.Equals(m.ProjectKey, project, StringComparison.OrdinalIgnoreCase))
            .Select(m => BitbucketRemote.Parse(_remotes.GetRemoteUrl(m.RepoPath)))
            .OfType<BitbucketRepo>()
            // Two clones of one repository are one repository; querying it twice would double the
            // candidates and every request.
            .DistinctBy(r => (r.Workspace.ToLowerInvariant(), r.Slug.ToLowerInvariant()))
            .ToList();
    }
}
