using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;

namespace TmTimeTracker.Services;

public sealed class RememberWatcher : BackgroundService
{
    private readonly IEventBus _bus;
    private readonly IClock _clock;
    private readonly TrackedRepoRepository _repos;
    private readonly RememberEntryRepository _entries;
    private readonly ILogger<RememberWatcher> _log;
    private readonly TimeSpan _refreshInterval = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, FileSystemWatcher> _watchers =
        new(StringComparer.OrdinalIgnoreCase);

    public RememberWatcher(IEventBus bus, IClock clock, TrackedRepoRepository repos,
        RememberEntryRepository entries, ILogger<RememberWatcher> log)
    {
        _bus = bus; _clock = clock; _repos = repos; _entries = entries; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RefreshWatchers();
        using var timer = new PeriodicTimer(_refreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                RefreshWatchers();
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var w in _watchers.Values) { try { w.Dispose(); } catch { } }
            _watchers.Clear();
        }
    }

    private void RefreshWatchers()
    {
        var wanted = _repos.GetAll()
            .Select(r => Path.Combine(r.Path, ".remember"))
            .Where(Directory.Exists)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in wanted)
        {
            if (_watchers.ContainsKey(path)) continue;
            try
            {
                var fsw = new FileSystemWatcher(path)
                {
                    Filter = "*.md",
                    EnableRaisingEvents = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
                };
                fsw.Changed += (_, e) => SafeScanFile(e.FullPath);
                fsw.Created += (_, e) => SafeScanFile(e.FullPath);
                _watchers[path] = fsw;
                ScanAll(path);
                _log.LogInformation("Watching {Path}", path);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not watch {Path}", path);
            }
        }

        foreach (var existing in _watchers.Keys.ToList())
        {
            if (!wanted.Contains(existing))
            {
                try { _watchers[existing].Dispose(); } catch { }
                _watchers.Remove(existing);
                _log.LogInformation("Stopped watching {Path}", existing);
            }
        }
    }

    private void ScanAll(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
            SafeScanFile(file);
    }

    private void SafeScanFile(string fullPath)
    {
        try
        {
            var name = Path.GetFileName(fullPath);
            if (!(name.StartsWith("today-", StringComparison.OrdinalIgnoreCase) ||
                  name.Equals("now.md", StringComparison.OrdinalIgnoreCase))) return;

            string content;
            try { content = File.ReadAllText(fullPath); }
            catch (IOException) { return; }

            var entryDate = ExtractDateFromFilename(name) ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
            var entries = RememberEntryParser.Parse(content, name);
            _entries.UpsertMany(entries, entryDate);
            if (entries.Count > 0)
                _ = _bus.PublishAsync(new RememberEntriesObserved(entries, entryDate, _clock.UtcNow));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to scan {File}", fullPath);
        }
    }

    private static string? ExtractDateFromFilename(string name)
    {
        if (!name.StartsWith("today-", StringComparison.OrdinalIgnoreCase)) return null;
        var datePart = Path.GetFileNameWithoutExtension(name).Substring("today-".Length);
        return DateTime.TryParse(datePart, out _) ? datePart : null;
    }
}
