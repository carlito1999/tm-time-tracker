namespace TmTimeTracker.Platform;

public sealed record ForegroundWindow(string ProcessName, string Title);

public interface IForegroundWindowProbe
{
    /// <summary>
    /// Returns the currently focused window's process name (without .exe) and title,
    /// or null when no foreground window is available or the query fails.
    /// </summary>
    ForegroundWindow? Probe();
}
