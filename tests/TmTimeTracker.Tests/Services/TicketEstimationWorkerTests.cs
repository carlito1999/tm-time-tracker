using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TmTimeTracker.Data;
using TmTimeTracker.GitLab;
using TmTimeTracker.Jira;
using TmTimeTracker.Logic;
using TmTimeTracker.Platform;
using TmTimeTracker.Services;
using TmTimeTracker.Tests.Data;
using TmTimeTracker.UI;
using Xunit;

namespace TmTimeTracker.Tests.Services;

public class TicketEstimationWorkerTests
{
    private const string Repo = @"C:\projects\training-manager";

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 9, 3, 11, 0, 0, DateTimeKind.Utc);
        public DateTimeOffset LocalNow => new(UtcNow.ToLocalTime());
    }

    /// <summary>
    /// Stands in for Jira's stored value. The writer only updates it when
    /// <see cref="AcceptsWrites"/> is true, which is how the gate-4 tests reproduce the real
    /// hazard: Jira returns 2xx and stores nothing when the field is off the edit screen.
    /// </summary>
    private sealed class FakeJira
    {
        public int? StoredSeconds { get; set; }
        public bool AcceptsWrites { get; set; } = true;
        public int WriteCount { get; private set; }

        public Task Write(string key, int minutes, CancellationToken ct)
        {
            WriteCount++;
            if (AcceptsWrites) StoredSeconds = minutes * 60;
            return Task.CompletedTask;
        }
    }

    private static string Output(int impl = 90, int test = 30, int review = 20,
        string confidence = "medium", string rationale = "Touches the poll loop.") =>
        "[{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"structured_output\":{"
        + $"\"implementation_minutes\":{impl},\"testing_minutes\":{test},"
        + $"\"review_minutes\":{review},\"confidence\":\"{confidence}\","
        + $"\"rationale\":\"{rationale}\""
        + "}}]";

    private const string GarbageOutput = """[{"type":"result","subtype":"success","is_error":false}]""";

    private sealed class Harness
    {
        public required TicketEstimationWorker Worker { get; init; }
        public required TicketEstimateRepository Estimates { get; init; }
        public required RepoProjectRepository Mappings { get; init; }
        public required FakeJira Jira { get; init; }
        public required Mock<IClaudeEstimator> Claude { get; init; }
        public required Mock<IUserNotifier> Notifier { get; init; }
        public required Mock<IGitWorktreeManager> Worktrees { get; init; }
        public required Mock<IGitLabIssueSource> GitLab { get; init; }
        public required List<string> Prompts { get; init; }

        public string LastPrompt => Prompts[^1];
        public required List<string> Jql { get; init; }
    }

    private static Harness Build(
        string? mappedProject = "TM",
        IReadOnlyList<Issue>? issues = null,
        Queue<string>? claudeOutputs = null,
        bool jiraAcceptsWrites = true,
        int? existingEstimateSeconds = null,
        IReadOnlyList<JiraProject>? projects = null,
        JsonElement? description = null)
    {
        var factory = SharedSqlite.NewInMemory();
        new DatabaseInitializer(factory).EnsureCreated();

        var repos = new TrackedRepoRepository(factory);
        repos.Add(Repo);

        var mappings = new RepoProjectRepository(factory);
        if (mappedProject is not null) mappings.Save(Repo, mappedProject, autoMatched: true);

        var estimates = new TicketEstimateRepository(factory);
        var jira = new FakeJira { AcceptsWrites = jiraAcceptsWrites, StoredSeconds = existingEstimateSeconds };

        var jql = new List<string>();
        var search = new Mock<IJiraSearchSource>();
        search.Setup(s => s.SearchIssuesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Callback<string, CancellationToken>((q, _) => jql.Add(q))
              .ReturnsAsync(issues ?? new[] { Issue("TM-1", existingEstimateSeconds, description) });

        var reader = new Mock<IJiraIssueSource>();
        reader.Setup(r => r.GetIssueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((string k, CancellationToken _) => Issue(k, jira.StoredSeconds));

        var writer = new Mock<IJiraEstimateWriter>();
        writer.Setup(w => w.SetOriginalEstimateAsync(It.IsAny<string>(), It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
              .Returns((string k, int m, CancellationToken ct) => jira.Write(k, m, ct));

        var projectSource = new Mock<IJiraProjectSource>();
        projectSource.Setup(p => p.ListProjectsAsync(It.IsAny<CancellationToken>()))
                     .ReturnsAsync(projects ?? new[] { new JiraProject("TM", "Training Manager") });

        var outputs = claudeOutputs ?? new Queue<string>(new[] { Output() });
        var prompts = new List<string>();
        var claude = new Mock<IClaudeEstimator>();
        claude.Setup(c => c.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Callback<string, string, CancellationToken>((_, prompt, _) => prompts.Add(prompt))
              .ReturnsAsync(() => new ClaudeRun(0, outputs.Count > 0 ? outputs.Dequeue() : Output(),
                  "", TimedOut: false));

        var worktrees = new Mock<IGitWorktreeManager>();
        worktrees.Setup(w => w.PrepareAsync(It.IsAny<string>(), It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                 .ReturnsAsync(@"C:\estimates\training-manager");
        worktrees.Setup(w => w.RecentCommitSubjectsAsync(It.IsAny<string>(), It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new[] { "feat: previous change" });

        var notifier = new Mock<IUserNotifier>();

        var attachmentSource = new Mock<IJiraAttachmentSource>();
        attachmentSource.Setup(a => a.DownloadAttachmentAsync(It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                        .ReturnsAsync(Array.Empty<byte>());
        var fetcher = new TicketAttachmentFetcher(attachmentSource.Object,
            NullLogger<TicketAttachmentFetcher>.Instance);

        // Reads nothing unless a test says otherwise: most tickets carry no linked issue, and a
        // fetch that returns null must leave the estimate exactly as it was.
        var gitlab = new Mock<IGitLabIssueSource>();
        gitlab.Setup(g => g.FetchAsync(It.IsAny<GitLabIssueRef>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((LinkedIssue?)null);

        return new Harness
        {
            Worker = new TicketEstimationWorker(repos, mappings,
                new RepoBranchRepository(factory), estimates, search.Object,
                reader.Object, writer.Object, projectSource.Object, claude.Object,
                worktrees.Object, fetcher, gitlab.Object, notifier.Object, new FixedClock(),
                NullLogger<TicketEstimationWorker>.Instance),
            Estimates = estimates,
            Mappings = mappings,
            Jira = jira,
            Claude = claude,
            Notifier = notifier,
            Worktrees = worktrees,
            GitLab = gitlab,
            Prompts = prompts,
            Jql = jql
        };
    }

    private static Issue Issue(string key, int? estimateSeconds = null,
        JsonElement? description = null) =>
        new(key, new IssueFields(
            new IssueStatus("To Do", new StatusCategory("new", "To Do")),
            Summary: "Add a retry to the poll loop",
            TimeTracking: estimateSeconds is null ? null : new JiraTimeTracking(estimateSeconds),
            Description: description),
            Id: "10001");

    /// <summary>An ADF description whose only content is a link, the way SN-305's is.</summary>
    private static JsonElement LinkOnlyDescription(string url) =>
        JsonDocument.Parse(
            "{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":["
            + "{\"type\":\"inlineCard\",\"attrs\":{\"url\":\"" + url + "\"}}]}]}").RootElement;

    // --- the happy path ------------------------------------------------------------------

    // 90+30+20 = 140, which is not on the quarter-hour grid, so Jira receives 150. The raw
    // phases are still what Records_the_three_phases_it_was_given asserts.
    [Fact]
    public async Task Writes_the_total_estimate_to_jira_rounded_up_to_a_quarter_hour()
    {
        var h = Build();

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Jira.StoredSeconds.Should().Be(150 * 60);
        h.Estimates.Find("TM-1")!.Status.Should().Be(EstimateStatus.Done);
    }

    // Gate 4 compares Jira's stored value against what was written, so it has to read back the
    // rounded figure - comparing against the raw total would fail every time it rounded.
    [Fact]
    public async Task Passes_gate_four_against_the_rounded_figure()
    {
        var h = Build();

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Estimates.Find("TM-1")!.Status.Should().Be(EstimateStatus.Done);
        h.Jira.StoredSeconds.Should().Be(EstimateRounding.CeilingToQuarterHour(140) * 60);
    }

    [Fact]
    public async Task Records_the_three_phases_it_was_given()
    {
        var h = Build();

        await h.Worker.RunOnceAsync(CancellationToken.None);

        var row = h.Estimates.Find("TM-1")!;
        row.ImplementationMinutes.Should().Be(90);
        row.TestingMinutes.Should().Be(30);
        row.ReviewMinutes.Should().Be(20);
    }

    [Fact]
    public async Task Searches_only_for_to_do_tickets_in_the_mapped_project()
    {
        var h = Build();

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Jql.Single().Should().Contain("TM").And.Contain("To Do");
    }

    /// <summary>
    /// Jira's category "To Do" also covers Backlog, Open and Selected for Development. Searching
    /// by category pulled in tickets the user does not regard as To Do at all, and each one costs
    /// a real Claude session.
    /// </summary>
    [Fact]
    public async Task Searches_by_status_not_by_the_broader_status_category()
    {
        var h = Build();

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Jql.Single().Should().NotContain("statusCategory");
    }

    [Fact]
    public async Task Cleans_up_the_worktree_afterwards()
    {
        var h = Build();

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Worktrees.Verify(w => w.Remove(Repo), Times.AtLeastOnce);
    }

    // --- not re-doing settled work -------------------------------------------------------

    // Never clobber a human's estimate. This is what makes an unattended loop acceptable.
    [Fact]
    public async Task Leaves_a_ticket_that_already_has_an_estimate_alone()
    {
        var h = Build(existingEstimateSeconds: 3600);

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Jira.WriteCount.Should().Be(0);
        h.Claude.Verify(c => c.RunAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        h.Estimates.Find("TM-1")!.Status.Should().Be(EstimateStatus.SkippedExisting);
    }

    [Fact]
    public async Task Does_not_re_estimate_a_finished_ticket_on_the_next_sweep()
    {
        var h = Build();
        await h.Worker.RunOnceAsync(CancellationToken.None);

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Claude.Verify(c => c.RunAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Does_not_retry_a_ticket_that_already_failed_twice()
    {
        var outputs = new Queue<string>(new[] { GarbageOutput, GarbageOutput });
        var h = Build(claudeOutputs: outputs);
        await h.Worker.RunOnceAsync(CancellationToken.None);
        h.Claude.Invocations.Clear();

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Claude.Verify(c => c.RunAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- gates 1-3: retry the session ----------------------------------------------------

    [Fact]
    public async Task Runs_claude_again_when_the_first_answer_fails_a_gate()
    {
        var outputs = new Queue<string>(new[] { GarbageOutput, Output() });
        var h = Build(claudeOutputs: outputs);

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Claude.Verify(c => c.RunAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        h.Estimates.Find("TM-1")!.Status.Should().Be(EstimateStatus.Done);
    }

    [Fact]
    public async Task Gives_up_and_notifies_after_two_failed_runs()
    {
        var outputs = new Queue<string>(new[] { GarbageOutput, GarbageOutput });
        var h = Build(claudeOutputs: outputs);

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Claude.Verify(c => c.RunAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        h.Estimates.Find("TM-1")!.Status.Should().Be(EstimateStatus.Failed);
        h.Notifier.Verify(n => n.Show(It.Is<string>(t => t.Contains("TM-1")),
            It.IsAny<string>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task Records_which_gate_rejected_the_run()
    {
        var outputs = new Queue<string>(new[] { GarbageOutput, GarbageOutput });
        var h = Build(claudeOutputs: outputs);

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Estimates.Find("TM-1")!.FailedGate.Should().Be(nameof(EstimateGate.Schema));
    }

    // --- gate 4: retry the write, not the session ----------------------------------------

    /// <summary>
    /// The gate that catches a "done" that is not done: Jira answers 2xx and stores nothing
    /// when timetracking is off the edit screen.
    /// </summary>
    [Fact]
    public async Task Fails_when_jira_accepts_the_write_but_stores_nothing()
    {
        var h = Build(jiraAcceptsWrites: false);

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Estimates.Find("TM-1")!.Status.Should().Be(EstimateStatus.Failed);
        h.Estimates.Find("TM-1")!.FailedGate.Should().Be(nameof(EstimateGate.Effect));
    }

    /// <summary>
    /// Re-running a ten-minute session cannot fix a field missing from a Jira screen, and the
    /// estimate already in hand is fine - so the retry targets the write.
    /// </summary>
    [Fact]
    public async Task Retries_the_write_rather_than_the_session_when_jira_drops_it()
    {
        var h = Build(jiraAcceptsWrites: false);

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Jira.WriteCount.Should().Be(2);
        h.Claude.Verify(c => c.RunAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Notifies_when_jira_will_not_store_the_estimate()
    {
        var h = Build(jiraAcceptsWrites: false);

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Notifier.Verify(n => n.Show(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Once);
    }

    // --- notification hygiene -------------------------------------------------------------

    // The sweep runs every five minutes; one broken ticket must not become a toast every five
    // minutes forever.
    [Fact]
    public async Task Notifies_once_about_the_same_failure_not_on_every_sweep()
    {
        var outputs = new Queue<string>(new[] { GarbageOutput, GarbageOutput });
        var h = Build(claudeOutputs: outputs);

        await h.Worker.RunOnceAsync(CancellationToken.None);
        await h.Worker.RunOnceAsync(CancellationToken.None);
        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Notifier.Verify(n => n.Show(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Once);
    }

    // --- repo to project mapping ----------------------------------------------------------

    [Fact]
    public async Task Auto_maps_a_repo_whose_name_matches_a_project()
    {
        var h = Build(mappedProject: null);

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Mappings.Find(Repo).Should().Be("TM");
    }

    [Fact]
    public async Task Notifies_and_skips_a_repo_it_cannot_map()
    {
        var h = Build(mappedProject: null,
            projects: new[] { new JiraProject("XX", "Something Unrelated") });

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Claude.Verify(c => c.RunAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        h.Notifier.Verify(n => n.Show(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Once);
    }

    // --- infrastructure failures -----------------------------------------------------------

    [Fact]
    public async Task Notifies_when_the_worktree_cannot_be_prepared()
    {
        var h = Build();
        h.Worktrees.Setup(w => w.PrepareAsync(It.IsAny<string>(), It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new GitWorktreeException("no origin"));

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Notifier.Verify(n => n.Show(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Once);
        h.Jira.WriteCount.Should().Be(0);
    }

    // Nothing works until the user acts, so this one has to break through Do Not Disturb.
    [Fact]
    public async Task Raises_an_urgent_notification_when_claude_is_not_installed()
    {
        var h = Build();
        h.Claude.Setup(c => c.RunAsync(It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ClaudeUnavailableException("not found"));

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Notifier.Verify(n => n.Show(It.IsAny<string>(), It.IsAny<string>(), true), Times.Once);
    }

    /// <summary>
    /// A first run against an existing backlog would otherwise estimate every To-Do ticket back
    /// to back, each up to ten minutes, on the same subscription quota the user's own Claude
    /// sessions draw from. Later sweeps drain the rest.
    /// </summary>
    [Fact]
    public async Task Estimates_at_most_a_few_tickets_per_sweep()
    {
        var backlog = Enumerable.Range(1, 12).Select(i => Issue($"TM-{i}")).ToArray();
        var h = Build(issues: backlog,
            claudeOutputs: new Queue<string>(Enumerable.Repeat(Output(), 12)));

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Claude.Invocations.Count.Should().BeLessThan(backlog.Length);
        h.Estimates.GetAll().Count(r => r.Status == EstimateStatus.Done)
            .Should().BeLessThan(backlog.Length);
    }

    [Fact]
    public async Task Works_through_the_backlog_over_successive_sweeps()
    {
        var backlog = Enumerable.Range(1, 5).Select(i => Issue($"TM-{i}")).ToArray();
        var h = Build(issues: backlog,
            claudeOutputs: new Queue<string>(Enumerable.Repeat(Output(), 10)));

        await h.Worker.RunOnceAsync(CancellationToken.None);
        var afterFirst = h.Estimates.GetAll().Count(r => r.Status == EstimateStatus.Done);

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Estimates.GetAll().Count(r => r.Status == EstimateStatus.Done)
            .Should().BeGreaterThan(afterFirst);
    }

    // One unusable repo or ticket must not stop the rest of the sweep.
    [Fact]
    public async Task Carries_on_with_other_tickets_after_one_fails()
    {
        var outputs = new Queue<string>(new[] { GarbageOutput, GarbageOutput, Output(), Output() });
        var h = Build(issues: new[] { Issue("TM-1"), Issue("TM-2") }, claudeOutputs: outputs);

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Estimates.Find("TM-1")!.Status.Should().Be(EstimateStatus.Failed);
        h.Estimates.Find("TM-2")!.Status.Should().Be(EstimateStatus.Done);
    }

    // --- linked GitLab issues ------------------------------------------------------------

    // SN-305's description was nothing but a link to work item 377, and the estimate came back
    // saying the root cause was unknown from the ticket alone.
    [Fact]
    public async Task Fetches_a_gitlab_issue_linked_from_the_description_and_puts_it_in_the_prompt()
    {
        var h = Build(description: LinkOnlyDescription(
            "https://gitlab.com/si-bv/stamboekonline/-/work_items/377"));

        h.GitLab
         .Setup(g => g.FetchAsync(
             It.Is<GitLabIssueRef>(l => l.Iid == 377 && l.ProjectPath == "si-bv/stamboekonline"),
             It.IsAny<CancellationToken>()))
         .ReturnsAsync(new LinkedIssue(
             "https://gitlab.com/si-bv/stamboekonline/-/work_items/377", 377,
             "si-bv/stamboekonline", "Ancestry is not shown",
             "not the ancestry overview", Array.Empty<string>()));

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.LastPrompt.Should().Contain("not the ancestry overview");
    }

    // The link lives in an inlineCard, which carries no text at all, so a worker reading the
    // flattened description would never see it.
    [Fact]
    public async Task Finds_the_link_even_though_flattening_the_description_yields_nothing()
    {
        var h = Build(description: LinkOnlyDescription("https://gitlab.com/a/b/-/issues/9"));

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.GitLab.Verify(g => g.FetchAsync(
            It.Is<GitLabIssueRef>(l => l.Iid == 9), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Still_estimates_when_the_linked_issue_cannot_be_read()
    {
        var h = Build(description: LinkOnlyDescription("https://gitlab.com/a/b/-/issues/1"));

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.Jira.StoredSeconds.Should().NotBeNull();
        h.Estimates.Find("TM-1")!.Status.Should().Be(EstimateStatus.Done);
    }

    [Fact]
    public async Task Does_not_call_gitlab_when_the_description_has_no_link()
    {
        var h = Build();

        await h.Worker.RunOnceAsync(CancellationToken.None);

        h.GitLab.Verify(g => g.FetchAsync(
            It.IsAny<GitLabIssueRef>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
