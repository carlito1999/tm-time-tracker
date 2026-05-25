using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TmTimeTracker.Services;

public sealed class BranchWatcher : BackgroundService
{
    private readonly ActiveRepoResolver _resolver;
    private readonly IEventBus _bus;
    private readonly IClock _clock;
    private readonly ILogger<BranchWatcher> _log;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(2);

    private string? _lastRepoPath;
    private string? _lastBranch;
    private string? _lastTicket;

    public BranchWatcher(ActiveRepoResolver resolver, IEventBus bus, IClock clock,
        ILogger<BranchWatcher> log)
    {
        _resolver = resolver; _bus = bus; _clock = clock; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        do
        {
            try
            {
                var resolution = _resolver.Resolve();
                var repoPath = resolution?.RepoPath;
                var branch = resolution?.Branch;
                var ticket = resolution?.TicketKey;

                if (repoPath != _lastRepoPath || branch != _lastBranch || ticket != _lastTicket)
                {
                    _lastRepoPath = repoPath;
                    _lastBranch = branch;
                    _lastTicket = ticket;
                    await _bus.PublishAsync(new BranchChanged(branch, ticket, _clock.UtcNow), stoppingToken)
                              .ConfigureAwait(false);
                    _log.LogInformation("Active repo -> {Repo} branch={Branch} ticket={Ticket}",
                        repoPath, branch, ticket);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "BranchWatcher tick failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
