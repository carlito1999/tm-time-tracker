namespace TmTimeTracker.Platform;

public interface IClaudeCodeActivityProbe
{
    /// <summary>
    /// Returns a snapshot of {projectSlug → latest .jsonl mtime (UTC)} across
    /// all Claude Code project folders. Empty dictionary if no projects folder.
    /// Dictionary uses OrdinalIgnoreCase keys.
    /// </summary>
    IReadOnlyDictionary<string, DateTime> Snapshot();
}
