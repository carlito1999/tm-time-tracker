using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using TmTimeTracker.Platform;
using TmTimeTracker.Services;
using TmTimeTracker.Tests.Data;
using Xunit;

namespace TmTimeTracker.Tests.Services;

public class RepoActivityMonitorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "tmtt-activity-" + Guid.NewGuid().ToString("N"));

    public RepoActivityMonitorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class FakeForeground : IForegroundWindowProbe
    {
        public ForegroundWindow? Value { get; set; }
        public ForegroundWindow? Probe() => Value;
    }

    private sealed class FakeBranchProbe : IGitBranchProbe
    {
        public Dictionary<string, string?> ByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? GetCurrentBranch(string repoPath) =>
            ByPath.TryGetValue(repoPath, out var b) ? b : null;
    }

    private sealed class FakeClaude : IClaudeCodeActivityProbe
    {
        public Dictionary<string, DateTime> Snapshot { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public bool Throws { get; set; }
        IReadOnlyDictionary<string, DateTime> IClaudeCodeActivityProbe.Snapshot() =>
            Throws ? throw new IOException("probe exploded") : Snapshot;
    }

    private sealed class FakeSessions : IClaudeSessionProbe
    {
        public List<ClaudeSession> Items { get; } = new();
        public bool Throws { get; set; }
        public IReadOnlyList<ClaudeSession> Snapshot() =>
            Throws ? throw new IOException("session probe exploded") : Items;
    }

    private sealed class FakeLiveness : IProcessLiveness
    {
        public HashSet<int> AlivePids { get; } = new();
        public bool IsRunning(ClaudeSession session) => AlivePids.Contains(session.Pid);
    }

    private readonly FakeSessions _sessions = new();
    private readonly FakeLiveness _liveness = new();

    /// <summary>A busy session Claude Code would have written for <paramref name="repoPath"/>.</summary>
    private void BusySessionIn(string repoPath, int pid = 4242, bool alive = true)
    {
        _sessions.Items.Add(new ClaudeSession(pid, repoPath, "busy", 1));
        if (alive) _liveness.AlivePids.Add(pid);
    }

    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        public DateTimeOffset LocalNow => new(UtcNow, TimeSpan.Zero);
    }

    /// <summary>
    /// A real directory, because the monitor skips repos that are gone from disk. Deliberately
    /// has no .git/HEAD, so the resolver's mtime tier cannot fire and each test drives exactly
    /// the signal it names.
    /// </summary>
    private string MakeRepo(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private (RepoActivityMonitor mon, FakeForeground fg, FakeClaude claude,
             FakeBranchProbe git, FakeClock clk)
        Build(params string[] repoPaths)
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        var repos = new TrackedRepoRepository(ds);
        foreach (var p in repoPaths) repos.Add(p);

        var fg = new FakeForeground();
        var claude = new FakeClaude();
        var git = new FakeBranchProbe();
        var clk = new FakeClock();
        var resolver = new ActiveRepoResolver(repos, fg, git, clk,
            NullLogger<ActiveRepoResolver>.Instance);
        var mon = new RepoActivityMonitor(repos, resolver, claude, _sessions, _liveness, git,
            NullLogger<RepoActivityMonitor>.Instance);
        return (mon, fg, claude, git, clk);
    }

    private static ForegroundWindow VsCodeOn(string folder) =>
        new("Code", $"Program.cs - {folder} - Visual Studio Code");

    [Fact]
    public void Samples_the_focused_repo_and_a_Claude_repo_together()
    {
        var a = MakeRepo("sheepsonline");
        var b = MakeRepo("training-manager");
        var (mon, fg, claude, git, clk) = Build(a, b);
        git.ByPath[a] = "SN-299-x";
        git.ByPath[b] = "TM-30-y";

        fg.Value = VsCodeOn("sheepsonline");
        claude.Snapshot[ClaudeProjectSlug.FromPath(b)] = clk.UtcNow;

        var sample = mon.Sample(clk.UtcNow.AddMinutes(-1), humanActive: true);

        sample.Select(s => s.TicketKey).Should().BeEquivalentTo(new[] { "SN-299", "TM-30" });
    }

    [Fact]
    public void Claude_repo_is_sampled_even_when_the_human_is_idle()
    {
        var b = MakeRepo("training-manager");
        var (mon, _, claude, git, clk) = Build(b);
        git.ByPath[b] = "TM-30-y";
        claude.Snapshot[ClaudeProjectSlug.FromPath(b)] = clk.UtcNow;

        var sample = mon.Sample(clk.UtcNow.AddMinutes(-1), humanActive: false);

        sample.Should().ContainSingle().Which.TicketKey.Should().Be("TM-30");
    }

    [Fact]
    public void Human_repo_is_not_sampled_when_idle()
    {
        var a = MakeRepo("sheepsonline");
        var (mon, fg, _, git, clk) = Build(a);
        git.ByPath[a] = "SN-299-x";
        fg.Value = VsCodeOn("sheepsonline");

        mon.Sample(clk.UtcNow.AddMinutes(-1), humanActive: false).Should().BeEmpty();
    }

    [Fact]
    public void Claude_writes_older_than_the_last_tick_are_not_sampled()
    {
        var b = MakeRepo("training-manager");
        var (mon, _, claude, git, clk) = Build(b);
        git.ByPath[b] = "TM-30-y";
        claude.Snapshot[ClaudeProjectSlug.FromPath(b)] = clk.UtcNow.AddMinutes(-5);

        mon.Sample(clk.UtcNow.AddMinutes(-1), humanActive: false).Should().BeEmpty();
    }

    [Fact]
    public void A_repo_both_focused_and_Claude_active_appears_once()
    {
        var a = MakeRepo("sheepsonline");
        var (mon, fg, claude, git, clk) = Build(a);
        git.ByPath[a] = "SN-299-x";
        fg.Value = VsCodeOn("sheepsonline");
        claude.Snapshot[ClaudeProjectSlug.FromPath(a)] = clk.UtcNow;

        mon.Sample(clk.UtcNow.AddMinutes(-1), humanActive: true).Should().ContainSingle();
    }

    [Fact]
    public void Claude_activity_for_an_untracked_repo_is_ignored()
    {
        var a = MakeRepo("sheepsonline");
        var (mon, _, claude, git, clk) = Build(a);
        git.ByPath[a] = "SN-299-x";
        claude.Snapshot["c--projects-untracked-side-project"] = clk.UtcNow;

        mon.Sample(clk.UtcNow.AddMinutes(-1), humanActive: false).Should().BeEmpty();
    }

    [Fact]
    public void A_branch_with_no_ticket_yields_a_null_ticket_key()
    {
        var b = MakeRepo("training-manager");
        var (mon, _, claude, git, clk) = Build(b);
        git.ByPath[b] = "main";
        claude.Snapshot[ClaudeProjectSlug.FromPath(b)] = clk.UtcNow;

        var sample = mon.Sample(clk.UtcNow.AddMinutes(-1), humanActive: false);

        sample.Should().ContainSingle().Which.TicketKey.Should().BeNull();
    }

    [Fact]
    public void A_tracked_repo_missing_from_disk_is_skipped()
    {
        var ghost = Path.Combine(_root, "deleted-repo");
        var (mon, _, claude, git, clk) = Build(ghost);
        git.ByPath[ghost] = "TM-30-y";
        claude.Snapshot[ClaudeProjectSlug.FromPath(ghost)] = clk.UtcNow;

        mon.Sample(clk.UtcNow.AddMinutes(-1), humanActive: false).Should().BeEmpty();
    }

    [Fact]
    public void A_throwing_Claude_probe_does_not_break_the_human_stream()
    {
        var a = MakeRepo("sheepsonline");
        var (mon, fg, claude, git, clk) = Build(a);
        git.ByPath[a] = "SN-299-x";
        fg.Value = VsCodeOn("sheepsonline");
        claude.Throws = true;

        var sample = mon.Sample(clk.UtcNow.AddMinutes(-1), humanActive: true);

        sample.Should().ContainSingle().Which.TicketKey.Should().Be("SN-299");
    }

    [Fact]
    public void A_busy_Claude_session_earns_a_minute_with_no_transcript_write_at_all()
    {
        // The reported bug. Inside a long tool call Claude Code appends nothing to the
        // transcript, so the mtime rule sees an idle repo and a ten-minute test run bills zero.
        var b = MakeRepo("sheepsonline");
        var (mon, _, _, git, clk) = Build(b);
        git.ByPath[b] = "SN-279-x";
        BusySessionIn(b);

        var sample = mon.Sample(clk.UtcNow.AddMinutes(-1), humanActive: false);

        sample.Should().ContainSingle().Which.TicketKey.Should().Be("SN-279");
    }

    [Fact]
    public void A_busy_session_keeps_earning_however_stale_the_transcript_is()
    {
        // There is no time cap: the repo earns until Claude says it stopped.
        var b = MakeRepo("sheepsonline");
        var (mon, _, claude, git, clk) = Build(b);
        git.ByPath[b] = "SN-279-x";
        claude.Snapshot[ClaudeProjectSlug.FromPath(b)] = clk.UtcNow.AddHours(-3);
        BusySessionIn(b);

        mon.Sample(clk.UtcNow.AddMinutes(-1), humanActive: false).Should().ContainSingle();
    }

    [Fact]
    public void A_busy_session_whose_process_is_gone_earns_nothing()
    {
        // Closing VS Code mid-tool-call leaves status latched at "busy" forever. Process death
        // is the only thing that ends that run, so it has to be checked.
        var b = MakeRepo("sheepsonline");
        var (mon, _, _, git, clk) = Build(b);
        git.ByPath[b] = "SN-279-x";
        BusySessionIn(b, alive: false);

        mon.Sample(clk.UtcNow.AddMinutes(-1), humanActive: false).Should().BeEmpty();
    }

    [Fact]
    public void An_idle_session_earns_nothing()
    {
        var b = MakeRepo("sheepsonline");
        var (mon, _, _, git, clk) = Build(b);
        git.ByPath[b] = "SN-279-x";
        _sessions.Items.Add(new ClaudeSession(7, b, "idle", 1));
        _liveness.AlivePids.Add(7);

        mon.Sample(clk.UtcNow.AddMinutes(-1), humanActive: false).Should().BeEmpty();
    }

    [Fact]
    public void A_busy_session_in_the_focused_repo_does_not_book_it_twice()
    {
        // One repo, one ticket, one minute - whether the human, Claude, or both are working it.
        var a = MakeRepo("sheepsonline");
        var (mon, fg, _, git, clk) = Build(a);
        git.ByPath[a] = "SN-279-x";
        fg.Value = VsCodeOn("sheepsonline");
        BusySessionIn(a);

        mon.Sample(clk.UtcNow.AddMinutes(-1), humanActive: true)
            .Should().ContainSingle().Which.RepoPath.Should().Be(a);
    }

    [Fact]
    public void A_failing_session_probe_falls_back_to_the_transcript_rule()
    {
        var b = MakeRepo("sheepsonline");
        var (mon, _, claude, git, clk) = Build(b);
        git.ByPath[b] = "SN-279-x";
        claude.Snapshot[ClaudeProjectSlug.FromPath(b)] = clk.UtcNow;
        _sessions.Throws = true;

        mon.Sample(clk.UtcNow.AddMinutes(-1), humanActive: false).Should().ContainSingle();
    }
}
