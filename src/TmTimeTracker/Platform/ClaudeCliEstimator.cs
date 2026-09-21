using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;

namespace TmTimeTracker.Platform;

/// <summary>
/// Runs one estimation session through the Claude Code CLI.
///
/// Kept deliberately thin: argument construction lives in <see cref="ClaudeCommandLine"/> and
/// interpretation in <see cref="EstimateResult"/>, both under test. What remains here is only
/// the process handling that cannot be unit tested without spawning something.
///
/// Authentication is ambient by default - the child inherits the machine's own Claude Code
/// login. A stored token is an override for the headless case, where an expired interactive
/// login would otherwise fail every night with nothing in the UI to show why.
/// </summary>
public sealed class ClaudeCliEstimator : IClaudeEstimator
{
    private readonly ClaudeAuthRepository _auth;
    private readonly ClaudeEstimatorOptions _options;
    private readonly ILogger<ClaudeCliEstimator> _log;

    public ClaudeCliEstimator(ClaudeAuthRepository auth, ClaudeEstimatorOptions options,
        ILogger<ClaudeCliEstimator> log)
    {
        _auth = auth;
        _options = options;
        _log = log;
    }

    public Task<ClaudeRun> CheckAsync(CancellationToken ct) =>
        LaunchAsync(Path.GetTempPath(), ClaudeCommandLine.BuildCheck(), ct);

    public Task<ClaudeRun> RunAsync(string workingDirectory, string prompt, CancellationToken ct) =>
        LaunchAsync(workingDirectory,
            ClaudeCommandLine.Build(prompt, _options.Model, _options.MaxBudgetUsd), ct);

    private async Task<ClaudeRun> LaunchAsync(string workingDirectory,
        IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_options.ExecutablePath ?? "claude")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        ApplyCredential(psi);

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new ClaudeUnavailableException(
                "The claude executable could not be started. Check that Claude Code is installed " +
                "and on PATH, or set an explicit path in Settings.", ex);
        }

        // Read both streams concurrently. A run that fills the stderr pipe while nothing drains
        // it deadlocks, and this one prints a large JSON event array.
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.EffectiveTimeout);

        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(process);
            _log.LogWarning("Estimation run in {Dir} exceeded {Minutes} minutes and was killed",
                workingDirectory, _options.EffectiveTimeout.TotalMinutes);
            return new ClaudeRun(-1, await Drain(stdout), await Drain(stderr), TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        return new ClaudeRun(process.ExitCode, await Drain(stdout), await Drain(stderr), TimedOut: false);
    }

    private void ApplyCredential(ProcessStartInfo psi)
    {
        string? token;
        try
        {
            token = _auth.Get();
        }
        catch (Exception ex)
        {
            // An unreadable token must not stop the run: the ambient login is the normal path
            // and still works.
            _log.LogWarning(ex, "Could not read the stored Claude token; using the ambient login");
            return;
        }

        if (string.IsNullOrWhiteSpace(token)) return;

        psi.Environment["CLAUDE_CODE_OAUTH_TOKEN"] = token;
        _log.LogDebug("Using the stored Claude token rather than the ambient login");
    }

    // The CLI spawns children; killing only the parent leaves them running and holding the
    // worktree open, which makes the next sweep's cleanup fail.
    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    private static async Task<string> Drain(Task<string> stream)
    {
        try { return await stream.ConfigureAwait(false); }
        catch { return ""; }
    }
}
