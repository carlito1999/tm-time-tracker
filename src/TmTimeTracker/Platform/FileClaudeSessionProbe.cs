using System.Text.Json;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Logic;

namespace TmTimeTracker.Platform;

/// <summary>
/// Reads ~/.claude/sessions/&lt;pid&gt;.json. Claude Code keeps one file per session and latches a
/// "status" field there for the length of a turn, which is the only signal that survives a long
/// tool call - the transcript's mtime does not.
/// </summary>
public sealed class FileClaudeSessionProbe : IClaudeSessionProbe
{
    private readonly string _sessionsRoot;
    private readonly ILogger<FileClaudeSessionProbe> _log;
    private readonly HashSet<string> _warnedOnce = new(StringComparer.OrdinalIgnoreCase);

    public FileClaudeSessionProbe(ILogger<FileClaudeSessionProbe> log)
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".claude", "sessions"), log) { }

    // Test-friendly overload allowing a fake sessions root.
    public FileClaudeSessionProbe(string sessionsRoot, ILogger<FileClaudeSessionProbe> log)
    {
        _sessionsRoot = sessionsRoot;
        _log = log;
    }

    public IReadOnlyList<ClaudeSession> Snapshot()
    {
        var result = new List<ClaudeSession>();
        if (!Directory.Exists(_sessionsRoot)) return result;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(_sessionsRoot, "*.json"); }
        catch (Exception ex) { WarnOnce(_sessionsRoot, ex); return result; }

        foreach (var file in files)
        {
            if (TryRead(file) is { } session) result.Add(session);
        }
        return result;
    }

    private ClaudeSession? TryRead(string file)
    {
        try
        {
            // Claude Code rewrites these files whole, so a read can land mid-write: share
            // everything and treat a torn file as one absent session rather than a failed tick.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);

            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("pid", out var pid) ||
                pid.ValueKind != JsonValueKind.Number ||
                !pid.TryGetInt32(out var pidValue)) return null;

            return new ClaudeSession(pidValue, Text(root, "cwd"), Text(root, "status"),
                                     FileTime(root, "procStart"));
        }
        catch (Exception ex) when (ex is IOException or JsonException
                                     or UnauthorizedAccessException)
        {
            WarnOnce(file, ex);
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    /// <summary>procStart is a Windows FILETIME, written as a string because it overflows a
    /// JavaScript number.</summary>
    private static long? FileTime(JsonElement root, string name) =>
        long.TryParse(Text(root, name), out var value) ? value : null;

    private void WarnOnce(string path, Exception ex)
    {
        if (_warnedOnce.Add(path))
            _log.LogWarning(ex, "ClaudeSessionProbe: failed to read {Path}", path);
    }
}
