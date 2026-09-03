using TmTimeTracker.Logic;

namespace TmTimeTracker.Platform;

public interface IProcessLiveness
{
    /// <summary>
    /// Whether the process that wrote this session file is still running. False for anything it
    /// cannot verify, which is what keeps an uncapped Claude window safe.
    /// </summary>
    bool IsRunning(ClaudeSession session);
}
