using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;
using TmTimeTracker.Logic;
using TmTimeTracker.Platform;
using TmTimeTracker.UI;

namespace TmTimeTracker.Services;

/// <summary>
/// Estimates To-Do tickets with Claude Code and writes the total to Jira's Original Estimate.
///
/// Two rules make an unattended five-minute loop safe to run:
///
/// It never overwrites an estimate that is already there. A human's number always wins, which
/// also makes the sweep idempotent without needing a confirmation dialog.
///
/// It never asks Claude whether the work is done. Doneness is derived from artifacts through
/// four gates, and only the last of them - reading the issue back from Jira - is evidence.
/// Jira answers 2xx and silently stores nothing when timetracking is off the edit screen, so a
/// successful write is not proof of a stored estimate.
/// </summary>
public sealed class TicketEstimationWorker : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    // The user's rule: try Claude again once, then tell them. A cap is what stops one broken
    // ticket from spawning a session every five minutes forever.
    private const int MaxSessionAttempts = 2;
    private const int MaxWriteAttempts = 2;
    private const int CalibrationCommits = 25;

    /// <summary>
    /// Tickets estimated per repo per sweep. A first run against an existing backlog would
    /// otherwise work through every To-Do ticket back to back, each up to ten minutes and a
    /// couple of dollars, on the same subscription quota the user's own Claude sessions draw
    /// from - locking them out of their own tooling on the day they are testing this. The loop
    /// drains the rest on later sweeps.
    /// </summary>
    private const int MaxTicketsPerSweep = 3;

    private readonly TrackedRepoRepository _repos;
    private readonly RepoProjectRepository _mappings;
    private readonly RepoBranchRepository _branches;
    private readonly TicketEstimateRepository _estimates;
    private readonly IJiraSearchSource _search;
    private readonly IJiraIssueSource _issues;
    private readonly IJiraEstimateWriter _writer;
    private readonly IJiraProjectSource _projects;
    private readonly IClaudeEstimator _claude;
    private readonly IGitWorktreeManager _worktrees;
    private readonly IUserNotifier _notifier;
    private readonly IClock _clock;
    private readonly ILogger<TicketEstimationWorker> _log;

    // Repo-level faults have no ticket row to hold a warned_at flag, so dedupe lives here. A
    // restart re-notifies once about a still-broken repo, which is the right side to err on:
    // these are configuration problems the user has to fix.
    private readonly HashSet<string> _warnedRepoFaults = new(StringComparer.OrdinalIgnoreCase);

    public TicketEstimationWorker(
        TrackedRepoRepository repos, RepoProjectRepository mappings,
        RepoBranchRepository branches,
        TicketEstimateRepository estimates, IJiraSearchSource search,
        IJiraIssueSource issues, IJiraEstimateWriter writer, IJiraProjectSource projects,
        IClaudeEstimator claude, IGitWorktreeManager worktrees, IUserNotifier notifier,
        IClock clock, ILogger<TicketEstimationWorker> log)
    {
        _repos = repos; _mappings = mappings; _branches = branches;
        _estimates = estimates; _search = search;
        _issues = issues; _writer = writer; _projects = projects; _claude = claude;
        _worktrees = worktrees; _notifier = notifier; _clock = clock; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            // Awaiting inside the tick serialises sweeps: an overrunning pass delays the next
            // one rather than running alongside it.
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var repo in _repos.GetAll())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await SweepRepoAsync(repo.Path, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Estimation sweep failed for {Repo}", repo.Path);
            }
        }
    }

    private async Task SweepRepoAsync(string repoPath, CancellationToken ct)
    {
        var project = await ResolveProjectAsync(repoPath, ct).ConfigureAwait(false);
        if (project is null) return;

        // status, not statusCategory: the category "To Do" also covers Backlog, Open and
        // Selected for Development, which pulled in tickets the user does not consider To Do.
        var jql = $"project = \"{project}\" AND status = \"To Do\" ORDER BY created ASC";
        var issues = await _search.SearchIssuesAsync(jql, ct).ConfigureAwait(false);

        var outstanding = new List<Issue>();
        foreach (var issue in issues)
        {
            if (IsSettled(issue.Key)) continue;

            _estimates.QueueIfMissing(issue.Key, repoPath);

            // A human's estimate always wins, and there is nothing to do here.
            if (issue.Fields.TimeTracking?.OriginalEstimateSeconds is > 0)
            {
                _estimates.MarkSkippedExisting(issue.Key);
                _log.LogDebug("{Ticket} already has an Original Estimate; leaving it alone", issue.Key);
                continue;
            }

            outstanding.Add(issue);
        }

        if (outstanding.Count == 0) return;

        if (outstanding.Count > MaxTicketsPerSweep)
        {
            _log.LogInformation(
                "{Count} tickets await estimation in {Project}; taking {Take} this sweep",
                outstanding.Count, project, MaxTicketsPerSweep);
            outstanding = outstanding.Take(MaxTicketsPerSweep).ToList();
        }

        string worktree;
        try
        {
            worktree = await _worktrees
                .PrepareAsync(repoPath, _branches.Find(repoPath), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            WarnRepoOnce(repoPath, "worktree", $"Cannot prepare {Name(repoPath)} for estimation",
                ex.Message, urgent: false);
            return;
        }

        try
        {
            var commits = await _worktrees
                .RecentCommitSubjectsAsync(worktree, CalibrationCommits, ct).ConfigureAwait(false);

            foreach (var issue in outstanding)
            {
                ct.ThrowIfCancellationRequested();
                await EstimateAsync(issue, repoPath, worktree, commits, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _worktrees.Remove(repoPath);
        }
    }

    private async Task EstimateAsync(Issue issue, string repoPath, string worktree,
        IReadOnlyList<string> commits, CancellationToken ct)
    {
        var prompt = EstimatePromptBuilder.Build(
            issue.Key, issue.Fields.Summary, AdfText.Flatten(issue.Fields.Description),
            Name(repoPath), commits);

        EstimateParse? parse = null;
        string? rawOutput = null;

        for (var attempt = 1; attempt <= MaxSessionAttempts; attempt++)
        {
            _estimates.RecordAttempt(issue.Key);

            ClaudeRun run;
            try
            {
                run = await _claude.RunAsync(worktree, prompt, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (ClaudeUnavailableException ex)
            {
                // Nothing works until the user acts, so this one breaks through Do Not Disturb.
                Fail(issue.Key, EstimateGate.Process, ex.Message, urgent: true);
                return;
            }
            catch (Exception ex)
            {
                Fail(issue.Key, EstimateGate.Process, ex.Message, urgent: false);
                return;
            }

            rawOutput = run.Stdout;
            parse = EstimateResult.Parse(run.Stdout, run.ExitCode);
            if (parse.Ok) break;

            parse = parse with { Error = Detail(parse.Error, run) };

            _log.LogWarning("{Ticket} failed the {Gate} gate on attempt {Attempt}: {Error}",
                issue.Key, parse.FailedGate, attempt, parse.Error);
        }

        if (parse is null || !parse.Ok)
        {
            Fail(issue.Key, parse?.FailedGate, parse?.Error ?? "claude produced no result.",
                urgent: false);
            return;
        }

        await StoreAsync(issue.Key, parse.Value!, rawOutput, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Gate 4. The retry here targets the write rather than the session: re-running a
    /// ten-minute estimate cannot add a field to a Jira screen, and the estimate already in
    /// hand is perfectly good.
    /// </summary>
    private async Task StoreAsync(string ticketKey, TicketEstimate estimate, string? rawOutput,
        CancellationToken ct)
    {
        var minutes = estimate.TotalMinutes;
        string? lastError = null;

        for (var attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            try
            {
                await _writer.SetOriginalEstimateAsync(ticketKey, minutes, ct).ConfigureAwait(false);

                if (await StoredMatchesAsync(ticketKey, minutes, ct).ConfigureAwait(false))
                {
                    _estimates.MarkDone(ticketKey, estimate, rawOutput, _clock.UtcNow);
                    _log.LogInformation(
                        "Estimated {Ticket} at {Minutes} minutes ({Impl}+{Test}+{Review}), confidence {Confidence}",
                        ticketKey, minutes, estimate.ImplementationMinutes,
                        estimate.TestingMinutes, estimate.ReviewMinutes, estimate.Confidence);
                    return;
                }

                lastError = "Jira accepted the write but stored no estimate - Original Estimate "
                          + "is probably not on the issue's edit screen, or time tracking is off "
                          + "for this project.";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }

            _log.LogWarning("Writing the estimate for {Ticket} did not take on attempt {Attempt}: {Error}",
                ticketKey, attempt, lastError);
        }

        Fail(ticketKey, EstimateGate.Effect, lastError ?? "the estimate was not stored.",
            urgent: false);
    }

    private async Task<bool> StoredMatchesAsync(string ticketKey, int minutes, CancellationToken ct)
    {
        var issue = await _issues.GetIssueAsync(ticketKey, ct).ConfigureAwait(false);
        return issue.Fields.TimeTracking?.OriginalEstimateSeconds == minutes * 60;
    }

    /// <summary>
    /// Uses the stored mapping, else guesses from the folder name and remembers the guess. An
    /// unmappable repo is reported once and skipped - estimating against the wrong project's
    /// tickets would be worse than doing nothing.
    /// </summary>
    private async Task<string?> ResolveProjectAsync(string repoPath, CancellationToken ct)
    {
        var mapped = _mappings.Find(repoPath);
        if (mapped is not null) return mapped;

        IReadOnlyList<JiraProject> projects;
        try
        {
            projects = await _projects.ListProjectsAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            WarnRepoOnce(repoPath, "projects", "Cannot read your Jira projects",
                ex.Message, urgent: false);
            return null;
        }

        var matched = RepoProjectMatcher.Match(repoPath, projects);
        if (matched is null)
        {
            WarnRepoOnce(repoPath, "unmapped",
                $"No Jira project matches {Name(repoPath)}",
                "Estimation is skipped for this repo. The folder name has to match a Jira board "
                + "name for it to be found automatically.",
                urgent: false);
            return null;
        }

        _mappings.Save(repoPath, matched, autoMatched: true);
        _log.LogInformation("Matched {Repo} to Jira project {Project}", repoPath, matched);
        return matched;
    }

    private bool IsSettled(string ticketKey)
    {
        var row = _estimates.Find(ticketKey);
        return row is not null && EstimateStatus.IsTerminal(row.Status);
    }

    private void Fail(string ticketKey, EstimateGate? gate, string error, bool urgent)
    {
        _estimates.MarkFailed(ticketKey, gate, error);

        // warned_at lives in the row, so a restart cannot re-notify about the same failure.
        if (_estimates.Find(ticketKey)?.WarnedAtUtc is not null) return;

        _notifier.Show($"{ticketKey}: could not estimate", Describe(gate, error), urgent);
        _estimates.MarkWarned(ticketKey, _clock.UtcNow);
    }

    /// <summary>
    /// Folds the run's own explanation into the failure. Without stderr and the budget state, a
    /// run stopped by --max-budget-usd is indistinguishable from a crash: both are "exited with
    /// code 1", and the actual cause only exists in output the worker was throwing away.
    /// </summary>
    private static string Detail(string? error, ClaudeRun run)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(error)) parts.Add(error.Trim());
        if (run.TimedOut) parts.Add("The run hit its wall-clock timeout.");

        if (run.Stdout.Contains("\"budget_usd\"", StringComparison.Ordinal) ||
            run.Stderr.Contains("budget", StringComparison.OrdinalIgnoreCase))
            parts.Add("It looks like the run exhausted its --max-budget-usd allowance.");

        var stderr = run.Stderr.Trim();
        if (stderr.Length > 0)
            parts.Add(stderr.Length > 400 ? stderr[..400] + "..." : stderr);

        return string.Join(" ", parts);
    }

    private static string Describe(EstimateGate? gate, string error) => gate switch
    {
        EstimateGate.Effect => $"Jira did not store the estimate. {error}",
        EstimateGate.Process => $"The estimation run did not complete. {error}",
        EstimateGate.Schema => $"Claude's answer was not in the expected shape. {error}",
        EstimateGate.Sanity => $"Claude's answer was not usable. {error}",
        _ => error
    };

    private void WarnRepoOnce(string repoPath, string kind, string title, string body, bool urgent)
    {
        _log.LogWarning("{Title}: {Body}", title, body);
        if (!_warnedRepoFaults.Add($"{repoPath}|{kind}")) return;
        _notifier.Show(title, body, urgent);
    }

    private static string Name(string repoPath) =>
        Path.GetFileName(repoPath.TrimEnd('/', '\\'));
}
