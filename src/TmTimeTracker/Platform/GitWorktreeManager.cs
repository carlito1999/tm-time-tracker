using System.Globalization;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Configuration;
using TmTimeTracker.Logic;

namespace TmTimeTracker.Platform;

/// <summary>
/// Materialises a detached worktree at the remote's default branch, for Claude to estimate
/// against.
///
/// Two properties matter and both are load-bearing:
///
/// The worktree lives outside every tracked repo. FileClaudeCodeActivityProbe keys Claude
/// sessions by a slug derived from the working directory, and RepoActivityMonitor only ever
/// looks up slugs for tracked repo paths - so a session run here is invisible to it and cannot
/// credit minutes to whatever branch the repo happens to be on.
///
/// Neither `git fetch` nor `git worktree add` touches the source repo's .git/HEAD, which
/// ActiveRepoResolver reads as its activity signal. That is verified by a test rather than
/// assumed, because breaking it would silently inflate worklogs.
/// </summary>
public sealed class GitWorktreeManager : IGitWorktreeManager
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(5);

    // Below this a checkout is a stub, not a codebase. Low enough that a genuinely tiny repo
    // still estimates, high enough to catch a placeholder branch holding a .gitignore.
    private const int MinimumTrackedFiles = 5;

    private readonly string _estimatesRoot;
    private readonly ILogger<GitWorktreeManager> _log;

    public GitWorktreeManager(ILogger<GitWorktreeManager> log)
        : this(Path.Combine(AppPaths.DataDir, "estimates"), log) { }

    public GitWorktreeManager(string estimatesRoot, ILogger<GitWorktreeManager> log)
    {
        _estimatesRoot = estimatesRoot;
        _log = log;
    }

    public string PathFor(string repoPath) =>
        Path.Combine(_estimatesRoot, Sanitise(Path.GetFileName(repoPath.TrimEnd('/', '\\'))));

    public async Task<string> PrepareAsync(string repoPath, string? branchPattern,
        CancellationToken ct)
    {
        if (!Directory.Exists(repoPath))
            throw new GitWorktreeException($"Repository path does not exist: {repoPath}");

        var target = PathFor(repoPath);
        Directory.CreateDirectory(_estimatesRoot);

        // Bring the remote refs up to date first: "latest main" means latest on the remote, not
        // whatever this clone last saw.
        await RunAsync(repoPath, ct, "fetch", "--quiet", "origin");

        var reference = await ResolveBranchAsync(repoPath, branchPattern, ct);

        // A worktree left behind by the previous sweep would make `worktree add` fail outright.
        Detach(repoPath, target);

        await RunAsync(repoPath, ct, "worktree", "add", "--detach", "--quiet", target, reference);

        await EnsureNotEmptyAsync(repoPath, target, reference, ct);

        _log.LogDebug("Prepared estimation worktree for {Repo} at {Reference}", repoPath, reference);
        return target;
    }

    /// <summary>
    /// Refuses a checkout with no code in it.
    ///
    /// A repository's origin/HEAD can point at a stub branch while the real work happens
    /// elsewhere - one live repo had origin/HEAD on a branch holding a single .gitignore from an
    /// "Initial commit", with 2,000 files on the active branches. Estimating against that
    /// produced a confident, structurally valid, completely meaningless number, because nothing
    /// downstream can tell an empty repository from an easy ticket. Only refusing here can.
    /// </summary>
    private async Task EnsureNotEmptyAsync(string repoPath, string target, string reference,
        CancellationToken ct)
    {
        var listed = await TryRunAsync(target, ct, "ls-files");
        var fileCount = listed.Ok
            ? listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
            : 0;

        if (fileCount >= MinimumTrackedFiles) return;

        Detach(repoPath, target);
        throw new GitWorktreeException(
            $"{reference} holds only {fileCount} tracked file(s), so there is no code to estimate "
            + "against. Point the repository's default branch at the branch that carries the code "
            + "(git remote set-head origin <branch>).");
    }

    /// <summary>
    /// Recent commit subjects, used as the estimation prompt's reference class. Called against
    /// the prepared worktree, so the history is the default branch's rather than whatever the
    /// user has checked out locally.
    /// </summary>
    public async Task<IReadOnlyList<string>> RecentCommitSubjectsAsync(
        string repoPath, int count, CancellationToken ct)
    {
        var result = await TryRunAsync(repoPath, ct,
            "log", $"--max-count={count}", "--format=%s");

        // History is calibration, not a requirement: a shallow or empty repo still gets an
        // estimate, just without the reference class.
        if (!result.Ok) return Array.Empty<string>();

        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    public void Remove(string repoPath)
    {
        try
        {
            Detach(repoPath, PathFor(repoPath));
        }
        catch (Exception ex)
        {
            // A worktree that will not go away is not worth failing an estimate over; the next
            // sweep clears it before adding.
            _log.LogWarning(ex, "Could not remove the estimation worktree for {Repo}", repoPath);
        }
    }

    private void Detach(string repoPath, string target)
    {
        if (Directory.Exists(target))
            TryRun(repoPath, "worktree", "remove", "--force", target);

        if (Directory.Exists(target))
            ForceDelete(target);

        // Clears the administrative entry when the directory was deleted from underneath git.
        TryRun(repoPath, "worktree", "prune");
    }

    /// <summary>
    /// Picks the branch to estimate against: an explicit override if one is configured, else the
    /// remote's default.
    /// </summary>
    private async Task<string> ResolveBranchAsync(string repoPath, string? branchPattern,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(branchPattern))
            return await ResolveDefaultBranchAsync(repoPath, ct).ConfigureAwait(false);

        var pattern = branchPattern.Trim();
        if (!pattern.Contains('*', StringComparison.Ordinal))
            return $"origin/{pattern}";

        // Newest match wins, resolved fresh on every sweep so a rolling convention keeps working.
        var newest = await NewestMatchingAsync(repoPath, new[] { pattern }, ct).ConfigureAwait(false);

        if (newest is null)
            throw new GitWorktreeException(
                $"No remote branch matches '{pattern}' in {repoPath}.");

        _log.LogDebug("Branch pattern {Pattern} resolved to {Branch}", pattern, newest);
        return newest;
    }

    /// <summary>
    /// Finds the branch carrying current trunk work, without needing per-repo configuration.
    ///
    /// origin/HEAD is not reliable for this. One tracked repo points it at "mainTraining", a stub
    /// holding a single .gitignore from an Initial commit, while live work lands on rolling dated
    /// branches (dev-01-09-2026, dev-31-08-2026, ...). Trusting origin/HEAD there produced an
    /// estimate of an empty directory.
    ///
    /// So the trunk-shaped names are enumerated and the most recently committed one wins. That
    /// picks master in a repo that only has master, and the newest dev snapshot in a repo that
    /// cuts them by date - and it keeps working when the next one is cut. Note the stub above is
    /// itself matched by "main*"; it loses on date, which is exactly the intended behaviour.
    ///
    /// origin/HEAD remains the fallback for a repo whose trunk is named something else entirely.
    /// </summary>
    private static readonly string[] TrunkPatterns = { "main*", "master*", "dev*", "develop*" };

    private async Task<string> ResolveDefaultBranchAsync(string repoPath, CancellationToken ct)
    {
        var newest = await NewestMatchingAsync(repoPath, TrunkPatterns, ct).ConfigureAwait(false);
        if (newest is not null)
        {
            _log.LogDebug("Trunk branch for {Repo} resolved to {Branch}", repoPath, newest);
            return newest;
        }

        var symbolic = await TryRunAsync(repoPath, ct,
            "symbolic-ref", "--quiet", "refs/remotes/origin/HEAD");
        if (symbolic.Ok && symbolic.Output.StartsWith("refs/remotes/", StringComparison.Ordinal))
            return symbolic.Output["refs/remotes/".Length..];

        throw new GitWorktreeException(
            $"Could not find a trunk branch in {repoPath}: nothing matching main, master or dev, "
            + "and no origin/HEAD.");
    }

    /// <summary>
    /// The latest remote branch matching any of the given patterns, or null.
    ///
    /// "Latest" is decided by <see cref="TrunkBranchSelector"/>, which prefers the date stamped
    /// into the branch name over the commit date - the repos here cut dated trunk snapshots, and
    /// a hotfix pushed to an old one would otherwise make it look newest.
    ///
    /// One git call covers every pattern. origin/HEAD is excluded because it is a symbolic alias
    /// rather than a branch in its own right.
    /// </summary>
    private async Task<string?> NewestMatchingAsync(string repoPath, IReadOnlyList<string> patterns,
        CancellationToken ct)
    {
        var args = new List<string>
        {
            "for-each-ref", "--sort=-committerdate",
            "--format=%(refname:short)%09%(committerdate:iso8601)"
        };
        args.AddRange(patterns.Select(p => $"refs/remotes/origin/{p}"));

        var matches = await TryRunAsync(repoPath, ct, args.ToArray()).ConfigureAwait(false);
        if (!matches.Ok) return null;

        var candidates = new List<BranchCandidate>();
        foreach (var line in matches.Output.Split('\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            if (parts[0].EndsWith("/HEAD", StringComparison.Ordinal)) continue;

            // An unparseable date must not drop the branch: it can still win on its name stamp.
            DateTime.TryParse(parts[1], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var committed);
            candidates.Add(new BranchCandidate(parts[0], committed));
        }

        return TrunkBranchSelector.Select(candidates);
    }

    private async Task RunAsync(string cwd, CancellationToken ct, params string[] args)
    {
        var result = await TryRunAsync(cwd, ct, args);
        if (!result.Ok)
            throw new GitWorktreeException($"git {string.Join(' ', args)} failed: {result.Error}");
    }

    private void TryRun(string cwd, params string[] args) =>
        TryRunAsync(cwd, CancellationToken.None, args).GetAwaiter().GetResult();

    private async Task<(bool Ok, string Output, string Error)> TryRunAsync(
        string cwd, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        // The daemon starts from HKCU\Run with no console and no visible window. Without these,
        // a private remote whose credentials are not cached pops a Git Credential Manager dialog
        // that nothing is there to answer, and the fetch hangs until the timeout.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return (false, "", $"could not start git: {ex.Message}");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(GitTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(process);
            return (false, "", $"git {args[0]} timed out after {GitTimeout.TotalMinutes:0} minutes");
        }

        var output = (await stdout.ConfigureAwait(false)).Trim();
        var error = (await stderr.ConfigureAwait(false)).Trim();
        return (process.ExitCode == 0, output, error);
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    // Git marks objects in the worktree read-only, which defeats a plain recursive delete.
    private static void ForceDelete(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* best effort */ }
        }
        Directory.Delete(path, recursive: true);
    }

    private static string Sanitise(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return cleaned.Length == 0 ? "repo" : cleaned;
    }
}
