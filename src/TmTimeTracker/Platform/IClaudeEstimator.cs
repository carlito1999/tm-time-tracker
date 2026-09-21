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
/// <param name="Model">
/// Sonnet rather than Opus: sizing a ticket is a judgement over code that has already been read,
/// not a hard reasoning problem, and this runs unattended on the user's own subscription quota.
/// Together with subagents being disallowed it keeps a run to a fraction of the first live
/// attempt, which spent over two dollars on one ticket and still ran out.
///
/// Deliberately the bare alias "sonnet" and not a pinned name like "claude-sonnet-5". The CLI
/// resolves an alias to the latest model in that family, so this tracks new Sonnet releases
/// without anyone editing it. Do not pin a dated model here.
/// </param>
/// <param name="MaxBudgetUsd">
/// A ceiling, not an expectation. It exists so a pathological run stops rather than draining the
/// quota the user's own Claude sessions share.
/// </param>
public sealed record ClaudeEstimatorOptions(
    string? ExecutablePath = null,
    string? Model = "sonnet",
    decimal MaxBudgetUsd = 3.00m,
    TimeSpan? Timeout = null)
{
    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromMinutes(10);
}
