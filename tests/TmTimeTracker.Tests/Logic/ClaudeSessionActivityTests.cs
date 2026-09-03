using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

/// <summary>
/// Claude Code writes ~/.claude/sessions/&lt;pid&gt;.json and latches "status" to "busy" for the
/// whole turn - including the minutes inside a single long tool call, when nothing is appended
/// to the transcript. These tests pin the rule that turns those files into "this repo is being
/// worked on right now", and just as importantly the cases where it must stay silent.
/// </summary>
public class ClaudeSessionActivityTests
{
    private const string Repo = @"C:\projects\sheeponline-new";
    private const long Start = 134329052784643818;

    private static ClaudeSession Session(string? cwd = Repo, string? status = "busy",
        int pid = 1234, long? procStart = Start) =>
        new(pid, cwd, status, procStart);

    private static IReadOnlySet<string> Busy(ClaudeSession s, bool alive = true,
        string repo = Repo) =>
        ClaudeSessionActivity.BusyRepos(new[] { s }, new[] { repo }, _ => alive);

    [Fact]
    public void Busy_session_in_a_tracked_repo_with_a_live_process_is_active()
    {
        Busy(Session()).Should().BeEquivalentTo(new[] { Repo });
    }

    [Fact]
    public void Busy_session_whose_process_is_gone_is_not_active()
    {
        // The safety valve for an uncapped window: a session killed mid-tool-call never writes
        // a stop record, so without this it would bill its repo forever.
        Busy(Session(), alive: false).Should().BeEmpty();
    }

    [Fact]
    public void Idle_session_is_not_active()
    {
        Busy(Session(status: "idle")).Should().BeEmpty();
    }

    [Fact]
    public void Session_with_no_status_field_is_not_active()
    {
        // Older Claude Code builds and freshly-created sdk-cli files omit "status" entirely.
        // Those must fall through to the transcript-mtime rule, not be guessed at.
        Busy(Session(status: null)).Should().BeEmpty();
    }

    [Fact]
    public void Unrecognised_status_is_not_active()
    {
        // Positive match on "busy" only. If a future build reports something like "waiting" for
        // an unanswered permission prompt, we must not bill it.
        Busy(Session(status: "waiting")).Should().BeEmpty();
    }

    [Fact]
    public void Status_match_is_case_insensitive()
    {
        Busy(Session(status: "BUSY")).Should().BeEquivalentTo(new[] { Repo });
    }

    [Fact]
    public void Drive_letter_case_does_not_matter()
    {
        // Session files have been seen with a lowercase drive letter; the DB stores uppercase.
        Busy(Session(cwd: @"c:\projects\sheeponline-new")).Should().BeEquivalentTo(new[] { Repo });
    }

    [Fact]
    public void Trailing_separator_does_not_matter()
    {
        Busy(Session(cwd: @"C:\projects\sheeponline-new\")).Should().BeEquivalentTo(new[] { Repo });
    }

    [Fact]
    public void A_sibling_repo_sharing_a_name_prefix_is_not_matched()
    {
        // Plain string prefix matching would bill sheeponline-new for work done in -new-2.
        Busy(Session(cwd: @"C:\projects\sheeponline-new-2")).Should().BeEmpty();
    }

    [Fact]
    public void A_worktree_under_the_repo_is_not_matched()
    {
        // Deliberate: the monitor reads the branch from the repo path, so crediting a worktree
        // session here would bill the main checkout's ticket - the wrong one - and would put two
        // tickets on one repo path, which the aggregator's one-ticket-per-repo model forbids.
        Busy(Session(cwd: @"C:\projects\sheeponline-new\.claude\worktrees\gitlab-mcp-43e105"))
            .Should().BeEmpty();
    }

    [Fact]
    public void The_estimators_own_worktree_never_bills_the_repo_it_estimates()
    {
        // TicketEstimationWorker spawns headless Claude under LOCALAPPDATA. If that counted, the
        // tracker would invent time for every ticket it estimated.
        Busy(Session(cwd: @"C:\Users\TSEGKOS\AppData\Local\TmTimeTracker\estimates\sheeponline-new"))
            .Should().BeEmpty();
    }

    [Fact]
    public void A_busy_session_outside_every_tracked_repo_is_ignored()
    {
        Busy(Session(cwd: @"C:\projects\tm-time-tracker")).Should().BeEmpty();
    }

    [Fact]
    public void Null_or_blank_cwd_is_ignored()
    {
        Busy(Session(cwd: null)).Should().BeEmpty();
        Busy(Session(cwd: "   ")).Should().BeEmpty();
    }

    [Fact]
    public void Malformed_cwd_is_ignored_rather_than_throwing()
    {
        Busy(Session(cwd: new string('x', 40_000))).Should().BeEmpty();
    }

    [Fact]
    public void Concurrent_sessions_light_up_every_repo_they_are_busy_in()
    {
        const string other = @"C:\projects\training-manager";
        var sessions = new[]
        {
            Session(cwd: Repo, pid: 1),
            Session(cwd: other, pid: 2),
            Session(cwd: @"C:\projects\dynatag", status: "idle", pid: 3),
        };
        ClaudeSessionActivity
            .BusyRepos(sessions, new[] { Repo, other, @"C:\projects\dynatag" }, _ => true)
            .Should().BeEquivalentTo(new[] { Repo, other });
    }

    [Fact]
    public void Liveness_is_only_asked_about_sessions_that_could_matter()
    {
        // Process lookups are the expensive part of a tick that runs every minute; an idle or
        // unmatched session must never cost one.
        var asked = new List<int>();
        ClaudeSessionActivity.BusyRepos(
            new[] { Session(status: "idle", pid: 7), Session(cwd: @"C:\elsewhere", pid: 8),
                    Session(pid: 9) },
            new[] { Repo },
            s => { asked.Add(s.Pid); return true; });
        asked.Should().Equal(9);
    }

    [Fact]
    public void The_repo_path_is_returned_exactly_as_the_caller_spelled_it()
    {
        // Callers key their own dictionaries off these strings, so the result has to come back in
        // the tracked-repo spelling rather than the session file's.
        ClaudeSessionActivity
            .BusyRepos(new[] { Session(cwd: @"c:\PROJECTS\sheeponline-new\") }, new[] { Repo },
                       _ => true)
            .Should().BeEquivalentTo(new[] { Repo });
    }
}
