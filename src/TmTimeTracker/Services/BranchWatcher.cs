using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using TmTimeTracker.Platform;

namespace TmTimeTracker.Services;

public sealed class BranchWatcher : BackgroundService
{
    private readonly IGitBranchProbe _probe;
    private readonly IEventBus _bus;
    private readonly IClock _clock;
    private readonly ConfigRepository _config;
    private readonly ILogger<BranchWatcher> _log;
    private readonly TimeSpan _sampleInterval = TimeSpan.FromSeconds(10);

    public BranchWatcher(IGitBranchProbe probe, IEventBus bus, IClock clock,
        ConfigRepository config, ILogger<BranchWatcher> log)
    {
        _probe = probe; _bus = bus; _clock = clock; _config = config; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? lastBranch = null;
        var repoPath = _config.Get().RepoPath;

        while (!stoppingToken.IsCancellationRequested)
        {
            var branch = _probe.GetCurrentBranch(repoPath);
            if (branch != lastBranch)
            {
                var ticket = branch is null ? null : TicketKeyExtractor.Extract(branch);
                await _bus.PublishAsync(new BranchChanged(branch, ticket, _clock.UtcNow), stoppingToken)
                    .ConfigureAwait(false);
                _log.LogInformation("Branch -> {Branch} (ticket={Ticket})", branch, ticket);
                lastBranch = branch;
            }
            await Task.Delay(_sampleInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}
