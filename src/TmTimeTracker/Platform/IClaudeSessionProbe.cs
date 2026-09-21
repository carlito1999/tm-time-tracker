using TmTimeTracker.Logic;

namespace TmTimeTracker.Platform;

public interface IClaudeSessionProbe
{
    /// <summary>
    /// Every session Claude Code has a file for under ~/.claude/sessions. Files outlive the
    /// processes that wrote them, so a result here means "a session existed", not "it is running"
    /// - ask <see cref="IProcessLiveness"/> for that. Empty if the directory is absent.
    /// </summary>
    IReadOnlyList<ClaudeSession> Snapshot();
}
