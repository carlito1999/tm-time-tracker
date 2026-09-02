using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;
using TmTimeTracker.Logic;
using TmTimeTracker.Slack;
using TmTimeTracker.UI;

namespace TmTimeTracker.Services;

internal enum AnnouncementOutcome
{
    Announced,
    Waiting,
    NotApplicable
}

/// <summary>
/// Owns the whole "announce the pull request in Slack" job, as a durable checklist:
///
///   1. A ticket sitting in the review status is queued (from the transition event, and also by
///      periodic discovery so a restart or a missed event cannot lose it).
///   2. A queued ticket that has not been announced is polled until Jira reports its pull request.
///   3. When the pull request appears, the message is posted and the ticket is marked announced.
///   4. If no pull request appears within 10 minutes of the branch's last commit, a tray
///      notification asks the user to announce it manually - once, not repeatedly.
///
/// State lives in the pr_announcement table rather than in memory, because Jira can take longer to
/// surface a pull request than the app is guaranteed to stay running.
/// </summary>
public sealed class PrAnnouncementWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WarnAfterLastCommit = TimeSpan.FromMinutes(10);

    private readonly IEventBus _bus;
    private readonly PrAnnouncementRepository _announcements;
    private readonly SlackChannelRepository _channels;
    private readonly TicketTimeRepository _tickets;
    private readonly ConfigRepository _config;
    private readonly IJiraIssueSource _issues;
    private readonly IPullRequestSource _pullRequests;
    private readonly IJiraSiteResolver _site;
    private readonly ISlackPoster _slack;
    private readonly IBalloonNotifier _balloon;
    private readonly IClock _clock;
    private readonly ILogger<PrAnnouncementWorker> _log;

    public PrAnnouncementWorker(
        IEventBus bus,
        PrAnnouncementRepository announcements,
        SlackChannelRepository channels,
        TicketTimeRepository tickets,
        ConfigRepository config,
        IJiraIssueSource issues,
        IPullRequestSource pullRequests,
        IJiraSiteResolver site,
        ISlackPoster slack,
        IBalloonNotifier balloon,
        IClock clock,
        ILogger<PrAnnouncementWorker> log)
    {
        _bus = bus; _announcements = announcements; _channels = channels; _tickets = tickets;
        _config = config; _issues = issues; _pullRequests = pullRequests; _site = site;
        _slack = slack; _balloon = balloon; _clock = clock; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(
            ListenAsync(stoppingToken),
            PollLoopAsync(stoppingToken)).ConfigureAwait(false);
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        await foreach (var evt in _bus.Subscribe(ct).ConfigureAwait(false))
        {
            if (evt is not JiraStatusTransition transition) continue;

            try
            {
                Queue(transition);
                // Try straight away: usually Jira already knows, and waiting 30s would be silly.
                await TryAnnounceAsync(transition.TicketKey, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Queueing the {Ticket} announcement failed", transition.TicketKey);
            }
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await RunOnceAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await DiscoverAsync(ct).ConfigureAwait(false);
            Forget();

            foreach (var pending in _announcements.GetPending())
            {
                ct.ThrowIfCancellationRequested();
                await TryAnnounceAsync(pending.TicketKey, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Pull-request announcement pass failed");
        }
    }

    /// <summary>
    /// Step 1: any tracked ticket currently in the review status owes an announcement. Runs even
    /// when no transition event was seen, so a restart mid-wait recovers by itself.
    /// </summary>
    private async Task DiscoverAsync(CancellationToken ct)
    {
        var reviewStatus = _config.Get().TransitionToStatusName;
        var known = _announcements.GetAllTicketKeys().ToHashSet(StringComparer.Ordinal);

        foreach (var cycle in _tickets.GetAllOpen())
        {
            if (!string.Equals(cycle.LastSeenStatus, reviewStatus, StringComparison.Ordinal)) continue;
            if (known.Contains(cycle.TicketKey)) continue;
            if (JiraProjectKey.From(cycle.TicketKey) is null) continue;

            string? issueId = null;
            string? summary = null;
            try
            {
                var issue = await _issues.GetIssueAsync(cycle.TicketKey, ct).ConfigureAwait(false);
                issueId = issue.Id;
                summary = issue.Fields.Summary;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Could not read {Ticket} while discovering announcements", cycle.TicketKey);
            }

            var now = _clock.UtcNow;
            _announcements.QueueIfMissing(new PrAnnouncement(
                cycle.TicketKey, issueId, summary,
                _config.Get().InProgressStatusName, reviewStatus,
                cycle.MinutesActive, now, now, 0, null, null, null));

            _log.LogInformation("Discovered {Ticket} sitting in {Status}; queued for announcement",
                cycle.TicketKey, reviewStatus);
        }
    }

    /// <summary>
    /// Drops rows for tickets that are no longer waiting in review, so the same ticket announces
    /// again if it later comes back.
    /// </summary>
    private void Forget()
    {
        var reviewStatus = _config.Get().TransitionToStatusName;
        var inReview = _tickets.GetAllOpen()
            .Where(c => string.Equals(c.LastSeenStatus, reviewStatus, StringComparison.Ordinal))
            .Select(c => c.TicketKey)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var key in _announcements.GetAllTicketKeys())
        {
            if (inReview.Contains(key)) continue;
            _announcements.Remove(key);
            _log.LogDebug("{Ticket} left {Status}; forgetting its announcement state", key, reviewStatus);
        }
    }

    private void Queue(JiraStatusTransition evt)
    {
        var now = _clock.UtcNow;
        _announcements.QueueIfMissing(new PrAnnouncement(
            evt.TicketKey, evt.IssueId, evt.Summary, evt.FromStatus, evt.ToStatus,
            evt.MinutesActive, evt.AtUtc, now, 0, null, null, null));
    }

    internal async Task<AnnouncementOutcome> TryAnnounceAsync(string ticketKey, CancellationToken ct)
    {
        var row = _announcements.Find(ticketKey);
        if (row is null || row.IsAnnounced) return AnnouncementOutcome.NotApplicable;

        var project = JiraProjectKey.From(ticketKey);
        if (project is null) return AnnouncementOutcome.NotApplicable;

        var mapping = _channels.Find(project);
        if (mapping is null)
        {
            // Nothing to announce to. Drop it rather than poll Jira forever for no reason.
            _announcements.Remove(ticketKey);
            _log.LogDebug("No Slack channel configured for {Project}; dropping {Ticket}", project, ticketKey);
            return AnnouncementOutcome.NotApplicable;
        }

        var snapshot = await _pullRequests.GetSnapshotAsync(row.IssueId, ct).ConfigureAwait(false);
        var needsPullRequest = SlackVariables.TemplateReferencesPullRequest(mapping.MessageTemplate);

        if (needsPullRequest && snapshot.PullRequest is null)
        {
            _announcements.RecordAttempt(ticketKey);
            WarnIfOverdue(row, snapshot, ticketKey);
            return AnnouncementOutcome.Waiting;
        }

        var siteUrl = await _site.GetSiteUrlAsync(ct).ConfigureAwait(false);
        var variables = SlackVariables.Build(
            row.TicketKey, row.Summary, row.FromStatus, row.ToStatus, row.Minutes,
            row.OccurredAtUtc, siteUrl,
            snapshot.PullRequest?.Url, snapshot.PullRequest?.Title, snapshot.PullRequest?.Status);

        var text = MessageTemplateRenderer.Render(mapping.MessageTemplate, variables);
        await _slack.PostMessageAsync(mapping.ChannelId, text, ct).ConfigureAwait(false);

        _announcements.MarkAnnounced(ticketKey, snapshot.PullRequest?.Url, _clock.UtcNow);
        _log.LogInformation("Announced {Ticket} in #{Channel}", ticketKey, mapping.ChannelName);

        return AnnouncementOutcome.Announced;
    }

    /// <summary>
    /// Step 4: measured from the branch's last commit rather than from when the ticket was moved,
    /// because the commit is when the pull request became possible. Warns once per ticket.
    /// </summary>
    private void WarnIfOverdue(PrAnnouncement row, DevInfoSnapshot snapshot, string ticketKey)
    {
        if (row.HasWarned || snapshot.LastCommitUtc is null) return;
        if (_clock.UtcNow - snapshot.LastCommitUtc.Value.UtcDateTime < WarnAfterLastCommit) return;

        _balloon.Show(
            $"{ticketKey}: no pull request found",
            $"It has been over {WarnAfterLastCommit.TotalMinutes:0} minutes since the last commit and "
            + "Jira still reports no pull request. You may need to announce it yourself.",
            ToolTipIcon.Warning);

        _announcements.MarkWarned(ticketKey, _clock.UtcNow);
        _log.LogWarning("No pull request for {Ticket} {Minutes} minutes after its last commit",
            ticketKey, WarnAfterLastCommit.TotalMinutes);
    }
}
