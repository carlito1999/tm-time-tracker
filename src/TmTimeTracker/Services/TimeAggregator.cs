using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;

namespace TmTimeTracker.Services;

/// <summary>
/// Turns activity and branch events into minutes against a ticket.
///
/// Work done on a branch with no ticket in its name - typically main, before the feature branch
/// exists - is held rather than discarded, and credited to the next ticket branch checked out.
/// That covers the ordinary case of starting work and branching a few minutes later.
/// </summary>
public sealed class TimeAggregator : BackgroundService
{
    /// <summary>
    /// A long stretch on main is unrelated work, not a late branch, so only the most recent half
    /// hour can follow you. Without a cap, a whole morning on main would land on whichever ticket
    /// you happened to branch to after lunch.
    /// </summary>
    private const int MaxCarriedMinutes = 30;

    private readonly IEventBus _bus;
    private readonly TicketTimeRepository _tickets;
    private readonly IClock _clock;
    private readonly ILogger<TimeAggregator> _log;

    private UserActivityState _activity = UserActivityState.Active;
    private string? _currentTicket;

    // In memory on purpose: a restart should not resurrect hours of ambiguous time and attach it
    // to whatever branch happens to be checked out next.
    private int _carriedMinutes;

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
            case BranchChanged b:
                if (b.TicketKey is not null) Carry(b.TicketKey);
                _currentTicket = b.TicketKey;
                break;
        }
    }

    /// <summary>
    /// Hands the ticket the minutes banked while no ticket was known. Switching between two ticket
    /// branches moves nothing, because the buffer is only ever filled while the ticket is null.
    /// </summary>
    private void Carry(string ticketKey)
    {
        if (_carriedMinutes == 0) return;

        var cycle = _tickets.OpenOrCreateCycle(ticketKey, _clock.UtcNow);
        _tickets.AddMinutes(cycle.Id, _carriedMinutes);
        _log.LogInformation("Carried {Minutes} unattributed min onto {Ticket} (cycle {Id})",
            _carriedMinutes, ticketKey, cycle.Id);
        _carriedMinutes = 0;
    }

    public Task TickAsync()
    {
        if (_activity != UserActivityState.Active) return Task.CompletedTask;

        if (_currentTicket is null)
        {
            // Bank it instead of dropping it; stops counting at the cap rather than sliding, so
            // the buffer always means "the last stretch of untracked work, up to half an hour".
            if (_carriedMinutes < MaxCarriedMinutes) _carriedMinutes++;
            return Task.CompletedTask;
        }

        var open = _tickets.OpenOrCreateCycle(_currentTicket, _clock.UtcNow);
        _tickets.IncrementMinute(open.Id);
        _log.LogDebug("+1 min on {Ticket} (cycle {Id})", _currentTicket, open.Id);
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
