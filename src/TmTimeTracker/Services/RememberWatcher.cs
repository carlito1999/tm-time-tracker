using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;

namespace TmTimeTracker.Services;

public sealed class RememberWatcher : BackgroundService
{
    private readonly IEventBus _bus;
    private readonly IClock _clock;
    private readonly ConfigRepository _config;
    private readonly RememberEntryRepository _entries;
    private readonly ILogger<RememberWatcher> _log;
    private FileSystemWatcher? _fsw;

    public RememberWatcher(IEventBus bus, IClock clock, ConfigRepository config,
        RememberEntryRepository entries, ILogger<RememberWatcher> log)
    {
        _bus = bus; _clock = clock; _config = config; _entries = entries; _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = _config.Get().RememberPath;
        if (!Directory.Exists(path))
        {
            _log.LogWarning("Remember path {Path} not found; watcher disabled", path);
            return Task.CompletedTask;
        }

        ScanAll(path);

        _fsw = new FileSystemWatcher(path)
        {
            Filter = "*.md",
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
        };
        _fsw.Changed += (_, e) => SafeScanFile(e.FullPath);
        _fsw.Created += (_, e) => SafeScanFile(e.FullPath);

        stoppingToken.Register(() =>
        {
            _fsw.EnableRaisingEvents = false;
            _fsw.Dispose();
        });

        return Task.Delay(Timeout.Infinite, stoppingToken);
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
            try
            {
                content = File.ReadAllText(fullPath);
            }
            catch (IOException)
            {
                return;
            }

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
