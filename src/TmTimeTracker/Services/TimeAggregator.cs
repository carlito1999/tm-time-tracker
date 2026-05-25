using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;

namespace TmTimeTracker.Services;

public sealed class TimeAggregator : BackgroundService
{
    private readonly IEventBus _bus;
    private readonly TicketTimeRepository _tickets;
    private readonly IClock _clock;
    private readonly ILogger<TimeAggregator> _log;

    private UserActivityState _activity = UserActivityState.Active;
    private string? _currentTicket;

    public TimeAggregator(IEventBus bus, TicketTimeRepository tickets, IClock clock,
        ILogger<TimeAggregator> log)
    {
        _bus = bus; _tickets = tickets; _clock = clock; _log = log;
    }

    public void ProcessEvent(DomainEvent evt)
    {
        switch (evt)
        {
            case ActivityChanged a: _activity = a.State; break;
            case BranchChanged b: _currentTicket = b.TicketKey; break;
        }
    }

    public Task TickAsync()
    {
        if (_activity == UserActivityState.Active && _currentTicket is not null)
        {
            var cycle = _tickets.OpenOrCreateCycle(_currentTicket, _clock.UtcNow);
            _tickets.IncrementMinute(cycle.Id);
            _log.LogDebug("+1 min on {Ticket} (cycle {Id})", _currentTicket, cycle.Id);
        }
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = Task.Run(async () =>
        {
            await foreach (var evt in _bus.Subscribe(stoppingToken).ConfigureAwait(false))
                ProcessEvent(evt);
        }, stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await TickAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        await subscriber.ConfigureAwait(false);
    }
}
