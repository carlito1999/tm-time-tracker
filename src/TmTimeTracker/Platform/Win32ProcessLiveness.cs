using System.Diagnostics;
using TmTimeTracker.Logic;

namespace TmTimeTracker.Platform;

/// <summary>
/// The stop condition for a Claude session that never says it stopped.
///
/// A session killed mid-tool-call - VS Code closed, a crash, the machine asleep - leaves its
/// status latched at "busy" forever, so with no time cap on the window it is process death, and
/// only process death, that ends the run. That makes this check load-bearing rather than a
/// nicety, hence the deliberate strictness: a pid alone is not enough, because pids are reused.
/// </summary>
public sealed class Win32ProcessLiveness : IProcessLiveness
{
    /// <summary>
    /// procStart comes from the same kernel creation time Process.StartTime reports, so this is
    /// slack for rounding rather than drift.
    /// </summary>
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);

    public bool IsRunning(ClaudeSession session)
    {
        // No recorded start time means no defence against a reused pid, and a wrong "yes" here
        // bills a repo indefinitely. Fall back to the transcript-mtime rule instead.
        if (session.Pid <= 0 || session.ProcStartFileTime is null) return false;

        DateTime started;
        try { started = DateTime.FromFileTimeUtc(session.ProcStartFileTime.Value); }
        catch (ArgumentOutOfRangeException) { return false; }

        try
        {
            using var process = Process.GetProcessById(session.Pid);
            return !process.HasExited
                   && (process.StartTime.ToUniversalTime() - started).Duration() <= StartTolerance;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                     or System.ComponentModel.Win32Exception
                                     or NotSupportedException)
        {
            // No such process, it exited while we looked, or we cannot see it. Not verifiably
            // alive either way.
            return false;
        }
    }
}
