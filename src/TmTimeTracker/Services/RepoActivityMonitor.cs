using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using TmTimeTracker.Platform;

namespace TmTimeTracker.Services;

/// <summary>One repo that earned a minute this tick, with the ticket to bill it to.</summary>
public sealed record RepoActivity(string RepoPath, string? Branch, string? TicketKey);

public interface IRepoActivitySource
{
    /// <param name="lastTickUtc">Claude writes count as fresh if they are newer than this.</param>
    /// <param name="humanActive">False when idle or locked. Suppresses the human stream only.</param>
    IReadOnlyList<RepoActivity> Sample(DateTime lastTickUtc, bool humanActive);
}

/// <summary>
/// The credit set for one minute: the repo the user is working in, plus every repo Claude Code
/// touched since the last tick. Two independent streams, so hand-editing one project while an
/// agent works another pays both and neither can take the other's minutes.
///
/// Claude streams deliberately ignore idle state and are never capped - crediting unattended
/// agent work is the whole reason the second stream exists.
/// </summary>
public sealed class RepoActivityMonitor : IRepoActivitySource
{
    private readonly TrackedRepoRepository _repos;
    private readonly ActiveRepoResolver _resolver;
    private readonly IClaudeCodeActivityProbe _claude;
    private readonly IGitBranchProbe _git;
    private readonly ILogger<RepoActivityMonitor> _log;
    private readonly HashSet<string> _warnedOnce = new(StringComparer.OrdinalIgnoreCase);

    public RepoActivityMonitor(TrackedRepoRepository repos, ActiveRepoResolver resolver,
        IClaudeCodeActivityProbe claude, IGitBranchProbe git,
        ILogger<RepoActivityMonitor> log)
    {
        _repos = repos; _resolver = resolver; _claude = claude; _git = git; _log = log;
    }

    public IReadOnlyList<RepoActivity> Sample(DateTime lastTickUtc, bool humanActive)
    {
        var byPath = new Dictionary<string, RepoActivity>(StringComparer.OrdinalIgnoreCase);

        // Human stream: at most one repo, and only while the user is actually at the desk.
        if (humanActive)
        {
            var human = _resolver.Resolve();
            if (human is not null && Directory.Exists(human.RepoPath))
                byPath[human.RepoPath] = new RepoActivity(human.RepoPath, human.Branch, human.TicketKey);
        }

        // Claude streams: any number of repos, regardless of idle. The resolver already looked up
        // a branch for the human's repo, so only repos it did not cover cost a git spawn.
        var snapshot = SafeSnapshot();
        if (snapshot.Count > 0)
        {
            foreach (var repo in _repos.GetAll())
            {
                if (byPath.ContainsKey(repo.Path)) continue;

                var slug = ClaudeProjectSlug.FromPath(repo.Path);
                if (!snapshot.TryGetValue(slug, out var mtime) || mtime <= lastTickUtc) continue;

                if (!Directory.Exists(repo.Path)) { WarnOnce(repo.Path); continue; }

                var branch = _git.GetCurrentBranch(repo.Path);
                var ticket = branch is null ? null : TicketKeyExtractor.Extract(branch);
                byPath[repo.Path] = new RepoActivity(repo.Path, branch, ticket);
            }
        }

        return byPath.Values.ToList();
    }

    private IReadOnlyDictionary<string, DateTime> SafeSnapshot()
    {
        try
        {
            return _claude.Snapshot();
        }
        catch (Exception ex)
        {
            if (_warnedOnce.Add("<claude-probe>"))
                _log.LogWarning(ex, "Claude activity probe failed; Claude streams idle this tick");
            return new Dictionary<string, DateTime>();
        }
    }

    private void WarnOnce(string path)
    {
        if (_warnedOnce.Add(path))
            _log.LogWarning("Tracked repo path is not accessible: {Path}", path);
    }
}
