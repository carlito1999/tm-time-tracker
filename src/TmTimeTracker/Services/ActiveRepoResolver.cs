using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using TmTimeTracker.Platform;

namespace TmTimeTracker.Services;

public sealed record ActiveResolution(string RepoPath, string? Branch, string? TicketKey);

public sealed class ActiveRepoResolver
{
    private static readonly TimeSpan ActivityWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ClaudeActivityWindow = TimeSpan.FromSeconds(60);
    private static readonly string[] VsCodeProcessNames = { "Code", "Code.exe" };

    private readonly TrackedRepoRepository _repos;
    private readonly IForegroundWindowProbe _foreground;
    private readonly IClaudeCodeActivityProbe _claude;
    private readonly IGitBranchProbe _git;
    private readonly IClock _clock;
    private readonly ILogger<ActiveRepoResolver> _log;
    private readonly HashSet<string> _warnedMissing = new(StringComparer.OrdinalIgnoreCase);
    private ActiveResolution? _last;

    public ActiveRepoResolver(TrackedRepoRepository repos, IForegroundWindowProbe foreground,
        IClaudeCodeActivityProbe claude, IGitBranchProbe git, IClock clock,
        ILogger<ActiveRepoResolver> log)
    {
        _repos = repos; _foreground = foreground; _claude = claude;
        _git = git; _clock = clock; _log = log;
    }

    /// <summary>Most recent resolution result. Used by IdleMonitor to gate Claude-active override.</summary>
    public ActiveResolution? LastResolution => _last;

    public ActiveResolution? Resolve()
    {
        var tracked = _repos.GetAll();
        if (tracked.Count == 0)
        {
            _last = null;
            return null;
        }

        var now = _clock.UtcNow;

        // Tier 0: Claude Code wrote within last 60s for one of our tracked repos
        var claudeSnap = _claude.Snapshot();
        var freshClaude = tracked
            .Select(r => (repo: r, mtime: GetClaudeMtime(claudeSnap, r.Path)))
            .Where(x => x.mtime.HasValue && (now - x.mtime!.Value) <= ClaudeActivityWindow)
            .OrderByDescending(x => x.mtime!.Value)
            .Select(x => x.repo)
            .FirstOrDefault();
        if (freshClaude is not null && Directory.Exists(freshClaude.Path))
        {
            _last = LookupBranch(freshClaude.Path);
            return _last;
        }

        // Tier 1: VS Code window title
        var fg = _foreground.Probe();
        if (fg is not null && IsVsCode(fg.ProcessName))
        {
            var folder = VsCodeWindowTitleParser.Parse(fg.Title);
            if (folder is not null)
            {
                var match = tracked.FirstOrDefault(r =>
                    string.Equals(Path.GetFileName(r.Path), folder, StringComparison.OrdinalIgnoreCase));
                if (match is not null && Directory.Exists(match.Path))
                {
                    _last = LookupBranch(match.Path);
                    return _last;
                }
            }
        }

        // Tier 2: recent activity polling
        var fresh = tracked
            .Select(r => (r, mtime: TryReadHeadMtime(r.Path)))
            .Where(x => x.mtime.HasValue && (now - x.mtime!.Value) <= ActivityWindow)
            .OrderByDescending(x => x.mtime!.Value)
            .Select(x => x.r)
            .FirstOrDefault();
        if (fresh is not null)
        {
            _last = LookupBranch(fresh.Path);
            return _last;
        }

        // Tier 3: sticky — return previous
        return _last;
    }

    private static bool IsVsCode(string processName) =>
        VsCodeProcessNames.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));

    private static DateTime? GetClaudeMtime(IReadOnlyDictionary<string, DateTime> snap, string repoPath)
    {
        var slug = ClaudeProjectSlug.FromPath(repoPath);
        return snap.TryGetValue(slug, out var mtime) ? mtime : null;
    }

    private ActiveResolution? LookupBranch(string repoPath)
    {
        if (!Directory.Exists(repoPath))
        {
            WarnMissingOnce(repoPath);
            return null;
        }
        var branch = _git.GetCurrentBranch(repoPath);
        var ticket = branch is null ? null : TicketKeyExtractor.Extract(branch);
        return new ActiveResolution(repoPath, branch, ticket);
    }

    private DateTime? TryReadHeadMtime(string repoPath)
    {
        try
        {
            var head = Path.Combine(repoPath, ".git", "HEAD");
            if (!File.Exists(head)) return null;
            return File.GetLastWriteTimeUtc(head);
        }
        catch
        {
            WarnMissingOnce(repoPath);
            return null;
        }
    }

    private void WarnMissingOnce(string path)
    {
        if (_warnedMissing.Add(path))
            _log.LogWarning("Tracked repo path is not accessible: {Path}", path);
    }
}
