using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;

namespace TmTimeTracker.Services;

/// <summary>
/// Sweeps the two stores that are allowed to forget. Everything else in the database is either
/// configuration or a worklog record that has to survive.
/// </summary>
public sealed class MaintenanceService : BackgroundService
{
    private const int MinuteSampleRetentionDays = 30;

    /// <summary>
    /// Three months, because that is how far back the weekly report is expected to reach. Longer
    /// than minute_sample's window: an hour bucket is a few rows a day rather than one a minute.
    /// </summary>
    private const int HourLedgerRetentionDays = 90;

    private readonly MinuteSampleRepository _samples;
    private readonly HourActivityRepository _hours;
    private readonly IClock _clock;
    private readonly ILogger<MaintenanceService> _log;

    public MaintenanceService(MinuteSampleRepository samples, HourActivityRepository hours,
        IClock clock, ILogger<MaintenanceService> log)
    {
        _samples = samples; _hours = hours; _clock = clock; _log = log;
    }

    /// <summary>One sweep. Returns the total rows removed across both stores.</summary>
    public int RunOnce()
    {
        var sampleCutoff = _clock.UtcNow.AddDays(-MinuteSampleRetentionDays);
        var deletedSamples = _samples.PruneOlderThan(sampleCutoff);
        if (deletedSamples > 0)
            _log.LogInformation("Pruned {N} minute_sample rows older than {C}", deletedSamples, sampleCutoff);

        // Local, because hour_activity stores local hours - a UTC cutoff would cut the ledger at
        // the wrong point by the machine's offset.
        var hourCutoff = _clock.LocalNow.DateTime.AddDays(-HourLedgerRetentionDays);
        var deletedHours = _hours.PruneOlderThan(hourCutoff);
        if (deletedHours > 0)
            _log.LogInformation("Pruned {N} hour_activity rows older than {C}", deletedHours, hourCutoff);

        return deletedSamples + deletedHours;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            try { RunOnce(); }
            catch (Exception ex) { _log.LogError(ex, "Maintenance failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
