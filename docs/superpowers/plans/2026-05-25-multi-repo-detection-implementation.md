# Multi-Repo Detection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the single-`RepoPath` config with a user-managed list of tracked repos and detect the active one each tick via VS Code's foreground-window title (primary) with `.git/HEAD` mtime polling as fallback, per [2026-05-25-multi-repo-detection-design.md](../specs/2026-05-25-multi-repo-detection-design.md).

**Architecture:** A new pure-logic `ActiveRepoResolver` consumes a `TrackedRepoRepository` (new SQLite table), a `Win32ForegroundWindowProbe` (Win32 P/Invoke), and a `VsCodeWindowTitleParser` (pure regex). `BranchWatcher` is rewritten to call `Resolve()` every 2 seconds and emit existing `BranchChanged` events; everything downstream (TimeAggregator, JiraPollService, dashboard) is untouched. `RememberWatcher` becomes multi-path. The Settings UI replaces the single Repo textbox with a ListBox + Add/Remove. A one-shot startup migration seeds `tracked_repo` from the legacy `config.repo_path`.

**Tech Stack:** No new NuGet packages. Adds Win32 P/Invoke (`GetForegroundWindow`, `GetWindowThreadProcessId`, `GetWindowText`) alongside the existing `Win32IdleProbe` pattern.

---

## Phases at a Glance

| Phase | Theme | Output |
|-------|-------|--------|
| 1 | Schema + `TrackedRepoRepository` | Persistent repo list with TDD |
| 2 | `VsCodeWindowTitleParser` | Pure parser, 6 unit tests |
| 3 | `IForegroundWindowProbe` + Win32 impl | Probe + interface; manual smoke later |
| 4 | `ActiveRepoResolver` | Pure logic with fakes; 7 unit tests |
| 5 | `BranchWatcher` rewrite + `RememberWatcher` rewrite | Multi-repo runtime; existing tests still pass |
| 6 | Startup migration + Settings UI list editor | One-time DB migration + user-facing list |
| Final | Repack exe to Desktop | Updated `TmTimeTracker.exe` |

---

# Phase 1: Schema + TrackedRepoRepository

### Task 1.1: Extend Schema.sql

**Files:** `src/TmTimeTracker/Data/Schema.sql`

- [ ] **Step 1: Append the new table**

Add at the end of the file:

```sql
CREATE TABLE IF NOT EXISTS tracked_repo (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    path       TEXT NOT NULL UNIQUE COLLATE NOCASE,
    sort_order INTEGER NOT NULL DEFAULT 0
);
```

- [ ] **Step 2: Verify build**

Run: `dotnet build`
Expected: success.

---

### Task 1.2: TrackedRepoRepository (TDD)

**Files:**
- Create: `src/TmTimeTracker/Data/TrackedRepoRepository.cs`
- Create: `tests/TmTimeTracker.Tests/Data/TrackedRepoRepositoryTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/TmTimeTracker.Tests/Data/TrackedRepoRepositoryTests.cs`:

```csharp
using FluentAssertions;
using TmTimeTracker.Data;
using Xunit;

namespace TmTimeTracker.Tests.Data;

public class TrackedRepoRepositoryTests
{
    private static TrackedRepoRepository New()
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        return new TrackedRepoRepository(ds);
    }

    [Fact]
    public void Add_then_GetAll_returns_in_sort_order()
    {
        var repo = New();
        repo.Add(@"c:\projects\a");
        repo.Add(@"c:\projects\b");
        repo.GetAll().Select(r => r.Path).Should().Equal(@"c:\projects\a", @"c:\projects\b");
    }

    [Fact]
    public void Add_is_case_insensitive_dedupe()
    {
        var repo = New();
        repo.Add(@"C:\Projects\X");
        repo.Add(@"c:\projects\x");
        repo.GetAll().Should().ContainSingle();
    }

    [Fact]
    public void Remove_by_path_deletes_matching_row()
    {
        var repo = New();
        repo.Add(@"c:\projects\a");
        repo.Add(@"c:\projects\b");
        repo.Remove(@"c:\projects\a");
        repo.GetAll().Should().ContainSingle().Which.Path.Should().Be(@"c:\projects\b");
    }

    [Fact]
    public void Remove_is_case_insensitive()
    {
        var repo = New();
        repo.Add(@"C:\Projects\X");
        repo.Remove(@"c:\projects\x");
        repo.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void Sort_order_increments_per_insertion()
    {
        var repo = New();
        repo.Add(@"c:\projects\a");
        repo.Add(@"c:\projects\b");
        repo.Add(@"c:\projects\c");
        var rows = repo.GetAll();
        rows.Select(r => r.SortOrder).Should().BeInAscendingOrder();
    }

    [Fact]
    public void GetAll_returns_empty_list_initially()
    {
        New().GetAll().Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run and verify fail**

Run: `dotnet test --filter "FullyQualifiedName~TrackedRepoRepositoryTests" --nologo`
Expected: compile failure — `TrackedRepoRepository` does not exist.

- [ ] **Step 3: Implement**

Create `src/TmTimeTracker/Data/TrackedRepoRepository.cs`:

```csharp
using Dapper;

namespace TmTimeTracker.Data;

public sealed record TrackedRepo(long Id, string Path, int SortOrder);

public sealed class TrackedRepoRepository
{
    private readonly ISqliteConnectionFactory _factory;
    public TrackedRepoRepository(ISqliteConnectionFactory factory) => _factory = factory;

    public void Add(string path)
    {
        using var conn = _factory.Open();
        var nextOrder = conn.ExecuteScalar<int>(
            "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM tracked_repo");
        conn.Execute(
            "INSERT OR IGNORE INTO tracked_repo (path, sort_order) VALUES (@p, @o)",
            new { p = path, o = nextOrder });
    }

    public void Remove(string path)
    {
        using var conn = _factory.Open();
        conn.Execute("DELETE FROM tracked_repo WHERE path = @p COLLATE NOCASE",
            new { p = path });
    }

    public IReadOnlyList<TrackedRepo> GetAll()
    {
        using var conn = _factory.Open();
        return conn.Query<TrackedRepoRow>(
            "SELECT id, path, sort_order FROM tracked_repo ORDER BY sort_order, id")
            .Select(r => new TrackedRepo(r.id, r.path, r.sort_order))
            .ToList();
    }

    private sealed class TrackedRepoRow
    {
        public long id { get; set; }
        public string path { get; set; } = "";
        public int sort_order { get; set; }
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~TrackedRepoRepositoryTests" --nologo`
Expected: 6 pass.

- [ ] **Step 5: Register in DI**

Modify `src/TmTimeTracker/HostingExtensions.cs` inside `AddTmTimeTrackerCore`'s service registrations (add after `OAuthAppConfigRepository`):

```csharp
services.AddSingleton<TrackedRepoRepository>();
```

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(data): TrackedRepoRepository with case-insensitive dedupe"
```

---

# Phase 2: VsCodeWindowTitleParser

### Task 2.1: Parser (TDD)

**Files:**
- Create: `src/TmTimeTracker/Logic/VsCodeWindowTitleParser.cs`
- Create: `tests/TmTimeTracker.Tests/Logic/VsCodeWindowTitleParserTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/TmTimeTracker.Tests/Logic/VsCodeWindowTitleParserTests.cs`:

```csharp
using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class VsCodeWindowTitleParserTests
{
    [Theory]
    [InlineData("foo.cs - my-repo - Visual Studio Code", "my-repo")]
    [InlineData("Program.cs - tm-time-tracker - Visual Studio Code", "tm-time-tracker")]
    [InlineData("● foo.cs - my-repo - Visual Studio Code", "my-repo")]
    [InlineData("my-repo - Visual Studio Code", "my-repo")]
    [InlineData("foo.cs - sub/dir/leaf - Visual Studio Code", "sub/dir/leaf")]
    public void Extracts_workspace_folder(string title, string expected)
    {
        VsCodeWindowTitleParser.Parse(title).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Notepad")]
    [InlineData("foo.cs - my-repo - Cursor")]
    [InlineData("Visual Studio Code")]
    public void Returns_null_for_non_matching_titles(string title)
    {
        VsCodeWindowTitleParser.Parse(title).Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_null_input()
    {
        VsCodeWindowTitleParser.Parse(null!).Should().BeNull();
    }
}
```

- [ ] **Step 2: Run and verify fail**

Run: `dotnet test --filter "FullyQualifiedName~VsCodeWindowTitleParserTests" --nologo`
Expected: compile failure.

- [ ] **Step 3: Implement**

Create `src/TmTimeTracker/Logic/VsCodeWindowTitleParser.cs`:

```csharp
using System.Text.RegularExpressions;

namespace TmTimeTracker.Logic;

public static class VsCodeWindowTitleParser
{
    // Matches "[dirty marker ]<file> - <folder> - Visual Studio Code"
    // OR     "<folder> - Visual Studio Code" (no file open)
    private static readonly Regex WithFile = new(
        @"^(?:●\s*)?.+?\s-\s(?<folder>.+?)\s-\sVisual Studio Code$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FolderOnly = new(
        @"^(?<folder>.+?)\s-\sVisual Studio Code$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string? Parse(string? title)
    {
        if (string.IsNullOrEmpty(title)) return null;
        var withFile = WithFile.Match(title);
        if (withFile.Success) return withFile.Groups["folder"].Value;
        var folderOnly = FolderOnly.Match(title);
        return folderOnly.Success ? folderOnly.Groups["folder"].Value : null;
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~VsCodeWindowTitleParserTests" --nologo`
Expected: 9 pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(logic): VsCodeWindowTitleParser extracts workspace folder from VS Code title"
```

---

# Phase 3: ForegroundWindowProbe

### Task 3.1: Interface + Win32 impl

**Files:**
- Create: `src/TmTimeTracker/Platform/IForegroundWindowProbe.cs`
- Create: `src/TmTimeTracker/Platform/Win32ForegroundWindowProbe.cs`

- [ ] **Step 1: Interface**

Create `src/TmTimeTracker/Platform/IForegroundWindowProbe.cs`:

```csharp
namespace TmTimeTracker.Platform;

public sealed record ForegroundWindow(string ProcessName, string Title);

public interface IForegroundWindowProbe
{
    /// <summary>
    /// Returns the currently focused window's process name (without .exe) and title,
    /// or null when no foreground window is available or query fails.
    /// </summary>
    ForegroundWindow? Probe();
}
```

- [ ] **Step 2: Win32 implementation**

Create `src/TmTimeTracker/Platform/Win32ForegroundWindowProbe.cs`:

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TmTimeTracker.Platform;

public sealed class Win32ForegroundWindowProbe : IForegroundWindowProbe
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public ForegroundWindow? Probe()
    {
        try
        {
            var hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return null;

            var length = GetWindowTextLengthW(hWnd);
            if (length <= 0) return null;
            var sb = new StringBuilder(length + 1);
            GetWindowTextW(hWnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (string.IsNullOrEmpty(title)) return null;

            GetWindowThreadProcessId(hWnd, out var pid);
            string processName;
            try { processName = Process.GetProcessById((int)pid).ProcessName; }
            catch { processName = ""; }

            return new ForegroundWindow(processName, title);
        }
        catch
        {
            return null;
        }
    }
}
```

- [ ] **Step 3: Register in DI**

In `HostingExtensions.cs` `AddTmTimeTrackerCore`, add after `IGitBranchProbe` registration:

```csharp
services.AddSingleton<IForegroundWindowProbe, Win32ForegroundWindowProbe>();
```

- [ ] **Step 4: Verify build**

Run: `dotnet build`
Expected: success.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(platform): Win32ForegroundWindowProbe via user32.dll"
```

---

# Phase 4: ActiveRepoResolver (TDD)

### Task 4.1: Resolver (TDD with fakes)

**Files:**
- Create: `src/TmTimeTracker/Services/ActiveRepoResolver.cs`
- Create: `tests/TmTimeTracker.Tests/Services/ActiveRepoResolverTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/TmTimeTracker.Tests/Services/ActiveRepoResolverTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TmTimeTracker.Data;
using TmTimeTracker.Platform;
using TmTimeTracker.Services;
using TmTimeTracker.Tests.Data;
using Xunit;

namespace TmTimeTracker.Tests.Services;

public class ActiveRepoResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "tmtt-resolver-" + Guid.NewGuid().ToString("N"));

    public ActiveRepoResolverTests() => Directory.CreateDirectory(_root);

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

    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc);
        public DateTimeOffset LocalNow => new(UtcNow, TimeSpan.Zero);
    }

    private string MakeRepo(string name, DateTime headMtimeUtc)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        var headFile = Path.Combine(path, ".git", "HEAD");
        File.WriteAllText(headFile, "ref: refs/heads/main\n");
        File.SetLastWriteTimeUtc(headFile, headMtimeUtc);
        return path;
    }

    private (ActiveRepoResolver r, TrackedRepoRepository repos, FakeForeground fg, FakeBranchProbe git, FakeClock clk)
        Build(IEnumerable<string> repoPaths)
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        var repos = new TrackedRepoRepository(ds);
        foreach (var p in repoPaths) repos.Add(p);

        var fg = new FakeForeground();
        var git = new FakeBranchProbe();
        var clk = new FakeClock();
        var r = new ActiveRepoResolver(repos, fg, git, clk, NullLogger<ActiveRepoResolver>.Instance);
        return (r, repos, fg, git, clk);
    }

    [Fact]
    public void Returns_null_resolution_when_no_repos_tracked()
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        var r = new ActiveRepoResolver(new TrackedRepoRepository(ds),
            new FakeForeground(), new FakeBranchProbe(), new FakeClock(),
            NullLogger<ActiveRepoResolver>.Instance);
        r.Resolve().Should().BeNull();
    }

    [Fact]
    public void Window_title_match_wins_over_polling()
    {
        var clock = new FakeClock();
        var fresh = clock.UtcNow.AddMinutes(-1);
        var stale = clock.UtcNow.AddHours(-1);

        var a = MakeRepo("project-a", fresh);   // recent
        var b = MakeRepo("project-b", stale);   // older

        var (r, _, fg, git, _) = Build(new[] { a, b });
        fg.Value = new ForegroundWindow("Code", "Program.cs - project-b - Visual Studio Code");
        git.ByPath[b] = "TM-29-foo";

        var res = r.Resolve();
        res.Should().NotBeNull();
        res!.RepoPath.Should().Be(b);
        res.TicketKey.Should().Be("TM-29");
    }

    [Fact]
    public void Polling_used_when_no_IDE_match()
    {
        var clock = new FakeClock();
        var a = MakeRepo("a", clock.UtcNow.AddMinutes(-2));
        var b = MakeRepo("b", clock.UtcNow.AddMinutes(-1));   // newer

        var (r, _, fg, git, _) = Build(new[] { a, b });
        fg.Value = new ForegroundWindow("chrome", "Some browser tab - Google Chrome");
        git.ByPath[b] = "feature/TM-30";

        var res = r.Resolve();
        res!.RepoPath.Should().Be(b);
        res.TicketKey.Should().Be("TM-30");
    }

    [Fact]
    public void Sticky_when_no_signal_returns_previous_resolution()
    {
        var clock = new FakeClock();
        var a = MakeRepo("a", clock.UtcNow.AddHours(-1));    // old
        var (r, _, fg, git, _) = Build(new[] { a });
        fg.Value = new ForegroundWindow("Code", "Program.cs - a - Visual Studio Code");
        git.ByPath[a] = "TM-29";
        var first = r.Resolve();
        first!.RepoPath.Should().Be(a);

        // Now lose both signals
        fg.Value = null;
        var second = r.Resolve();
        second.Should().BeEquivalentTo(first);
    }

    [Fact]
    public void Window_title_match_by_basename_is_case_insensitive()
    {
        var clock = new FakeClock();
        var repo = MakeRepo("Project-X", clock.UtcNow);
        var (r, _, fg, git, _) = Build(new[] { repo });
        fg.Value = new ForegroundWindow("Code", "foo.cs - project-x - Visual Studio Code");
        git.ByPath[repo] = "main";

        r.Resolve()!.RepoPath.Should().Be(repo);
    }

    [Fact]
    public void Missing_repo_path_on_disk_does_not_throw()
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        var repos = new TrackedRepoRepository(ds);
        repos.Add(@"c:\nonexistent\ghost");

        var r = new ActiveRepoResolver(repos, new FakeForeground(), new FakeBranchProbe(),
            new FakeClock(), NullLogger<ActiveRepoResolver>.Instance);

        r.Resolve().Should().BeNull();
    }

    [Fact]
    public void Resolution_with_no_ticket_returns_null_ticket()
    {
        var clock = new FakeClock();
        var a = MakeRepo("a", clock.UtcNow);
        var (r, _, fg, git, _) = Build(new[] { a });
        fg.Value = new ForegroundWindow("Code", "foo.cs - a - Visual Studio Code");
        git.ByPath[a] = "main";

        var res = r.Resolve();
        res!.Branch.Should().Be("main");
        res.TicketKey.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run and verify fail**

Run: `dotnet test --filter "FullyQualifiedName~ActiveRepoResolverTests" --nologo`
Expected: compile failure.

- [ ] **Step 3: Implement**

Create `src/TmTimeTracker/Services/ActiveRepoResolver.cs`:

```csharp
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using TmTimeTracker.Platform;

namespace TmTimeTracker.Services;

public sealed record ActiveResolution(string RepoPath, string? Branch, string? TicketKey);

public sealed class ActiveRepoResolver
{
    private static readonly TimeSpan ActivityWindow = TimeSpan.FromMinutes(5);
    private static readonly string[] VsCodeProcessNames = { "Code", "Code.exe" };

    private readonly TrackedRepoRepository _repos;
    private readonly IForegroundWindowProbe _foreground;
    private readonly IGitBranchProbe _git;
    private readonly IClock _clock;
    private readonly ILogger<ActiveRepoResolver> _log;
    private readonly HashSet<string> _warnedMissing = new(StringComparer.OrdinalIgnoreCase);
    private ActiveResolution? _last;

    public ActiveRepoResolver(TrackedRepoRepository repos, IForegroundWindowProbe foreground,
        IGitBranchProbe git, IClock clock, ILogger<ActiveRepoResolver> log)
    {
        _repos = repos; _foreground = foreground; _git = git; _clock = clock; _log = log;
    }

    public ActiveResolution? Resolve()
    {
        var tracked = _repos.GetAll();
        if (tracked.Count == 0)
        {
            _last = null;
            return null;
        }

        // Tier 1: window title
        var fg = _foreground.Probe();
        if (fg is not null && IsVsCode(fg.ProcessName))
        {
            var folder = VsCodeWindowTitleParser.Parse(fg.Title);
            if (folder is not null)
            {
                var match = tracked.FirstOrDefault(r =>
                    string.Equals(Path.GetFileName(r.Path), folder, StringComparison.OrdinalIgnoreCase));
                if (match is not null && Directory.Exists(match.Path))
                {
                    _last = LookupBranch(match.Path);
                    return _last;
                }
            }
        }

        // Tier 2: recent activity polling
        var now = _clock.UtcNow;
        var fresh = tracked
            .Select(r => (r, mtime: TryReadHeadMtime(r.Path)))
            .Where(x => x.mtime.HasValue && (now - x.mtime!.Value) <= ActivityWindow)
            .OrderByDescending(x => x.mtime!.Value)
            .Select(x => x.r)
            .FirstOrDefault();
        if (fresh is not null)
        {
            _last = LookupBranch(fresh.Path);
            return _last;
        }

        // Tier 3: sticky — return previous
        return _last;
    }

    private static bool IsVsCode(string processName) =>
        VsCodeProcessNames.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));

    private ActiveResolution? LookupBranch(string repoPath)
    {
        if (!Directory.Exists(repoPath))
        {
            WarnMissingOnce(repoPath);
            return null;
        }
        var branch = _git.GetCurrentBranch(repoPath);
        var ticket = branch is null ? null : TicketKeyExtractor.Extract(branch);
        return new ActiveResolution(repoPath, branch, ticket);
    }

    private DateTime? TryReadHeadMtime(string repoPath)
    {
        try
        {
            var head = Path.Combine(repoPath, ".git", "HEAD");
            if (!File.Exists(head)) return null;
            return File.GetLastWriteTimeUtc(head);
        }
        catch
        {
            WarnMissingOnce(repoPath);
            return null;
        }
    }

    private void WarnMissingOnce(string path)
    {
        if (_warnedMissing.Add(path))
            _log.LogWarning("Tracked repo path is not accessible: {Path}", path);
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~ActiveRepoResolverTests" --nologo`
Expected: 7 pass.

- [ ] **Step 5: Register in DI**

In `HostingExtensions.cs` `AddActivityServices` (or `AddTmTimeTrackerCore` — pick one and keep it together with related services), add:

In `AddTmTimeTrackerCore`:
```csharp
services.AddSingleton<ActiveRepoResolver>();
```

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(services): ActiveRepoResolver picks active repo via VS Code window + git mtime"
```

---

# Phase 5: BranchWatcher + RememberWatcher rewrites

### Task 5.1: BranchWatcher uses ActiveRepoResolver

**Files:** modify `src/TmTimeTracker/Services/BranchWatcher.cs`

- [ ] **Step 1: Rewrite the watcher**

Replace the contents of `src/TmTimeTracker/Services/BranchWatcher.cs`:

```csharp
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
```

- [ ] **Step 2: Verify build**

Run: `dotnet build`
Expected: success.

- [ ] **Step 3: Verify existing tests still pass**

Run: `dotnet test --nologo`
Expected: all tests pass (existing TimeAggregator tests don't touch BranchWatcher; new resolver tests don't touch BranchWatcher).

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "refactor(services): BranchWatcher delegates active-repo decision to ActiveRepoResolver"
```

---

### Task 5.2: RememberWatcher watches multiple paths

**Files:** modify `src/TmTimeTracker/Services/RememberWatcher.cs`

- [ ] **Step 1: Replace contents**

Replace `src/TmTimeTracker/Services/RememberWatcher.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;

namespace TmTimeTracker.Services;

public sealed class RememberWatcher : BackgroundService
{
    private readonly IEventBus _bus;
    private readonly IClock _clock;
    private readonly TrackedRepoRepository _repos;
    private readonly RememberEntryRepository _entries;
    private readonly ILogger<RememberWatcher> _log;
    private readonly TimeSpan _refreshInterval = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, FileSystemWatcher> _watchers =
        new(StringComparer.OrdinalIgnoreCase);

    public RememberWatcher(IEventBus bus, IClock clock, TrackedRepoRepository repos,
        RememberEntryRepository entries, ILogger<RememberWatcher> log)
    {
        _bus = bus; _clock = clock; _repos = repos; _entries = entries; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RefreshWatchers();
        using var timer = new PeriodicTimer(_refreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                RefreshWatchers();
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var w in _watchers.Values) { try { w.Dispose(); } catch { } }
            _watchers.Clear();
        }
    }

    private void RefreshWatchers()
    {
        var wanted = _repos.GetAll()
            .Select(r => Path.Combine(r.Path, ".remember"))
            .Where(Directory.Exists)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Add new ones
        foreach (var path in wanted)
        {
            if (_watchers.ContainsKey(path)) continue;
            try
            {
                var fsw = new FileSystemWatcher(path)
                {
                    Filter = "*.md",
                    EnableRaisingEvents = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
                };
                fsw.Changed += (_, e) => SafeScanFile(e.FullPath);
                fsw.Created += (_, e) => SafeScanFile(e.FullPath);
                _watchers[path] = fsw;
                ScanAll(path);
                _log.LogInformation("Watching {Path}", path);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not watch {Path}", path);
            }
        }

        // Drop removed ones
        foreach (var existing in _watchers.Keys.ToList())
        {
            if (!wanted.Contains(existing))
            {
                try { _watchers[existing].Dispose(); } catch { }
                _watchers.Remove(existing);
                _log.LogInformation("Stopped watching {Path}", existing);
            }
        }
    }

    private void ScanAll(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
            SafeScanFile(file);
    }

    private void SafeScanFile(string fullPath)
    {
        try
        {
            var name = Path.GetFileName(fullPath);
            if (!(name.StartsWith("today-", StringComparison.OrdinalIgnoreCase) ||
                  name.Equals("now.md", StringComparison.OrdinalIgnoreCase))) return;

            string content;
            try { content = File.ReadAllText(fullPath); }
            catch (IOException) { return; }

            var entryDate = ExtractDateFromFilename(name) ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
            var entries = RememberEntryParser.Parse(content, name);
            _entries.UpsertMany(entries, entryDate);
            if (entries.Count > 0)
                _ = _bus.PublishAsync(new RememberEntriesObserved(entries, entryDate, _clock.UtcNow));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to scan {File}", fullPath);
        }
    }

    private static string? ExtractDateFromFilename(string name)
    {
        if (!name.StartsWith("today-", StringComparison.OrdinalIgnoreCase)) return null;
        var datePart = Path.GetFileNameWithoutExtension(name).Substring("today-".Length);
        return DateTime.TryParse(datePart, out _) ? datePart : null;
    }
}
```

- [ ] **Step 2: Verify build + tests**

Run: `dotnet build && dotnet test --nologo`
Expected: success, all tests pass.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "refactor(services): RememberWatcher monitors .remember/ across all tracked repos"
```

---

# Phase 6: Migration + Settings UI

### Task 6.1: Startup migration from legacy config.repo_path

**Files:** modify `src/TmTimeTracker/Program.cs`

- [ ] **Step 1: Add migration helper**

In `Program.cs`, add a new static helper near `TryMigrateSecretsJson`:

```csharp
static void TryMigrateTrackedRepos(IServiceProvider sp)
{
    var trackedRepo = sp.GetRequiredService<TrackedRepoRepository>();
    if (trackedRepo.GetAll().Count > 0) return;

    var cfgRepo = sp.GetRequiredService<ConfigRepository>();
    var cfg = cfgRepo.TryGet();
    if (cfg is null || string.IsNullOrWhiteSpace(cfg.RepoPath)) return;
    if (!Directory.Exists(cfg.RepoPath)) return;

    trackedRepo.Add(cfg.RepoPath);
}
```

- [ ] **Step 2: Call it during startup**

In `RunDaemon`, after `TryMigrateSecretsJson(sp);` and before reading `appConfig/oauthState/cfg`, add:

```csharp
TryMigrateTrackedRepos(sp);
```

- [ ] **Step 3: Adjust setup-needed check to require ≥1 tracked repo**

In `RunDaemon`, replace the existing line:

```csharp
var setupNeeded = appConfig is null || oauthState is null || cfg is null;
```

with:

```csharp
var hasTrackedRepo = sp.GetRequiredService<TrackedRepoRepository>().GetAll().Count > 0;
var setupNeeded = appConfig is null || oauthState is null || cfg is null || !hasTrackedRepo;
```

(`cfg` still gates on the legacy timing/status fields; the new tracked-repo gate is additional.)

- [ ] **Step 4: Verify build**

Run: `dotnet build`
Expected: success.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): startup migration from config.repo_path into tracked_repo"
```

---

### Task 6.2: PathsPage list editor

**Files:** modify `src/TmTimeTracker/UI/SetupPages/PathsPage.cs`

- [ ] **Step 1: Replace contents**

Replace `src/TmTimeTracker/UI/SetupPages/PathsPage.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using TmTimeTracker.Data;

namespace TmTimeTracker.UI.SetupPages;

public sealed class PathsPage : UserControl
{
    private readonly TrackedRepoRepository _repos;
    private readonly ListBox _repoList;
    private readonly NumericUpDown _idleMin;
    private readonly NumericUpDown _pollSec;
    private readonly TextBox _inProgress;
    private readonly TextBox _transitionTo;
    public event Action? StateChanged;

    public PathsPage(IServiceProvider sp)
    {
        _repos = sp.GetRequiredService<TrackedRepoRepository>();
        Dock = DockStyle.Fill;

        Controls.Add(new Label { Top = 10, Left = 10, AutoSize = true, Text = "Tracked repos:" });
        _repoList = new ListBox { Top = 30, Left = 10, Width = 470, Height = 110 };
        Controls.Add(_repoList);

        var add = new Button { Top = 30, Left = 490, Width = 70, Text = "Add…" };
        add.Click += (_, _) => AddRepo();
        Controls.Add(add);

        var remove = new Button { Top = 65, Left = 490, Width = 70, Text = "Remove" };
        remove.Click += (_, _) => RemoveSelected();
        Controls.Add(remove);

        Controls.Add(new Label { Top = 155, Left = 10, AutoSize = true, Text = "Idle threshold (minutes):" });
        _idleMin = new NumericUpDown { Top = 173, Left = 10, Width = 80, Minimum = 1, Maximum = 120, Value = 10 };
        Controls.Add(_idleMin);

        Controls.Add(new Label { Top = 155, Left = 200, AutoSize = true, Text = "Jira poll interval (seconds):" });
        _pollSec = new NumericUpDown { Top = 173, Left = 200, Width = 80, Minimum = 30, Maximum = 600, Value = 90 };
        Controls.Add(_pollSec);

        Controls.Add(new Label { Top = 210, Left = 10, AutoSize = true, Text = "'In Progress' status name:" });
        _inProgress = new TextBox { Top = 230, Left = 10, Width = 260, Text = "In Progress" };
        _inProgress.TextChanged += (_, _) => StateChanged?.Invoke();
        Controls.Add(_inProgress);

        Controls.Add(new Label { Top = 210, Left = 290, AutoSize = true, Text = "Transition target status:" });
        _transitionTo = new TextBox { Top = 230, Left = 290, Width = 270, Text = "Review" };
        _transitionTo.TextChanged += (_, _) => StateChanged?.Invoke();
        Controls.Add(_transitionTo);

        ReloadList();
        var existing = sp.GetRequiredService<ConfigRepository>().TryGet();
        if (existing is not null)
        {
            _idleMin.Value = Math.Clamp(existing.IdleThresholdSeconds / 60, 1, 120);
            _pollSec.Value = Math.Clamp(existing.JiraPollIntervalSeconds, 30, 600);
            _inProgress.Text = existing.InProgressStatusName;
            _transitionTo.Text = existing.TransitionToStatusName;
        }
    }

    public bool IsValid =>
        _repoList.Items.Count > 0 &&
        _inProgress.Text.Trim().Length > 0 &&
        _transitionTo.Text.Trim().Length > 0;

    public AppConfig BuildConfig() => new(
        IdleThresholdSeconds: (int)_idleMin.Value * 60,
        JiraPollIntervalSeconds: (int)_pollSec.Value,
        RepoPath: _repoList.Items.Count > 0 ? (string)_repoList.Items[0]! : "",   // legacy field; first repo
        RememberPath: "",                                                          // legacy field, unused
        InProgressStatusName: _inProgress.Text.Trim(),
        TransitionToStatusName: _transitionTo.Text.Trim());

    private void ReloadList()
    {
        _repoList.Items.Clear();
        foreach (var r in _repos.GetAll())
            _repoList.Items.Add(r.Path);
    }

    private void AddRepo()
    {
        using var fbd = new FolderBrowserDialog { Description = "Select a git repository folder" };
        if (fbd.ShowDialog() != DialogResult.OK) return;
        var path = fbd.SelectedPath;
        if (!Directory.Exists(Path.Combine(path, ".git")))
        {
            MessageBox.Show($"{path} does not contain a .git folder.", "TmTimeTracker",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _repos.Add(path);
        ReloadList();
        StateChanged?.Invoke();
    }

    private void RemoveSelected()
    {
        if (_repoList.SelectedItem is not string path) return;
        _repos.Remove(path);
        ReloadList();
        StateChanged?.Invoke();
    }
}
```

Note: the legacy `RepoPath` field in `AppConfig` is still populated (with the first tracked repo path) for back-compat — downstream code no longer uses it, but the constructor signature stays.

- [ ] **Step 2: Verify build + tests**

Run: `dotnet build && dotnet test --nologo`
Expected: success.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat(ui): PathsPage list editor for tracked repos; drop single-repo textbox"
```

---

# Final: Repack exe to Desktop

- [ ] **Step 1: Run full test suite**

Run: `dotnet test --nologo`
Expected: all tests pass.

- [ ] **Step 2: Publish self-contained exe**

```bash
dotnet publish src/TmTimeTracker/TmTimeTracker.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish
```

- [ ] **Step 3: Copy to Desktop**

```powershell
Copy-Item publish\TmTimeTracker.exe (Join-Path ([Environment]::GetFolderPath('Desktop')) 'TmTimeTracker.exe') -Force
```

- [ ] **Step 4: Verify timestamp**

```powershell
Get-Item (Join-Path ([Environment]::GetFolderPath('Desktop')) 'TmTimeTracker.exe') | Select-Object Name, LastWriteTime
```

---

# Self-Review

Cross-checked vs spec sections 4–11:

| Spec section | Plan task |
|--------------|-----------|
| 4.1 Detection pipeline | 4.1 (ActiveRepoResolver) + 5.1 (BranchWatcher rewrite) |
| 4.2 Component boundaries | Task ordering enforces it (resolver pure, probes thin) |
| 5.1 New table | 1.1 |
| 5.2 Migration | 6.1 |
| 6 component table | 1.2 / 2.1 / 3.1 / 4.1 / 5.1 / 5.2 / 6.2 |
| 7 algorithm | 4.1 (Resolve method) |
| 8 Settings UI | 6.2 |
| 9 error handling | distributed: 4.1 (missing-repo warnings, sticky, no-tracked-repo), 5.2 (FileSystemWatcher try/catch), 6.2 (.git check on add) |
| 10 testing | 1.2 (repo tests), 2.1 (parser tests), 4.1 (resolver tests) |
| 11 acceptance criteria | covered by phase outputs |

Note: spec §11 AC #1 (migration preserves single-repo behavior) is tested via the migration helper in 6.1 — manual smoke on a real `state.db` with `config.repo_path` set is the verification path.
