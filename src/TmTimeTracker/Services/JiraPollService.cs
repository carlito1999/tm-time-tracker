using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;

namespace TmTimeTracker.Services;

public sealed class JiraPollService : BackgroundService
{
    private readonly TicketTimeRepository _tickets;
    private readonly IJiraIssueSource _api;
    private readonly ConfigRepository _config;
    private readonly IEventBus _bus;
    private readonly IClock _clock;
    private readonly ILogger<JiraPollService> _log;
    private readonly PollServiceGate _gate;

    public JiraPollService(TicketTimeRepository tickets, IJiraIssueSource api,
        ConfigRepository config, IEventBus bus, IClock clock, ILogger<JiraPollService> log,
        PollServiceGate gate)
    {
        _tickets = tickets; _api = api; _config = config;
        _bus = bus; _clock = clock; _log = log; _gate = gate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!_gate.Enabled && !stoppingToken.IsCancellationRequested)
            await Task.Delay(500, stoppingToken).ConfigureAwait(false);
        if (stoppingToken.IsCancellationRequested) return;

        var cfg = _config.Get();
        var interval = TimeSpan.FromSeconds(cfg.JiraPollIntervalSeconds);
        var transitionTo = cfg.TransitionToStatusName;

        using var timer = new PeriodicTimer(interval);
        do
        {
            try { await PollOnce(transitionTo, stoppingToken).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogError(ex, "Poll cycle failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async Task PollOnce(string transitionTo, CancellationToken ct)
    {
        var open = _tickets.GetAllOpen();
        foreach (var cycle in open)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var issue = await _api.GetIssueAsync(cycle.TicketKey, ct).ConfigureAwait(false);
                var nowUtc = _clock.UtcNow;
                var previous = cycle.LastSeenStatus;
                _tickets.UpdateStatusSnapshot(cycle.Id, issue.Fields.Status.Name, nowUtc);

                if (previous is null) continue;

                if (issue.Fields.Status.Name == transitionTo && previous != transitionTo)
                {
                    await _bus.PublishAsync(
                        new JiraStatusTransition(
                            cycle.TicketKey,
                            issue.Fields.Summary,
                            previous,
                            issue.Fields.Status.Name,
                            cycle.MinutesActive,
                            nowUtc,
                            issue.Id),
                        ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Poll failed for {Ticket}; continuing with remaining cycles", cycle.TicketKey);
            }
        }
    }
}
