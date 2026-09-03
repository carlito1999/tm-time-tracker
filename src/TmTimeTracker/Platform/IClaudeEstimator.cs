namespace TmTimeTracker.Platform;

/// <summary>Raw result of one `claude -p` run. Interpretation belongs to EstimateResult.</summary>
public sealed record ClaudeRun(int ExitCode, string Stdout, string Stderr, bool TimedOut);

/// <summary>Raised when the claude executable cannot be found or started at all.</summary>
public sealed class ClaudeUnavailableException : Exception
{
    public ClaudeUnavailableException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>
/// Seam over the Claude Code CLI so the estimation sweep can be tested without spawning a
/// process. Deliberately returns raw output rather than an estimate: whether a run succeeded is
/// decided by the verification gates, never by the runner.
/// </summary>
public interface IClaudeEstimator
{
    Task<ClaudeRun> RunAsync(string workingDirectory, string prompt, CancellationToken ct);

    /// <summary>
    /// Minimal real session confirming the CLI is installed and its credential still works.
    /// Used by the Settings "Test" button.
    /// </summary>
    Task<ClaudeRun> CheckAsync(CancellationToken ct);
}

/// <param name="ExecutablePath">
/// Overrides PATH lookup. The daemon starts from HKCU\Run, where a user-local bin directory is
/// usually but not always on PATH.
/// </param>
public sealed record ClaudeEstimatorOptions(
    string? ExecutablePath = null,
    string? Model = "opus",
    decimal MaxBudgetUsd = 2.00m,
    TimeSpan? Timeout = null)
{
    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromMinutes(10);
}
