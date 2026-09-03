using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Configuration;

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

    public async Task<string> PrepareAsync(string repoPath, CancellationToken ct)
    {
        if (!Directory.Exists(repoPath))
            throw new GitWorktreeException($"Repository path does not exist: {repoPath}");

        var target = PathFor(repoPath);
        Directory.CreateDirectory(_estimatesRoot);

        // Bring the remote refs up to date first: "latest main" means latest on the remote, not
        // whatever this clone last saw.
        await RunAsync(repoPath, ct, "fetch", "--quiet", "origin");

        var reference = await ResolveDefaultBranchAsync(repoPath, ct);

        // A worktree left behind by the previous sweep would make `worktree add` fail outright.
        Detach(repoPath, target);

        await RunAsync(repoPath, ct, "worktree", "add", "--detach", "--quiet", target, reference);

        _log.LogDebug("Prepared estimation worktree for {Repo} at {Reference}", repoPath, reference);
        return target;
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
    /// origin/HEAD is set by clone but is missing from repos whose remote was added later, so
    /// the well-known names are probed before giving up.
    /// </summary>
    private async Task<string> ResolveDefaultBranchAsync(string repoPath, CancellationToken ct)
    {
        var symbolic = await TryRunAsync(repoPath, ct,
            "symbolic-ref", "--quiet", "refs/remotes/origin/HEAD");
        if (symbolic.Ok && symbolic.Output.StartsWith("refs/remotes/", StringComparison.Ordinal))
            return symbolic.Output["refs/remotes/".Length..];

        foreach (var candidate in new[] { "origin/main", "origin/master" })
        {
            var probe = await TryRunAsync(repoPath, ct,
                "rev-parse", "--verify", "--quiet", candidate);
            if (probe.Ok && probe.Output.Length > 0) return candidate;
        }

        throw new GitWorktreeException(
            $"Could not determine the default branch of {repoPath}; no origin/HEAD, origin/main or origin/master.");
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
