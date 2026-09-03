# Concurrent Per-Repo Time Tracking Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Credit time to every repo with work happening in it concurrently — one human stream plus N Claude streams — and stop the carry buffer leaking minutes between repos.

**Architecture:** `TimeAggregator` stops receiving `BranchChanged` for attribution and instead pulls a credit set from a new `IRepoActivitySource` once per 60s tick. Attribution state becomes a per-repo dictionary rather than two scalars. `ActiveRepoResolver` loses its Claude tier, because crediting unattended agent work is now the Claude stream's job rather than a hack inside the human stream.

**Tech Stack:** .NET 8, C#, xUnit, FluentAssertions, Dapper + SQLite, Microsoft.Extensions.Hosting.

**Spec:** `docs/superpowers/specs/2026-09-03-concurrent-repo-time-tracking-design.md`

## Global Constraints

- A minute is indivisible. Each active repo gets a **full** minute; the day may exceed wall-clock time. Never split or pro-rate.
- All attribution state keys on **repo path**, case-insensitively (`StringComparer.OrdinalIgnoreCase`) — Windows paths are case-insensitive.
- Crediting is deduplicated by **ticket key** before writing, because `OpenOrCreateCycle` returns the same open cycle for a key regardless of repo.
- The human stream keeps today's idle gating. Claude streams ignore idle entirely and are never capped.
- Carry cap stays **30 minutes**, now per repo.
- Per-repo state stays in memory only. A restart must not resurrect ambiguous time.
- Existing tests run with: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj`

---

### Task 1: Remove the Claude tier from `ActiveRepoResolver`

Tier 0 ranks Claude Code writes above foreground-window focus. It exists only to let unattended agent work be credited under the single-slot model; the Claude stream replaces that job. Leaving it in would let a Claude run in repo B keep stealing the minutes the user types into repo A.

**Files:**
- Modify: `src/TmTimeTracker/Services/ActiveRepoResolver.cs:44-56` (Tier 0 block), and the now-unused `ClaudeActivityWindow`, `GetClaudeMtime`, `_claude` field + constructor parameter
- Test: `tests/TmTimeTracker.Tests/Services/ActiveRepoResolverTests.cs:168-226`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `ActiveRepoResolver` constructor loses its `IClaudeCodeActivityProbe` parameter. New signature:
  ```csharp
  public ActiveRepoResolver(TrackedRepoRepository repos, IForegroundWindowProbe foreground,
      IGitBranchProbe git, IClock clock, ILogger<ActiveRepoResolver> log)
  ```
  `ActiveResolution(string RepoPath, string? Branch, string? TicketKey)` and `LastResolution` are unchanged.

- [ ] **Step 1: Rewrite the Claude-tier tests to assert the opposite**

In `ActiveRepoResolverTests.cs`, delete `Claude_active_beats_VS_Code_window_for_different_repo`, `Claude_stale_beyond_60s_does_not_win`, and `Claude_active_for_untracked_repo_is_ignored`. Replace with one test proving focus now wins:

```csharp
[Fact]
public void VS_Code_window_wins_even_when_another_repo_has_a_live_Claude_session()
{
    var (repos, fg, git, clock) = NewFixture();
    var a = AddRepo(repos, "project-a");
    var b = AddRepo(repos, "project-b");
    git.Branches[b] = "TM-30-x";

    // Focus is on project-b; a Claude session is hammering project-a.
    fg.Result = new ForegroundWindow("Code", "file.cs - project-b - Visual Studio Code");

    var r = NewResolver(repos, fg, git, clock);

    r.Resolve()!.RepoPath.Should().Be(b, "the Claude stream credits project-a separately now");
}
```

Adjust `NewFixture`/`NewResolver`/`AddRepo` to whatever the file's existing helpers are named — the file already has `FakeForeground`, `FakeBranchProbe`, `FakeClock` and a `Build`-style helper. Delete the `FakeClaude` class and every `new FakeClaude()` argument at the remaining construction sites (`ActiveRepoResolverTests.cs:37-41`, `:61`, `:72`, `:84`, `:161`).

- [ ] **Step 2: Run the tests and confirm they fail to compile**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter "FullyQualifiedName~ActiveRepoResolver"`
Expected: build error — `ActiveRepoResolver` still requires an `IClaudeCodeActivityProbe` argument.

- [ ] **Step 3: Delete Tier 0 from the resolver**

In `ActiveRepoResolver.cs`, remove the `ClaudeActivityWindow` constant, the `_claude` field, the constructor parameter, the `GetClaudeMtime` helper, and this whole block:

```csharp
// Tier 0: Claude Code wrote within last 60s for one of our tracked repos
var claudeSnap = _claude.Snapshot();
var freshClaude = tracked
    .Select(r => (repo: r, mtime: GetClaudeMtime(claudeSnap, r.Path)))
    .Where(x => x.mtime.HasValue && (now - x.mtime!.Value) <= ClaudeActivityWindow)
    .OrderByDescending(x => x.mtime!.Value)
    .Select(x => x.repo)
    .FirstOrDefault();
if (freshClaude is not null && Directory.Exists(freshClaude.Path))
{
    _last = LookupBranch(freshClaude.Path);
    return _last;
}
```

Renumber the remaining comments to `Tier 0: VS Code window title`, `Tier 1: recent activity polling`, `Tier 2: sticky`. Also drop the `using TmTimeTracker.Logic;` import if `ClaudeProjectSlug` was its only use — check whether `TicketKeyExtractor` still needs it (it does; keep the import).

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter "FullyQualifiedName~ActiveRepoResolver"`
Expected: PASS. (`HostingExtensions` still compiles — DI resolves the shorter constructor automatically.)

- [ ] **Step 5: Commit**

```bash
git add src/TmTimeTracker/Services/ActiveRepoResolver.cs tests/TmTimeTracker.Tests/Services/ActiveRepoResolverTests.cs
git commit -m "refactor: drop the Claude tier from ActiveRepoResolver"
```

---

### Task 2: Add `RepoActivityMonitor`

The credit set for one tick: the human's repo (when not idle) plus every tracked repo Claude wrote to since the previous tick.

**Files:**
- Create: `src/TmTimeTracker/Services/RepoActivityMonitor.cs`
- Test: `tests/TmTimeTracker.Tests/Services/RepoActivityMonitorTests.cs`

**Interfaces:**
- Consumes: `ActiveRepoResolver` from Task 1 (five-parameter constructor, no Claude probe).
- Produces:
  ```csharp
  public sealed record RepoActivity(string RepoPath, string? Branch, string? TicketKey);

  public interface IRepoActivitySource
  {
      IReadOnlyList<RepoActivity> Sample(DateTime lastTickUtc, bool humanActive);
  }
  ```
  Task 3 depends on both names exactly as written.

- [ ] **Step 1: Write the failing tests**

Create `tests/TmTimeTracker.Tests/Services/RepoActivityMonitorTests.cs`:

```csharp
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
    private readonly List<string> _dirs = new();

    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 9, 3, 9, 0, 0, DateTimeKind.Utc);
        public DateTimeOffset LocalNow => new(UtcNow, TimeSpan.Zero);
    }

    private sealed class FakeForeground : IForegroundWindowProbe
    {
        public ForegroundWindow? Result;
        public ForegroundWindow? Probe() => Result;
    }

    private sealed class FakeClaude : IClaudeCodeActivityProbe
    {
        public readonly Dictionary<string, DateTime> Snapshot = new(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, DateTime> IClaudeCodeActivityProbe.Snapshot() => Snapshot;
    }

    private sealed class FakeBranchProbe : IGitBranchProbe
    {
        public readonly Dictionary<string, string?> Branches = new(StringComparer.OrdinalIgnoreCase);
        public string? GetCurrentBranch(string repoPath) =>
            Branches.TryGetValue(repoPath, out var b) ? b : null;
    }

    /// <summary>A real directory, because the monitor skips repos that are gone from disk.</summary>
    private string NewRepoDir(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "ramt-" + Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(path);
        _dirs.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var d in _dirs)
        {
            try { Directory.Delete(Path.GetDirectoryName(d)!, recursive: true); } catch { }
        }
    }

    private (RepoActivityMonitor mon, TrackedRepoRepository repos, FakeForeground fg,
             FakeClaude claude, FakeBranchProbe git, FakeClock clock) Build()
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        var repos = new TrackedRepoRepository(ds);
        var fg = new FakeForeground();
        var claude = new FakeClaude();
        var git = new FakeBranchProbe();
        var clock = new FakeClock();
        var resolver = new ActiveRepoResolver(repos, fg, git, clock,
            NullLogger<ActiveRepoResolver>.Instance);
        var mon = new RepoActivityMonitor(repos, resolver, claude, git, clock,
            NullLogger<RepoActivityMonitor>.Instance);
        return (mon, repos, fg, claude, git, clock);
    }

    [Fact]
    public void Samples_the_focused_repo_and_a_Claude_repo_together()
    {
        var (mon, repos, fg, claude, git, clock) = Build();
        var a = NewRepoDir("sheepsonline");
        var b = NewRepoDir("training-manager");
        repos.Add(a); repos.Add(b);
        git.Branches[a] = "SN-299-x";
        git.Branches[b] = "TM-30-y";

        fg.Result = new ForegroundWindow("Code", "x.cs - sheepsonline - Visual Studio Code");
        claude.Snapshot[ClaudeProjectSlug.FromPath(b)] = clock.UtcNow;

        var sample = mon.Sample(clock.UtcNow.AddMinutes(-1), humanActive: true);

        sample.Select(s => s.TicketKey).Should().BeEquivalentTo(new[] { "SN-299", "TM-30" });
    }

    [Fact]
    public void Claude_repo_is_sampled_even_when_the_human_is_idle()
    {
        var (mon, repos, fg, claude, git, clock) = Build();
        var b = NewRepoDir("training-manager");
        repos.Add(b);
        git.Branches[b] = "TM-30-y";
        claude.Snapshot[ClaudeProjectSlug.FromPath(b)] = clock.UtcNow;

        var sample = mon.Sample(clock.UtcNow.AddMinutes(-1), humanActive: false);

        sample.Should().ContainSingle().Which.TicketKey.Should().Be("TM-30");
    }

    [Fact]
    public void Human_repo_is_not_sampled_when_idle()
    {
        var (mon, repos, fg, claude, git, clock) = Build();
        var a = NewRepoDir("sheepsonline");
        repos.Add(a);
        git.Branches[a] = "SN-299-x";
        fg.Result = new ForegroundWindow("Code", "x.cs - sheepsonline - Visual Studio Code");

        mon.Sample(clock.UtcNow.AddMinutes(-1), humanActive: false).Should().BeEmpty();
    }

    [Fact]
    public void Claude_writes_older_than_the_last_tick_are_not_sampled()
    {
        var (mon, repos, fg, claude, git, clock) = Build();
        var b = NewRepoDir("training-manager");
        repos.Add(b);
        git.Branches[b] = "TM-30-y";
        claude.Snapshot[ClaudeProjectSlug.FromPath(b)] = clock.UtcNow.AddMinutes(-5);

        mon.Sample(clock.UtcNow.AddMinutes(-1), humanActive: false).Should().BeEmpty();
    }

    [Fact]
    public void A_repo_both_focused_and_Claude_active_appears_once()
    {
        var (mon, repos, fg, claude, git, clock) = Build();
        var a = NewRepoDir("sheepsonline");
        repos.Add(a);
        git.Branches[a] = "SN-299-x";
        fg.Result = new ForegroundWindow("Code", "x.cs - sheepsonline - Visual Studio Code");
        claude.Snapshot[ClaudeProjectSlug.FromPath(a)] = clock.UtcNow;

        mon.Sample(clock.UtcNow.AddMinutes(-1), humanActive: true).Should().ContainSingle();
    }

    [Fact]
    public void Claude_activity_for_an_untracked_repo_is_ignored()
    {
        var (mon, repos, fg, claude, git, clock) = Build();
        var a = NewRepoDir("sheepsonline");
        repos.Add(a);
        git.Branches[a] = "SN-299-x";
        claude.Snapshot[ClaudeProjectSlug.FromPath(@"C:\somewhere\else")] = clock.UtcNow;

        mon.Sample(clock.UtcNow.AddMinutes(-1), humanActive: false).Should().BeEmpty();
    }

    [Fact]
    public void A_branch_with_no_ticket_yields_a_null_ticket_key()
    {
        var (mon, repos, fg, claude, git, clock) = Build();
        var b = NewRepoDir("training-manager");
        repos.Add(b);
        git.Branches[b] = "main";
        claude.Snapshot[ClaudeProjectSlug.FromPath(b)] = clock.UtcNow;

        var sample = mon.Sample(clock.UtcNow.AddMinutes(-1), humanActive: false);

        sample.Should().ContainSingle().Which.TicketKey.Should().BeNull();
    }
}
```

Check `TrackedRepoRepository` for the actual add method name before running — if it is not `Add(string path)`, use whatever it exposes (e.g. `Add(path, sortOrder)`), and check `ForegroundWindow`'s real constructor shape in `IForegroundWindowProbe.cs`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter "FullyQualifiedName~RepoActivityMonitor"`
Expected: build error — `RepoActivityMonitor` does not exist.

- [ ] **Step 3: Write the monitor**

Create `src/TmTimeTracker/Services/RepoActivityMonitor.cs`:

```csharp
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using TmTimeTracker.Platform;

namespace TmTimeTracker.Services;

/// <summary>One repo that earned a minute this tick, with the ticket to bill it to.</summary>
public sealed record RepoActivity(string RepoPath, string? Branch, string? TicketKey);

public interface IRepoActivitySource
{
    /// <param name="lastTickUtc">Claude writes are fresh if newer than this.</param>
    /// <param name="humanActive">False when idle or locked; suppresses the human stream only.</param>
    IReadOnlyList<RepoActivity> Sample(DateTime lastTickUtc, bool humanActive);
}

/// <summary>
/// The credit set for one minute: the repo the user is in, plus every repo Claude Code touched
/// since the last tick. Two independent streams - hand-editing one project while an agent works
/// another pays both, and neither can take the other's minutes.
///
/// Claude streams deliberately ignore idle state and are never capped: crediting unattended agent
/// work is the whole point of the second stream.
/// </summary>
public sealed class RepoActivityMonitor : IRepoActivitySource
{
    private readonly TrackedRepoRepository _repos;
    private readonly ActiveRepoResolver _resolver;
    private readonly IClaudeCodeActivityProbe _claude;
    private readonly IGitBranchProbe _git;
    private readonly IClock _clock;
    private readonly ILogger<RepoActivityMonitor> _log;
    private readonly HashSet<string> _warnedMissing = new(StringComparer.OrdinalIgnoreCase);

    public RepoActivityMonitor(TrackedRepoRepository repos, ActiveRepoResolver resolver,
        IClaudeCodeActivityProbe claude, IGitBranchProbe git, IClock clock,
        ILogger<RepoActivityMonitor> log)
    {
        _repos = repos; _resolver = resolver; _claude = claude;
        _git = git; _clock = clock; _log = log;
    }

    public IReadOnlyList<RepoActivity> Sample(DateTime lastTickUtc, bool humanActive)
    {
        var byPath = new Dictionary<string, RepoActivity>(StringComparer.OrdinalIgnoreCase);

        // Human stream: at most one repo, and only while the user is actually at the desk.
        if (humanActive)
        {
            var human = _resolver.Resolve();
            if (human is not null && Directory.Exists(human.RepoPath))
                byPath[human.RepoPath] = new RepoActivity(human.RepoPath, human.Branch, human.TicketKey);
        }

        // Claude streams: any number, regardless of idle. The resolver already gave us the branch
        // for the human repo, so only repos it did not cover cost a git spawn.
        var snapshot = SafeSnapshot();
        if (snapshot.Count > 0)
        {
            foreach (var repo in _repos.GetAll())
            {
                if (byPath.ContainsKey(repo.Path)) continue;
                var slug = ClaudeProjectSlug.FromPath(repo.Path);
                if (!snapshot.TryGetValue(slug, out var mtime) || mtime <= lastTickUtc) continue;
                if (!Directory.Exists(repo.Path)) { WarnMissingOnce(repo.Path); continue; }

                var branch = _git.GetCurrentBranch(repo.Path);
                var ticket = branch is null ? null : TicketKeyExtractor.Extract(branch);
                byPath[repo.Path] = new RepoActivity(repo.Path, branch, ticket);
            }
        }

        return byPath.Values.ToList();
    }

    private IReadOnlyDictionary<string, DateTime> SafeSnapshot()
    {
        try { return _claude.Snapshot(); }
        catch (Exception ex)
        {
            if (_warnedMissing.Add("<claude-probe>"))
                _log.LogWarning(ex, "Claude activity probe failed; Claude streams idle this tick");
            return new Dictionary<string, DateTime>();
        }
    }

    private void WarnMissingOnce(string path)
    {
        if (_warnedMissing.Add(path))
            _log.LogWarning("Tracked repo path is not accessible: {Path}", path);
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter "FullyQualifiedName~RepoActivityMonitor"`
Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```bash
git add src/TmTimeTracker/Services/RepoActivityMonitor.cs tests/TmTimeTracker.Tests/Services/RepoActivityMonitorTests.cs
git commit -m "feat: add RepoActivityMonitor for concurrent per-repo credit sets"
```

---

### Task 3: Rewrite `TimeAggregator` for per-repo concurrent accrual

The core change, and the one that fixes the reported bug. This is also the largest task, because every existing test drives the aggregator through `BranchChanged` and must be rewritten against a fake source.

**Files:**
- Modify: `src/TmTimeTracker/Services/TimeAggregator.cs` (whole file)
- Modify: `tests/TmTimeTracker.Tests/Services/TimeAggregatorTests.cs` (whole file)

**Interfaces:**
- Consumes: `IRepoActivitySource` and `RepoActivity` from Task 2.
- Produces: `TimeAggregator` constructor becomes
  ```csharp
  public TimeAggregator(IEventBus bus, IRepoActivitySource source, TicketTimeRepository tickets,
      IClock clock, ILogger<TimeAggregator> log)
  ```
  `ProcessEvent(DomainEvent)` and `TickAsync()` keep their signatures. `ProcessEvent` now handles `ActivityChanged` only.

**Behaviour change to expect:** carry used to fire on the `BranchChanged` event, so old tests asserted carried totals with no tick in between. Carry now happens on the tick that first sees the new ticket, so those totals gain the tick's own minute (e.g. `Caps_the_carried_time` moves from 30 to 31). This is correct, not a regression — the minute being credited is a real minute of work on the new branch.

- [ ] **Step 1: Rewrite the test file**

Replace `tests/TmTimeTracker.Tests/Services/TimeAggregatorTests.cs` entirely:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using TmTimeTracker.Services;
using TmTimeTracker.Tests.Data;
using Xunit;

namespace TmTimeTracker.Tests.Services;

public class TimeAggregatorTests
{
    private const string RepoA = @"C:\projects\sheepsonline";
    private const string RepoB = @"C:\projects\training-manager";

    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 9, 3, 9, 0, 0, DateTimeKind.Utc);
        public DateTimeOffset LocalNow => new(UtcNow, TimeSpan.Zero);
    }

    /// <summary>
    /// Stands in for RepoActivityMonitor. Tests set the credit set directly; `humanActive` is
    /// recorded so the idle-gating tests can prove the aggregator passes it through.
    /// </summary>
    private sealed class FakeSource : IRepoActivitySource
    {
        public List<RepoActivity> Next = new();
        public bool? LastHumanActive;
        public bool SuppressWhenIdle = true;

        public IReadOnlyList<RepoActivity> Sample(DateTime lastTickUtc, bool humanActive)
        {
            LastHumanActive = humanActive;
            // The real monitor drops the human stream when idle; mirror that so aggregator tests
            // exercise the same shape. Claude-stream tests set SuppressWhenIdle = false.
            if (!humanActive && SuppressWhenIdle) return Array.Empty<RepoActivity>();
            return Next.ToList();
        }
    }

    private static (TimeAggregator agg, FakeSource src, TicketTimeRepository tickets) Build()
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        var tickets = new TicketTimeRepository(ds);
        var src = new FakeSource();
        var agg = new TimeAggregator(new EventBus(), src, tickets, new FakeClock(),
            NullLogger<TimeAggregator>.Instance);
        agg.ProcessEvent(new ActivityChanged(UserActivityState.Active, DateTime.UtcNow));
        return (agg, src, tickets);
    }

    private static RepoActivity On(string repo, string branch, string? ticket) =>
        new(repo, branch, ticket);

    private static int Minutes(TicketTimeRepository t, string key) =>
        t.GetAllOpen().SingleOrDefault(c => c.TicketKey == key)?.MinutesActive ?? 0;

    // ---- single-repo behaviour, preserved from the original suite -------------------------

    [Fact]
    public async Task Increments_when_active_and_has_ticket()
    {
        var (agg, src, tickets) = Build();
        src.Next = new() { On(RepoA, "TM-29-x", "TM-29") };

        await agg.TickAsync();

        var open = tickets.GetAllOpen().Should().ContainSingle().Subject;
        open.TicketKey.Should().Be("TM-29");
        open.MinutesActive.Should().Be(1);
    }

    [Fact]
    public async Task Does_not_increment_when_idle()
    {
        var (agg, src, tickets) = Build();
        agg.ProcessEvent(new ActivityChanged(UserActivityState.Idle, DateTime.UtcNow));
        src.Next = new() { On(RepoA, "TM-29-x", "TM-29") };

        await agg.TickAsync();

        src.LastHumanActive.Should().BeFalse("the aggregator must pass idle state to the source");
        tickets.GetAllOpen().Should().BeEmpty();
    }

    [Fact]
    public async Task Does_not_increment_when_no_ticket()
    {
        var (agg, src, tickets) = Build();
        src.Next = new() { On(RepoA, "main", null) };

        await agg.TickAsync();
        tickets.GetAllOpen().Should().BeEmpty();
    }

    [Fact]
    public async Task Switching_branch_opens_new_cycle_for_new_ticket()
    {
        var (agg, src, tickets) = Build();

        src.Next = new() { On(RepoA, "feature/TM-29", "TM-29") };
        await agg.TickAsync();

        src.Next = new() { On(RepoA, "feature/TM-30", "TM-30") };
        await agg.TickAsync();
        await agg.TickAsync();

        tickets.GetAllOpen().Should().HaveCount(2);
        Minutes(tickets, "TM-29").Should().Be(1);
        Minutes(tickets, "TM-30").Should().Be(2);
    }

    [Fact]
    public async Task Switching_back_resumes_existing_cycle()
    {
        var (agg, src, tickets) = Build();

        src.Next = new() { On(RepoA, "TM-29-x", "TM-29") };
        await agg.TickAsync();
        src.Next = new() { On(RepoA, "TM-30-x", "TM-30") };
        await agg.TickAsync();
        src.Next = new() { On(RepoA, "TM-29-x", "TM-29") };
        await agg.TickAsync();

        Minutes(tickets, "TM-29").Should().Be(2);
        Minutes(tickets, "TM-30").Should().Be(1);
    }

    [Fact]
    public async Task Carries_unattributed_time_onto_the_next_ticket_branch()
    {
        var (agg, src, tickets) = Build();
        src.Next = new() { On(RepoA, "main", null) };
        for (var i = 0; i < 3; i++) await agg.TickAsync();
        tickets.GetAllOpen().Should().BeEmpty("nothing is written until a ticket is known");

        src.Next = new() { On(RepoA, "TM-29-x", "TM-29") };
        await agg.TickAsync();

        Minutes(tickets, "TM-29").Should().Be(4, "3 carried from main plus 1 of its own");
    }

    [Fact]
    public async Task Does_not_move_time_between_two_ticket_branches()
    {
        var (agg, src, tickets) = Build();

        src.Next = new() { On(RepoA, "TM-101-a", "TM-101") };
        await agg.TickAsync();
        await agg.TickAsync();

        src.Next = new() { On(RepoA, "TM-202-b", "TM-202") };
        await agg.TickAsync();

        Minutes(tickets, "TM-101").Should().Be(2);
        Minutes(tickets, "TM-202").Should().Be(1);
    }

    [Fact]
    public async Task Caps_the_carried_time()
    {
        var (agg, src, tickets) = Build();
        src.Next = new() { On(RepoA, "main", null) };
        for (var i = 0; i < 45; i++) await agg.TickAsync();

        src.Next = new() { On(RepoA, "TM-29-x", "TM-29") };
        await agg.TickAsync();

        Minutes(tickets, "TM-29").Should().Be(31, "30 capped carry plus this tick's own minute");
    }

    [Fact]
    public async Task Carries_the_buffer_only_once()
    {
        var (agg, src, tickets) = Build();
        src.Next = new() { On(RepoA, "main", null) };
        for (var i = 0; i < 3; i++) await agg.TickAsync();

        src.Next = new() { On(RepoA, "TM-1-a", "TM-1") };
        await agg.TickAsync();
        src.Next = new() { On(RepoA, "TM-2-b", "TM-2") };
        await agg.TickAsync();

        Minutes(tickets, "TM-1").Should().Be(4, "3 carried plus its own minute");
        Minutes(tickets, "TM-2").Should().Be(1, "the buffer was already spent");
    }

    [Fact]
    public async Task Does_not_buffer_idle_time_on_main()
    {
        var (agg, src, tickets) = Build();
        agg.ProcessEvent(new ActivityChanged(UserActivityState.Idle, DateTime.UtcNow));
        src.Next = new() { On(RepoA, "main", null) };
        for (var i = 0; i < 5; i++) await agg.TickAsync();

        agg.ProcessEvent(new ActivityChanged(UserActivityState.Active, DateTime.UtcNow));
        src.Next = new() { On(RepoA, "TM-29-x", "TM-29") };
        await agg.TickAsync();

        Minutes(tickets, "TM-29").Should().Be(1, "idle minutes on main are not work");
    }

    // ---- concurrent behaviour, new ---------------------------------------------------------

    [Fact]
    public async Task Credits_two_repos_in_the_same_minute()
    {
        var (agg, src, tickets) = Build();
        src.Next = new() { On(RepoA, "SN-299-x", "SN-299"), On(RepoB, "TM-30-y", "TM-30") };

        await agg.TickAsync();
        await agg.TickAsync();

        Minutes(tickets, "SN-299").Should().Be(2);
        Minutes(tickets, "TM-30").Should().Be(2);
    }

    [Fact]
    public async Task Credits_a_ticket_once_even_if_two_repos_are_on_it()
    {
        var (agg, src, tickets) = Build();
        src.Next = new() { On(RepoA, "TM-30-x", "TM-30"), On(RepoB, "TM-30-y", "TM-30") };

        await agg.TickAsync();

        Minutes(tickets, "TM-30").Should().Be(1, "one cycle exists per key, so credit it once");
    }

    // The reported bug: minutes banked on one repo's main used to land on whatever ticket branch
    // was checked out next, in any repo.
    [Fact]
    public async Task Banked_time_never_carries_across_repos()
    {
        var (agg, src, tickets) = Build();
        src.Next = new() { On(RepoA, "main", null) };
        for (var i = 0; i < 10; i++) await agg.TickAsync();

        src.Next = new() { On(RepoB, "TM-30-y", "TM-30") };
        await agg.TickAsync();

        Minutes(tickets, "TM-30").Should().Be(1, "repo A's banked time is not repo B's work");
    }

    [Fact]
    public async Task Banked_time_carries_within_its_own_repo_while_another_repo_is_active()
    {
        var (agg, src, tickets) = Build();
        src.Next = new() { On(RepoA, "main", null), On(RepoB, "TM-30-y", "TM-30") };
        for (var i = 0; i < 5; i++) await agg.TickAsync();

        Minutes(tickets, "TM-30").Should().Be(5);

        src.Next = new() { On(RepoA, "SN-299-x", "SN-299"), On(RepoB, "TM-30-y", "TM-30") };
        await agg.TickAsync();

        Minutes(tickets, "SN-299").Should().Be(6, "5 banked in repo A plus its own minute");
        Minutes(tickets, "TM-30").Should().Be(6, "repo B was unaffected throughout");
    }

    [Fact]
    public async Task Caps_each_repos_buffer_independently()
    {
        var (agg, src, tickets) = Build();
        src.Next = new() { On(RepoA, "main", null), On(RepoB, "develop", null) };
        for (var i = 0; i < 45; i++) await agg.TickAsync();

        src.Next = new() { On(RepoA, "SN-299-x", "SN-299"), On(RepoB, "TM-30-y", "TM-30") };
        await agg.TickAsync();

        Minutes(tickets, "SN-299").Should().Be(31);
        Minutes(tickets, "TM-30").Should().Be(31);
    }

    // Claude streams are not gated on the user being at the desk.
    [Fact]
    public async Task Credits_a_Claude_repo_while_the_human_is_idle()
    {
        var (agg, src, tickets) = Build();
        agg.ProcessEvent(new ActivityChanged(UserActivityState.Idle, DateTime.UtcNow));
        src.SuppressWhenIdle = false;   // the real monitor still returns Claude repos when idle
        src.Next = new() { On(RepoB, "TM-30-y", "TM-30") };

        await agg.TickAsync();
        await agg.TickAsync();

        Minutes(tickets, "TM-30").Should().Be(2);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter "FullyQualifiedName~TimeAggregator"`
Expected: build error — `TimeAggregator` has no constructor taking `IRepoActivitySource`.

- [ ] **Step 3: Rewrite the aggregator**

Replace `src/TmTimeTracker/Services/TimeAggregator.cs`:

```csharp
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

    /// <summary>
    /// Per-repo attribution state. In memory on purpose: a restart should not resurrect hours of
    /// ambiguous time and attach it to whatever branch happens to be checked out next.
    /// </summary>
    private sealed class RepoState
    {
        public string? LastTicket;
        public int Banked;
    }

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
        // BranchChanged is a dashboard-log event now; attribution is pulled at tick time.
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
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter "FullyQualifiedName~TimeAggregator"`
Expected: PASS, 16 tests. `Banked_time_never_carries_across_repos` is the reported bug; confirm it is green.

- [ ] **Step 5: Commit**

```bash
git add src/TmTimeTracker/Services/TimeAggregator.cs tests/TmTimeTracker.Tests/Services/TimeAggregatorTests.cs
git commit -m "fix: credit repos concurrently and scope the carry buffer per repo"
```

---

### Task 4: Drop the Claude override from idle detection

`IsClaudeActiveForCurrentRepo` and the `claudeActive` parameter exist only to keep the single-slot model crediting unattended agent work. The Claude stream owns that now, and leaving the override in would make the human stream report "Active" when nobody is at the desk.

**Files:**
- Modify: `src/TmTimeTracker/Logic/IdleStateMachine.cs:20-32`
- Modify: `src/TmTimeTracker/Services/IdleMonitor.cs` (drop two dependencies and the helper)
- Test: `tests/TmTimeTracker.Tests/Logic/IdleStateMachineTests.cs:61-82`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `void IdleStateMachine.Observe(long idleSeconds, bool isLocked)` and
  ```csharp
  public IdleMonitor(IIdleProbe probe, IEventBus bus, IClock clock,
      ConfigRepository config, ILogger<IdleMonitor> log)
  ```

- [ ] **Step 1: Update the state-machine tests**

In `IdleStateMachineTests.cs`, delete `Claude_active_keeps_state_active_past_idle_threshold` and `Claude_active_does_not_override_lock`. Replace `Claude_inactive_with_idle_past_threshold_goes_idle` with:

```csharp
[Fact]
public void Idle_past_threshold_goes_idle()
{
    var sm = new IdleStateMachine(TimeSpan.FromSeconds(600));
    sm.Observe(idleSeconds: 700, isLocked: false);
    sm.Current.Should().Be(UserActivityState.Idle);
}
```

Keep the surrounding construction style the file already uses — if its other tests build the machine with a different threshold or helper, match them.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj --filter "FullyQualifiedName~IdleStateMachine"`
Expected: still PASS at this point (the parameter is optional), so this step is a compile check only. Proceed to Step 3 and rely on Step 4 for the real signal.

- [ ] **Step 3: Remove the override**

In `IdleStateMachine.cs`, change `Observe` to:

```csharp
public void Observe(long idleSeconds, bool isLocked)
{
    var next = isLocked || idleSeconds >= _threshold.TotalSeconds
        ? UserActivityState.Idle
        : UserActivityState.Active;

    if (next == Current) return;
    Current = next;
    OnTransition?.Invoke(next);
}
```

In `IdleMonitor.cs`: delete the `ClaudeActivityWindow` constant, the `_claudeProbe` and `_resolver` fields, their constructor parameters, the `IsClaudeActiveForCurrentRepo` method, and the `claudeActive` local. The observe call becomes `sm.Observe(idleSec, locked);`. Drop the now-unused `using TmTimeTracker.Platform;` / `using TmTimeTracker.Logic;` imports only if nothing else in the file needs them — `UserActivityState` lives in `TmTimeTracker.Logic`, so keep that one.

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj`
Expected: PASS. A compile error here means a `sm.Observe(..., claudeActive: ...)` call site was missed.

- [ ] **Step 5: Commit**

```bash
git add src/TmTimeTracker/Logic/IdleStateMachine.cs src/TmTimeTracker/Services/IdleMonitor.cs tests/TmTimeTracker.Tests/Logic/IdleStateMachineTests.cs
git commit -m "refactor: idle detection no longer overridden by Claude activity"
```

---

### Task 5: Wire it up — DI, `BranchChanged.RepoPath`, dashboard badge

Nothing constructs `RepoActivityMonitor` yet, so the app would fail to start. This task makes it run, and finishes the two cosmetic consumers.

**Files:**
- Modify: `src/TmTimeTracker/HostingExtensions.cs:44` (registrations)
- Modify: `src/TmTimeTracker/Services/DomainEvents.cs:8`
- Modify: `src/TmTimeTracker/Services/BranchWatcher.cs:41`
- Modify: `src/TmTimeTracker/UI/DashboardWindow.cs:419-438` (`UpdateClaudeBadge`)

**Interfaces:**
- Consumes: `RepoActivityMonitor`, `IRepoActivitySource` (Task 2); the new `TimeAggregator` constructor (Task 3).
- Produces: `BranchChanged(string? RepoPath, string? Branch, string? TicketKey, DateTime AtUtc)`.

- [ ] **Step 1: Add `RepoPath` to `BranchChanged` and publish it**

In `DomainEvents.cs`:

```csharp
public sealed record BranchChanged(string? RepoPath, string? Branch, string? TicketKey, DateTime AtUtc)
    : DomainEvent(AtUtc);
```

In `BranchWatcher.cs:41`:

```csharp
await _bus.PublishAsync(new BranchChanged(repoPath, branch, ticket, _clock.UtcNow), stoppingToken)
          .ConfigureAwait(false);
```

In `DashboardWindow.cs:377`, include the repo so the event log tells the two projects apart:

```csharp
BranchChanged b   => $"{System.IO.Path.GetFileName(b.RepoPath) ?? "?"}: {b.Branch ?? "(detached)"} → {b.TicketKey ?? "(no ticket)"}",
```

- [ ] **Step 2: Register the monitor and confirm the app composes**

In `HostingExtensions.AddTmTimeTrackerCore`, after `services.AddSingleton<ActiveRepoResolver>();`:

```csharp
services.AddSingleton<RepoActivityMonitor>();
services.AddSingleton<IRepoActivitySource>(sp => sp.GetRequiredService<RepoActivityMonitor>());
```

Run: `dotnet build TmTimeTracker.sln`
Expected: build succeeds. A missing-service error at runtime would not surface here, so Step 4 covers it.

- [ ] **Step 3: Make the Claude badge count all repos**

`UpdateClaudeBadge` currently reports only the resolved repo, which under two streams is misleading. Replace it in `DashboardWindow.cs`:

```csharp
private void UpdateClaudeBadge()
{
    var snap = _claudeProbe.Snapshot();
    var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(60);
    var active = _repos.GetAll()
        .Count(r => snap.TryGetValue(ClaudeProjectSlug.FromPath(r.Path), out var m) && m > cutoff);

    if (active == 0) _claudePill.Set("Claude idle", PillTone.Idle);
    else if (active == 1) _claudePill.Set("Claude active", PillTone.Claude);
    else _claudePill.Set($"Claude active ×{active}", PillTone.Claude);
}
```

This needs a `TrackedRepoRepository _repos` field on `DashboardWindow`. Add it as a constructor dependency alongside the existing ones and pass it at the construction site — grep for `new DashboardWindow(` to find it. If `_resolver` becomes unused after this change, remove that dependency too; if other methods still use it, leave it.

Note the badge keeps a 60-second display window. That is a UI freshness heuristic, unrelated to the aggregator's tick-based accounting — a badge needs a fixed window because it repaints on a UI timer, not on the minute tick.

- [ ] **Step 4: Build, test, and smoke-run**

```bash
dotnet build TmTimeTracker.sln
dotnet test tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj
```
Expected: build clean, all tests pass.

Then confirm the host actually composes — a missing DI registration only bites at startup:
```bash
dotnet run --project src/TmTimeTracker/TmTimeTracker.csproj -- --help
```
Expected: it starts and exits without an `InvalidOperationException: Unable to resolve service`. If the project has no `--help`, run it for a few seconds and confirm no DI exception in the log at `AppPaths`' log location.

- [ ] **Step 5: Commit**

```bash
git add src/TmTimeTracker/HostingExtensions.cs src/TmTimeTracker/Services/DomainEvents.cs src/TmTimeTracker/Services/BranchWatcher.cs src/TmTimeTracker/UI/DashboardWindow.cs
git commit -m "feat: wire up concurrent repo tracking and per-repo Claude badge"
```

---

## Verification

After Task 5, the whole suite must be green and these behaviours must hold:

| Scenario | Expected |
|---|---|
| VS Code on sheepsonline focused, Claude running in training-manager | Both tickets gain a minute per minute |
| Ten minutes on sheepsonline `main`, then checkout `SN-299-x` | SN-299 gains 11; TM tickets unaffected |
| Ten minutes on sheepsonline `main`, then work in training-manager on `TM-30-y` | TM-30 gains 1, **not** 11 |
| Away from the desk, Claude running in training-manager | TM-30 keeps accruing |
| Away from the desk, both editors open, nothing running | Nothing accrues |
