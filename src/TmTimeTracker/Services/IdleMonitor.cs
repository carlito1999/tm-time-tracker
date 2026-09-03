using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using TmTimeTracker.Platform;

namespace TmTimeTracker.Services;

/// <summary>
/// Tracks whether the user is at the desk. This is the human stream's gate only - Claude Code
/// activity deliberately has no say here, because unattended agent work is credited by
/// <see cref="RepoActivityMonitor"/>'s Claude stream rather than by pretending someone is typing.
/// </summary>
public sealed class IdleMonitor : BackgroundService
{
    private readonly IIdleProbe _probe;
    private readonly IEventBus _bus;
    private readonly IClock _clock;
    private readonly ConfigRepository _config;
    private readonly ILogger<IdleMonitor> _log;
    private readonly TimeSpan _sampleInterval = TimeSpan.FromSeconds(5);

    public IdleMonitor(IIdleProbe probe, IEventBus bus, IClock clock,
        ConfigRepository config, ILogger<IdleMonitor> log)
    {
        _probe = probe; _bus = bus; _clock = clock; _config = config; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var threshold = TimeSpan.FromSeconds(_config.Get().IdleThresholdSeconds);
        var sm = new IdleStateMachine(threshold);
        sm.OnTransition += state =>
        {
            _ = _bus.PublishAsync(new ActivityChanged(state, _clock.UtcNow));
            _log.LogInformation("Activity state -> {State}", state);
        };

        await _bus.PublishAsync(new ActivityChanged(sm.Current, _clock.UtcNow), stoppingToken)
            .ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var idleSec = _probe.SecondsSinceLastInput();
                var locked = _probe.IsSessionLocked();
                sm.Observe(idleSec, locked);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Idle probe failed; assuming idle this tick");
                sm.Observe(long.MaxValue, isLocked: true);
            }
            await Task.Delay(_sampleInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}
