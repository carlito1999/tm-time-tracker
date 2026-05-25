using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;

namespace TmTimeTracker.Services;

public sealed class MaintenanceService : BackgroundService
{
    private readonly MinuteSampleRepository _samples;
    private readonly IClock _clock;
    private readonly ILogger<MaintenanceService> _log;

    public MaintenanceService(MinuteSampleRepository samples, IClock clock, ILogger<MaintenanceService> log)
    {
        _samples = samples; _clock = clock; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            try
            {
                var cutoff = _clock.UtcNow.AddDays(-30);
                var deleted = _samples.PruneOlderThan(cutoff);
                if (deleted > 0) _log.LogInformation("Pruned {N} minute_sample rows older than {C}", deleted, cutoff);
            }
            catch (Exception ex) { _log.LogError(ex, "Maintenance failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
