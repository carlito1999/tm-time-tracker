using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using TmTimeTracker.Slack;

namespace TmTimeTracker.Services;

internal enum NotificationOutcome
{
    Sent,
    Skipped,
    Deferred,
    Expired
}

/// <summary>
/// Posts a Slack message when a tracked ticket transitions into the review status.
///
/// No de-duplication is needed: JiraPollService publishes JiraStatusTransition only on the edge
/// (previous != target AND current == target), using ticket_time.last_seen_status as its memory.
///
/// When a template asks for pull-request data that is not available yet, the notification is HELD
/// and retried rather than sent with a literal {PR_URL} in it. Jira ingests a pull request a moment
/// after it is opened, so a ticket dragged to Review promptly would otherwise always lose the race.
/// </summary>
public sealed class ReviewSlackNotifier : BackgroundService
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(10);

    private readonly IEventBus _bus;
    private readonly SlackChannelRepository _channels;
    private readonly ISlackPoster _slack;
    private readonly IJiraSiteResolver _site;
    private readonly IPullRequestSource _pullRequests;
    private readonly IClock _clock;
    private readonly ILogger<ReviewSlackNotifier> _log;

    private readonly Dictionary<string, PendingNotification> _pending = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    private sealed record PendingNotification(JiraStatusTransition Event, DateTime FirstSeenUtc);

    public ReviewSlackNotifier(IEventBus bus, SlackChannelRepository channels,
        ISlackPoster slack, IJiraSiteResolver site, IPullRequestSource pullRequests,
        IClock clock, ILogger<ReviewSlackNotifier> log)
    {
        _bus = bus; _channels = channels; _slack = slack; _site = site;
        _pullRequests = pullRequests; _clock = clock; _log = log;
    }

    internal int PendingCount { get { lock (_lock) return _pending.Count; } }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(
            ListenAsync(stoppingToken),
            RetryHeldNotificationsAsync(stoppingToken)).ConfigureAwait(false);
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        await foreach (var evt in _bus.Subscribe(ct).ConfigureAwait(false))
        {
            if (evt is JiraStatusTransition transition)
                await HandleAsync(transition, ct).ConfigureAwait(false);
        }
    }

    private async Task RetryHeldNotificationsAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(RetryInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                PendingNotification[] held;
                lock (_lock) held = _pending.Values.ToArray();

                foreach (var pending in held)
                    await HandleAsync(pending.Event, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    internal async Task<NotificationOutcome> HandleAsync(JiraStatusTransition evt, CancellationToken ct)
    {
        try
        {
            var project = JiraProjectKey.From(evt.TicketKey);
            if (project is null)
            {
                _log.LogDebug("No project key in {Ticket}; skipping Slack notification", evt.TicketKey);
                return NotificationOutcome.Skipped;
            }

            var mapping = _channels.Find(project);
            if (mapping is null)
            {
                _log.LogDebug("No Slack channel configured for {Project}; skipping", project);
                return Forget(evt, NotificationOutcome.Skipped);
            }

            var pullRequest = await _pullRequests.GetBestAsync(evt.IssueId, ct).ConfigureAwait(false);

            if (pullRequest is null && SlackVariables.TemplateReferencesPullRequest(mapping.MessageTemplate))
                return Hold(evt);

            var siteUrl = await _site.GetSiteUrlAsync(ct).ConfigureAwait(false);
            var variables = SlackVariables.Build(evt.TicketKey, evt.Summary,
                evt.FromStatus, evt.ToStatus, evt.MinutesActive, evt.AtUtc, siteUrl,
                pullRequest?.Url, pullRequest?.Title, pullRequest?.Status);
            var text = MessageTemplateRenderer.Render(mapping.MessageTemplate, variables);

            await _slack.PostMessageAsync(mapping.ChannelId, text, ct).ConfigureAwait(false);
            _log.LogInformation("Posted {Ticket} review notification to #{Channel}",
                evt.TicketKey, mapping.ChannelName);

            return Forget(evt, NotificationOutcome.Sent);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A notification is a side-channel: never let it disturb time tracking.
            _log.LogWarning(ex, "Slack notification for {Ticket} failed", evt.TicketKey);
            return NotificationOutcome.Skipped;
        }
    }

    /// <summary>
    /// Holds a notification whose template wants pull-request data that Jira does not have yet.
    /// Gives up after MaxWait so a ticket that simply has no pull request cannot be retried forever.
    /// </summary>
    private NotificationOutcome Hold(JiraStatusTransition evt)
    {
        lock (_lock)
        {
            if (_pending.TryGetValue(evt.TicketKey, out var existing))
            {
                if (_clock.UtcNow - existing.FirstSeenUtc < MaxWait)
                    return NotificationOutcome.Deferred;

                _pending.Remove(evt.TicketKey);
                _log.LogWarning(
                    "No pull request appeared for {Ticket} within {Minutes} minutes; " +
                    "the Slack notification was dropped rather than sent without a PR link",
                    evt.TicketKey, MaxWait.TotalMinutes);
                return NotificationOutcome.Expired;
            }

            _pending[evt.TicketKey] = new PendingNotification(evt, _clock.UtcNow);
            _log.LogInformation(
                "Holding the {Ticket} notification until its pull request is known", evt.TicketKey);
            return NotificationOutcome.Deferred;
        }
    }

    private NotificationOutcome Forget(JiraStatusTransition evt, NotificationOutcome outcome)
    {
        lock (_lock) _pending.Remove(evt.TicketKey);
        return outcome;
    }
}
