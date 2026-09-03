using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;

namespace TmTimeTracker.Services;

/// <summary>
/// Turns per-repo activity into minutes against tickets.
///
/// Every repo with work happening in it earns a full minute, concurrently - hand-editing one
/// project while a Claude session runs in another pays both. A day can therefore total more than
/// the wall clock, which is deliberate: two streams of work really did happen.
///
/// Work done on a branch with no ticket in its name - typically main, before the feature branch
/// exists - is held rather than discarded, and credited to the next ticket branch checked out
/// *in that same repo*. Banked minutes can never cross to another repo's ticket.
/// </summary>
public sealed class TimeAggregator : BackgroundService
{
    /// <summary>
    /// A long stretch on main is unrelated work, not a late branch, so only the most recent half
    /// hour can follow you. Without a cap, a whole morning on main would land on whichever ticket
    /// you happened to branch to after lunch. Applied per repo.
    /// </summary>
    private const int MaxCarriedMinutes = 30;

    private readonly IEventBus _bus;
    private readonly IRepoActivitySource _source;
    private readonly TicketTimeRepository _tickets;
    private readonly IClock _clock;
    private readonly ILogger<TimeAggregator> _log;

    private UserActivityState _activity = UserActivityState.Active;
    private DateTime _lastTickUtc;

    /// <summary>Attribution state for one repo.</summary>
    private sealed class RepoState
    {
        public string? LastTicket;
        public int Banked;
    }

    // In memory on purpose: a restart should not resurrect hours of ambiguous time and attach it
    // to whatever branch happens to be checked out next. Keyed by repo path, case-insensitively,
    // because Windows paths are.
    private readonly Dictionary<string, RepoState> _repos =
        new(StringComparer.OrdinalIgnoreCase);

    public TimeAggregator(IEventBus bus, IRepoActivitySource source, TicketTimeRepository tickets,
        IClock clock, ILogger<TimeAggregator> log)
    {
        _bus = bus; _source = source; _tickets = tickets; _clock = clock; _log = log;
        _lastTickUtc = clock.UtcNow;
    }

    public void ProcessEvent(DomainEvent evt)
    {
        // BranchChanged is a dashboard-log event now; attribution is pulled at tick time instead,
        // so a branch switch cannot bank or carry anything on its own.
        if (evt is ActivityChanged a) _activity = a.State;
    }

    public Task TickAsync()
    {
        var now = _clock.UtcNow;
        var humanActive = _activity == UserActivityState.Active;

        IReadOnlyList<RepoActivity> sample;
        try
        {
            sample = _source.Sample(_lastTickUtc, humanActive);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Repo activity sample failed; crediting nothing this tick");
            sample = Array.Empty<RepoActivity>();
        }
        _lastTickUtc = now;

        var toCredit = new List<string>();
        foreach (var repo in sample)
        {
            // One repo's bad tick must not cost the others their minute.
            try { Accrue(repo, now, toCredit); }
            catch (Exception ex) { _log.LogError(ex, "Accrual failed for {Repo}", repo.RepoPath); }
        }

        // Deduplicated because OpenOrCreateCycle returns the same open cycle for a given key
        // whatever the repo - two repos on one ticket must not bill it twice in one minute.
        foreach (var key in toCredit.Distinct(StringComparer.Ordinal))
        {
            var cycle = _tickets.OpenOrCreateCycle(key, now);
            _tickets.IncrementMinute(cycle.Id);
            _log.LogDebug("+1 min on {Ticket} (cycle {Id})", key, cycle.Id);
        }

        return Task.CompletedTask;
    }

    private void Accrue(RepoActivity repo, DateTime now, List<string> toCredit)
    {
        if (!_repos.TryGetValue(repo.RepoPath, out var state))
            _repos[repo.RepoPath] = state = new RepoState();

        if (repo.TicketKey is null)
        {
            // Bank it instead of dropping it; stops counting at the cap rather than sliding, so
            // the buffer always means "the last stretch of untracked work, up to half an hour".
            state.LastTicket = null;
            if (state.Banked < MaxCarriedMinutes) state.Banked++;
            return;
        }

        if (!string.Equals(state.LastTicket, repo.TicketKey, StringComparison.Ordinal))
            Carry(repo.RepoPath, state, repo.TicketKey, now);

        state.LastTicket = repo.TicketKey;
        toCredit.Add(repo.TicketKey);
    }

    /// <summary>
    /// Hands the ticket the minutes banked while this repo had no ticket. Switching between two
    /// ticket branches moves nothing, because a repo's buffer only fills while its ticket is null.
    /// </summary>
    private void Carry(string repoPath, RepoState state, string ticketKey, DateTime now)
    {
        if (state.Banked == 0) return;

        var cycle = _tickets.OpenOrCreateCycle(ticketKey, now);
        _tickets.AddMinutes(cycle.Id, state.Banked);
        _log.LogInformation("Carried {Minutes} unattributed min from {Repo} onto {Ticket} (cycle {Id})",
            state.Banked, repoPath, ticketKey, cycle.Id);
        state.Banked = 0;
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
