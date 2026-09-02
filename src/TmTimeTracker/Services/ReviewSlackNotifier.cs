using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using TmTimeTracker.Slack;

namespace TmTimeTracker.Services;

/// <summary>
/// Posts a Slack message when a tracked ticket transitions into the review status.
///
/// No de-duplication is needed: JiraPollService publishes JiraStatusTransition only on the edge
/// (previous != target AND current == target), using ticket_time.last_seen_status as its memory.
/// </summary>
public sealed class ReviewSlackNotifier : BackgroundService
{
    private readonly IEventBus _bus;
    private readonly SlackChannelRepository _channels;
    private readonly ISlackPoster _slack;
    private readonly IJiraSiteResolver _site;
    private readonly IPullRequestSource _pullRequests;
    private readonly ILogger<ReviewSlackNotifier> _log;

    public ReviewSlackNotifier(IEventBus bus, SlackChannelRepository channels,
        ISlackPoster slack, IJiraSiteResolver site, IPullRequestSource pullRequests,
        ILogger<ReviewSlackNotifier> log)
    {
        _bus = bus; _channels = channels; _slack = slack; _site = site;
        _pullRequests = pullRequests; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var evt in _bus.Subscribe(stoppingToken).ConfigureAwait(false))
        {
            if (evt is JiraStatusTransition transition)
                await HandleAsync(transition, stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task HandleAsync(JiraStatusTransition evt, CancellationToken ct)
    {
        try
        {
            var project = JiraProjectKey.From(evt.TicketKey);
            if (project is null)
            {
                _log.LogDebug("No project key in {Ticket}; skipping Slack notification", evt.TicketKey);
                return;
            }

            var mapping = _channels.Find(project);
            if (mapping is null)
            {
                _log.LogDebug("No Slack channel configured for {Project}; skipping", project);
                return;
            }

            var siteUrl = await _site.GetSiteUrlAsync(ct).ConfigureAwait(false);
            var pullRequest = await _pullRequests.GetBestAsync(evt.IssueId, ct).ConfigureAwait(false);

            var variables = SlackVariables.Build(evt.TicketKey, evt.Summary,
                evt.FromStatus, evt.ToStatus, evt.MinutesActive, evt.AtUtc, siteUrl,
                pullRequest?.Url, pullRequest?.Title, pullRequest?.Status);
            var text = MessageTemplateRenderer.Render(mapping.MessageTemplate, variables);

            await _slack.PostMessageAsync(mapping.ChannelId, text, ct).ConfigureAwait(false);
            _log.LogInformation("Posted {Ticket} review notification to #{Channel}",
                evt.TicketKey, mapping.ChannelName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A notification is a side-channel: it must never disturb time tracking or worklogs.
            _log.LogWarning(ex, "Slack notification for {Ticket} failed", evt.TicketKey);
        }
    }
}
