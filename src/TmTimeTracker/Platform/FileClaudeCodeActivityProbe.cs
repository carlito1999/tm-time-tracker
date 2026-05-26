using Microsoft.Extensions.Logging;

namespace TmTimeTracker.Platform;

public sealed class FileClaudeCodeActivityProbe : IClaudeCodeActivityProbe
{
    private readonly string _projectsRoot;
    private readonly ILogger<FileClaudeCodeActivityProbe> _log;
    private readonly HashSet<string> _warnedOnce =
        new(StringComparer.OrdinalIgnoreCase);

    public FileClaudeCodeActivityProbe(ILogger<FileClaudeCodeActivityProbe> log)
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".claude", "projects"), log) { }

    // Test-friendly overload allowing a fake projects root.
    public FileClaudeCodeActivityProbe(string projectsRoot,
        ILogger<FileClaudeCodeActivityProbe> log)
    {
        _projectsRoot = projectsRoot;
        _log = log;
    }

    public IReadOnlyDictionary<string, DateTime> Snapshot()
    {
        var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_projectsRoot)) return result;

        IEnumerable<string> projectDirs;
        try { projectDirs = Directory.EnumerateDirectories(_projectsRoot); }
        catch (Exception ex) { WarnOnce(_projectsRoot, ex); return result; }

        foreach (var dir in projectDirs)
        {
            try
            {
                var latest = Directory.EnumerateFiles(dir, "*.jsonl")
                    .Select(File.GetLastWriteTimeUtc)
                    .DefaultIfEmpty(DateTime.MinValue)
                    .Max();
                if (latest > DateTime.MinValue)
                    result[Path.GetFileName(dir)] = latest;
            }
            catch (Exception ex) { WarnOnce(dir, ex); }
        }
        return result;
    }

    private void WarnOnce(string path, Exception ex)
    {
        if (_warnedOnce.Add(path))
            _log.LogWarning(ex, "ClaudeCodeActivityProbe: failed to enumerate {Path}", path);
    }
}
