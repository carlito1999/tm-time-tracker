# Jira Time-Tracker Daemon — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows tray daemon (`TmTimeTracker.exe`) that observes Claude Code work activity, tracks per-minute time against Jira tickets extracted from git branch names, and posts worklogs on the In Progress → Review transition — backed by the design in [docs/superpowers/specs/2026-05-25-jira-time-tracker-daemon-design.md](../specs/2026-05-25-jira-time-tracker-daemon-design.md).

**Architecture:** Single .NET 8 WinForms-host exe using Generic Host + BackgroundServices + WinForms `NotifyIcon`. Local SQLite stores time, remember entries, and DPAPI-encrypted OAuth tokens. All components communicate over an in-process channel-based `EventBus`. Pure logic (parsers, state machines, aggregators) is isolated from platform concerns (Win32, registry, DPAPI, HTTP) so it can be TDD'd against `:memory:` SQLite and mocked clocks.

**Tech Stack:**
- **Runtime:** .NET 8 / C# 12, `net8.0-windows`, single-file self-contained publish for win-x64
- **Hosting:** `Microsoft.Extensions.Hosting` (Generic Host) + `Microsoft.Extensions.DependencyInjection`
- **UI:** WinForms (`UseWindowsForms=true`, `OutputType=WinExe`)
- **Data:** `Microsoft.Data.Sqlite` + `Dapper`
- **HTTP:** `IHttpClientFactory` + `System.Net.Http.Json`
- **Logging:** `Serilog` + `Serilog.Sinks.File` + `Serilog.Extensions.Hosting`
- **Crypto:** `System.Security.Cryptography.ProtectedData` (DPAPI)
- **Tests:** `xunit` + `xunit.runner.visualstudio` + `FluentAssertions` + `Moq` + `WireMock.Net`

**Solution layout (final state):**
```
TmTimeTracker.sln
├── .gitignore
├── secrets.example.json
├── src/TmTimeTracker/
│   ├── TmTimeTracker.csproj
│   ├── Program.cs
│   ├── HostingExtensions.cs
│   ├── Configuration/{AppSecrets,AppPaths}.cs
│   ├── Data/{DatabaseInitializer,SqliteConnectionFactory,TicketTimeRepository,
│   │        RememberEntryRepository,ConfigRepository,OAuthStateRepository,
│   │        MinuteSampleRepository}.cs
│   ├── Logic/{TicketKeyExtractor,RememberEntryParser,IdleStateMachine,
│   │         WorklogDescriptionBuilder}.cs
│   ├── Platform/{Win32IdleProbe,DpapiTokenProtector,AutoStartRegistrar,
│   │            SingleInstanceGuard,GitBranchProbe}.cs
│   ├── Services/{EventBus,IdleMonitor,BranchWatcher,RememberWatcher,
│   │            TimeAggregator,JiraPollService,OAuthCoordinator,
│   │            MaintenanceService,SystemClock}.cs
│   ├── Jira/{JiraOAuthClient,JiraApiClient,JiraDtos,LocalCallbackListener}.cs
│   └── UI/{TrayIconHost,WorklogEditForm,BalloonNotifier}.cs
└── tests/TmTimeTracker.Tests/
    ├── TmTimeTracker.Tests.csproj
    ├── Logic/...Tests.cs
    ├── Data/...Tests.cs
    ├── Services/...Tests.cs
    ├── Jira/...Tests.cs
    └── Integration/...Tests.cs
```

---

## Phases at a Glance

| Phase | Theme | Tasks | Output |
|-------|-------|-------|--------|
| 1 | Foundation: solution, schema, pure logic | 1.1–1.9 | Class library + 30+ unit tests passing |
| 2 | Activity monitoring | 2.1–2.8 | Console runner streams minute-by-minute Active/Idle/Branch/Ticket |
| 3 | Jira REST + OAuth | 3.1–3.10 | CLI command posts test worklog after OAuth consent |
| 4 | Tray UI, edit form, auto-start | 4.1–4.8 | Full daemon: tray balloon → form → POST → Jira |
| 5 | CI + packaging + smoke | 5.1–5.5 | GH Actions green; single-file exe artifact; smoke list checked |

Each phase ends with a green test run and a commit. Stop between phases for review.

---

# Phase 0: Prerequisites (one-time)

### Task 0.1: Verify .NET 8 SDK + tools

**Files:** none

- [ ] **Step 1: Verify SDK**

Run: `dotnet --list-sdks`
Expected: at least one `8.0.x` line.

If missing: install .NET 8 SDK from `https://dotnet.microsoft.com/download/dotnet/8.0` (x64 installer). Re-open shell.

- [ ] **Step 2: Verify git**

Run: `git --version`
Expected: any 2.x version.

- [ ] **Step 3: Confirm working directory**

Run: `pwd`
Expected: `c:\projects\tm-time-tracker` (or shell equivalent).

---

# Phase 1: Foundation

Goal of phase: solution + projects + SQLite schema + pure-logic units, all TDD'd.

### Task 1.1: Create solution and projects

**Files:**
- Create: `TmTimeTracker.sln`
- Create: `src/TmTimeTracker/TmTimeTracker.csproj`
- Create: `tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj`
- Create: `.gitignore`

- [ ] **Step 1: Create folder layout**

Run:
```powershell
New-Item -ItemType Directory -Path src\TmTimeTracker, tests\TmTimeTracker.Tests -Force | Out-Null
```

- [ ] **Step 2: Create solution**

Run:
```powershell
dotnet new sln -n TmTimeTracker
```

Expected: `TmTimeTracker.sln` created at root.

- [ ] **Step 3: Create main project (WinForms host)**

Run:
```powershell
dotnet new winforms -n TmTimeTracker -o src\TmTimeTracker --framework net8.0-windows
```

Expected: project created with `Form1.cs`, `Program.cs`, etc. We will overwrite/delete Form1 later.

- [ ] **Step 4: Create xunit test project**

Run:
```powershell
dotnet new xunit -n TmTimeTracker.Tests -o tests\TmTimeTracker.Tests --framework net8.0
```

Note: tests target plain `net8.0`, not `net8.0-windows`. Pure-logic and data tests don't need WinForms; the few Win32/WinForms tests live in `[Trait("Category","ManualSmoke")]` and aren't run in CI.

- [ ] **Step 5: Wire projects to solution**

Run:
```powershell
dotnet sln add src\TmTimeTracker\TmTimeTracker.csproj
dotnet sln add tests\TmTimeTracker.Tests\TmTimeTracker.Tests.csproj
dotnet add tests\TmTimeTracker.Tests\TmTimeTracker.Tests.csproj reference src\TmTimeTracker\TmTimeTracker.csproj
```

- [ ] **Step 6: Verify build**

Run: `dotnet build`
Expected: `Build succeeded.` Zero errors.

- [ ] **Step 7: Verify tests run**

Run: `dotnet test`
Expected: `Passed!  - Failed: 0, Passed: 0, Skipped: 0` (no tests yet, but framework runs).

- [ ] **Step 8: Write .gitignore**

Create `.gitignore` with content:
```gitignore
# Build output
bin/
obj/
*.user
*.suo
.vs/

# Publish output
publish/
*.exe
*.dll
*.pdb

# Local state
secrets.json
*.db
*.db-shm
*.db-wal
local.settings.json

# IDE
.idea/
.vscode/

# Test output
TestResults/
coverage.*
*.trx
```

- [ ] **Step 9: Commit**

```powershell
git add .
git commit -m "feat: scaffold .NET 8 solution with WinForms host and xunit test project"
```

---

### Task 1.2: Add NuGet dependencies

**Files:**
- Modify: `src/TmTimeTracker/TmTimeTracker.csproj`
- Modify: `tests/TmTimeTracker.Tests/TmTimeTracker.Tests.csproj`

- [ ] **Step 1: Add packages to main project**

Run:
```powershell
$proj = 'src\TmTimeTracker\TmTimeTracker.csproj'
dotnet add $proj package Microsoft.Extensions.Hosting --version 8.0.0
dotnet add $proj package Microsoft.Extensions.Http --version 8.0.0
dotnet add $proj package Microsoft.Data.Sqlite --version 8.0.0
dotnet add $proj package Dapper --version 2.1.35
dotnet add $proj package Serilog.Extensions.Hosting --version 8.0.0
dotnet add $proj package Serilog.Sinks.File --version 5.0.0
dotnet add $proj package Serilog.Sinks.Console --version 5.0.1
dotnet add $proj package System.Security.Cryptography.ProtectedData --version 8.0.0
```

- [ ] **Step 2: Add packages to test project**

Run:
```powershell
$tproj = 'tests\TmTimeTracker.Tests\TmTimeTracker.Tests.csproj'
dotnet add $tproj package FluentAssertions --version 6.12.0
dotnet add $tproj package Moq --version 4.20.70
dotnet add $tproj package WireMock.Net --version 1.5.46
dotnet add $tproj package Microsoft.Data.Sqlite --version 8.0.0
dotnet add $tproj package Dapper --version 2.1.35
```

- [ ] **Step 3: Verify build**

Run: `dotnet build`
Expected: build succeeds. NuGet may take 30s on first restore.

- [ ] **Step 4: Commit**

```powershell
git add src tests
git commit -m "build: add runtime and test NuGet dependencies"
```

---

### Task 1.3: TicketKeyExtractor (pure logic, TDD)

Extracts the first `TM-NNN` token from a branch name. Used by `BranchWatcher` and tests.

**Files:**
- Create: `src/TmTimeTracker/Logic/TicketKeyExtractor.cs`
- Create: `tests/TmTimeTracker.Tests/Logic/TicketKeyExtractorTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/TmTimeTracker.Tests/Logic/TicketKeyExtractorTests.cs`:

```csharp
using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class TicketKeyExtractorTests
{
    [Theory]
    [InlineData("TM-29-1808-add-worklog", "TM-29")]
    [InlineData("feature/TM-31", "TM-31")]
    [InlineData("bugfix/TM-7-fix-typo", "TM-7")]
    [InlineData("TM-100", "TM-100")]
    [InlineData("user/lefteris/TM-42-poc", "TM-42")]
    public void Extracts_first_TM_token(string branch, string expected)
    {
        TicketKeyExtractor.Extract(branch).Should().Be(expected);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("master")]
    [InlineData("feature/no-ticket")]
    [InlineData("")]
    [InlineData("TM-")]
    [InlineData("XX-29")]
    public void Returns_null_when_no_match(string branch)
    {
        TicketKeyExtractor.Extract(branch).Should().BeNull();
    }

    [Fact]
    public void Returns_first_match_when_multiple_present()
    {
        TicketKeyExtractor.Extract("TM-29-merge-from-TM-30").Should().Be("TM-29");
    }

    [Fact]
    public void Null_input_returns_null()
    {
        TicketKeyExtractor.Extract(null!).Should().BeNull();
    }
}
```

- [ ] **Step 2: Run and verify fail**

Run: `dotnet test --filter "FullyQualifiedName~TicketKeyExtractorTests"`
Expected: compile error — `TicketKeyExtractor` does not exist.

- [ ] **Step 3: Implement**

Create `src/TmTimeTracker/Logic/TicketKeyExtractor.cs`:

```csharp
using System.Text.RegularExpressions;

namespace TmTimeTracker.Logic;

public static class TicketKeyExtractor
{
    private static readonly Regex Pattern = new(@"\bTM-\d+\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string? Extract(string? branchName)
    {
        if (string.IsNullOrEmpty(branchName)) return null;
        var match = Pattern.Match(branchName);
        return match.Success ? match.Value : null;
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~TicketKeyExtractorTests"`
Expected: all 11 tests pass.

- [ ] **Step 5: Delete WinForms-template cruft**

Remove the auto-generated WinForms template files that won't survive into the final shape:
- Delete `src/TmTimeTracker/Form1.cs`
- Delete `src/TmTimeTracker/Form1.Designer.cs`
- Delete `src/TmTimeTracker/Form1.resx`

Then replace `src/TmTimeTracker/Program.cs` with a minimal placeholder so the project still builds:

```csharp
namespace TmTimeTracker;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Will be replaced by Generic Host wiring in Task 4.x.
        Console.WriteLine("TmTimeTracker scaffold OK");
    }
}
```

- [ ] **Step 6: Verify build still passes**

Run: `dotnet build`
Expected: build succeeds.

- [ ] **Step 7: Commit**

```powershell
git add src tests
git commit -m "feat(logic): extract TM-NNN ticket key from git branch names"
```

---

### Task 1.4: RememberEntryParser (pure logic, TDD)

Parses `.remember/today-YYYY-MM-DD.md` and `now.md` files into structured `RememberEntry` records.

**Files:**
- Create: `src/TmTimeTracker/Logic/RememberEntry.cs`
- Create: `src/TmTimeTracker/Logic/RememberEntryParser.cs`
- Create: `tests/TmTimeTracker.Tests/Logic/RememberEntryParserTests.cs`

`.remember/` files in this codebase use the structure: `## HH:MM | <context tag>` headers, followed by a body until the next header or EOF. The `<context tag>` is free-form; we treat it as a string and let `TicketKeyExtractor` decide if it carries a ticket.

- [ ] **Step 1: Write failing tests**

Create `tests/TmTimeTracker.Tests/Logic/RememberEntryParserTests.cs`:

```csharp
using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class RememberEntryParserTests
{
    [Fact]
    public void Parses_single_entry_with_ticket_tag()
    {
        var md = "## 07:20 | TM-29-1808-add-worklog\n" +
                 "Started on the worklog endpoint.\nGot the auth working.\n";

        var entries = RememberEntryParser.Parse(md, "today-2026-05-25.md");

        entries.Should().HaveCount(1);
        var e = entries[0];
        e.TimeOfDay.Should().Be("07:20");
        e.ContextTag.Should().Be("TM-29-1808-add-worklog");
        e.TicketKey.Should().Be("TM-29");
        e.Body.Should().Be("Started on the worklog endpoint.\nGot the auth working.");
        e.SourceFile.Should().Be("today-2026-05-25.md");
    }

    [Fact]
    public void Parses_multiple_entries()
    {
        var md =
            "## 09:00 | TM-29\nbody one\n" +
            "## 10:15 | TM-30-something\nbody two line A\nbody two line B\n";

        var entries = RememberEntryParser.Parse(md, "today-2026-05-25.md");

        entries.Should().HaveCount(2);
        entries[0].TicketKey.Should().Be("TM-29");
        entries[0].Body.Should().Be("body one");
        entries[1].TicketKey.Should().Be("TM-30");
        entries[1].Body.Should().Be("body two line A\nbody two line B");
    }

    [Fact]
    public void Skips_entry_without_ticket_in_tag()
    {
        var md = "## 09:00 | random meeting\nnotes\n";
        RememberEntryParser.Parse(md, "x.md").Should().BeEmpty();
    }

    [Fact]
    public void Header_without_pipe_is_ignored()
    {
        var md = "## not an entry header\nbody\n## 09:00 | TM-29\nreal body\n";
        var entries = RememberEntryParser.Parse(md, "x.md");
        entries.Should().HaveCount(1);
        entries[0].Body.Should().Be("real body");
    }

    [Fact]
    public void Empty_body_is_allowed()
    {
        var md = "## 09:00 | TM-29\n";
        var entries = RememberEntryParser.Parse(md, "x.md");
        entries.Should().HaveCount(1);
        entries[0].Body.Should().BeEmpty();
    }

    [Fact]
    public void Trims_trailing_blank_lines_in_body()
    {
        var md = "## 09:00 | TM-29\nbody\n\n\n";
        RememberEntryParser.Parse(md, "x.md")[0].Body.Should().Be("body");
    }

    [Fact]
    public void Empty_or_null_input_returns_empty()
    {
        RememberEntryParser.Parse("", "x.md").Should().BeEmpty();
        RememberEntryParser.Parse(null!, "x.md").Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run and verify fail**

Run: `dotnet test --filter "FullyQualifiedName~RememberEntryParserTests"`
Expected: compile errors — types don't exist yet.

- [ ] **Step 3: Implement record type**

Create `src/TmTimeTracker/Logic/RememberEntry.cs`:

```csharp
namespace TmTimeTracker.Logic;

public sealed record RememberEntry(
    string TimeOfDay,    // "HH:MM"
    string ContextTag,   // raw tag after the pipe
    string? TicketKey,   // extracted via TicketKeyExtractor; nullable here
    string Body,
    string SourceFile);
```

- [ ] **Step 4: Implement parser**

Create `src/TmTimeTracker/Logic/RememberEntryParser.cs`:

```csharp
using System.Text;
using System.Text.RegularExpressions;

namespace TmTimeTracker.Logic;

public static class RememberEntryParser
{
    private static readonly Regex HeaderPattern = new(
        @"^##\s+(?<time>\d{1,2}:\d{2})\s*\|\s*(?<tag>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<RememberEntry> Parse(string? content, string sourceFile)
    {
        if (string.IsNullOrEmpty(content)) return Array.Empty<RememberEntry>();

        var lines = content.Split('\n');
        var result = new List<RememberEntry>();
        Match? activeHeader = null;
        var bodyBuilder = new StringBuilder();

        void Flush()
        {
            if (activeHeader is null) return;
            var tag = activeHeader.Groups["tag"].Value;
            var ticket = TicketKeyExtractor.Extract(tag);
            if (ticket is not null)
            {
                var body = bodyBuilder.ToString().TrimEnd('\n', '\r');
                result.Add(new RememberEntry(
                    TimeOfDay: activeHeader.Groups["time"].Value,
                    ContextTag: tag,
                    TicketKey: ticket,
                    Body: body,
                    SourceFile: sourceFile));
            }
            bodyBuilder.Clear();
            activeHeader = null;
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var headerMatch = HeaderPattern.Match(line);
            if (headerMatch.Success)
            {
                Flush();
                activeHeader = headerMatch;
            }
            else if (activeHeader is not null)
            {
                if (bodyBuilder.Length > 0) bodyBuilder.Append('\n');
                bodyBuilder.Append(line);
            }
        }
        Flush();

        return result;
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~RememberEntryParserTests"`
Expected: all 7 tests pass.

- [ ] **Step 6: Commit**

```powershell
git add src tests
git commit -m "feat(logic): parse .remember/ markdown entries into structured records"
```

---

### Task 1.5: IdleStateMachine (pure logic, TDD)

State machine that takes "last input was N seconds ago" + "is screen locked" + a threshold and emits Active/Idle transitions. **No** Win32 here — that's a separate task.

**Files:**
- Create: `src/TmTimeTracker/Logic/IdleStateMachine.cs`
- Create: `src/TmTimeTracker/Logic/UserActivityState.cs`
- Create: `tests/TmTimeTracker.Tests/Logic/IdleStateMachineTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/TmTimeTracker.Tests/Logic/IdleStateMachineTests.cs`:

```csharp
using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class IdleStateMachineTests
{
    private static IdleStateMachine New(int thresholdSeconds = 600) =>
        new(TimeSpan.FromSeconds(thresholdSeconds));

    [Fact]
    public void Starts_in_active_state()
    {
        New().Current.Should().Be(UserActivityState.Active);
    }

    [Fact]
    public void Stays_active_below_threshold()
    {
        var sm = New(600);
        sm.Observe(idleSeconds: 100, isLocked: false);
        sm.Current.Should().Be(UserActivityState.Active);
    }

    [Fact]
    public void Becomes_idle_when_threshold_exceeded()
    {
        var sm = New(600);
        sm.Observe(idleSeconds: 700, isLocked: false);
        sm.Current.Should().Be(UserActivityState.Idle);
    }

    [Fact]
    public void Lock_forces_immediate_idle_regardless_of_input_age()
    {
        var sm = New(600);
        sm.Observe(idleSeconds: 5, isLocked: true);
        sm.Current.Should().Be(UserActivityState.Idle);
    }

    [Fact]
    public void Returns_to_active_when_input_resumes()
    {
        var sm = New(600);
        sm.Observe(700, isLocked: false);
        sm.Observe(idleSeconds: 2, isLocked: false);
        sm.Current.Should().Be(UserActivityState.Active);
    }

    [Fact]
    public void Lock_release_alone_does_not_unlock_until_input()
    {
        var sm = New(600);
        sm.Observe(5, isLocked: true);
        sm.Observe(idleSeconds: 700, isLocked: false);
        sm.Current.Should().Be(UserActivityState.Idle);
    }

    [Fact]
    public void Emits_transition_on_change_only()
    {
        var sm = New(600);
        var transitions = new List<UserActivityState>();
        sm.OnTransition += s => transitions.Add(s);

        sm.Observe(100, false);          // Active (no change)
        sm.Observe(700, false);          // -> Idle
        sm.Observe(800, false);          // Idle (no change)
        sm.Observe(2, false);            // -> Active

        transitions.Should().Equal(UserActivityState.Idle, UserActivityState.Active);
    }
}
```

- [ ] **Step 2: Run and verify fail**

Run: `dotnet test --filter "FullyQualifiedName~IdleStateMachineTests"`
Expected: compile errors.

- [ ] **Step 3: Implement**

Create `src/TmTimeTracker/Logic/UserActivityState.cs`:

```csharp
namespace TmTimeTracker.Logic;

public enum UserActivityState { Active, Idle }
```

Create `src/TmTimeTracker/Logic/IdleStateMachine.cs`:

```csharp
namespace TmTimeTracker.Logic;

public sealed class IdleStateMachine
{
    private readonly TimeSpan _threshold;

    public IdleStateMachine(TimeSpan threshold)
    {
        if (threshold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        _threshold = threshold;
    }

    public UserActivityState Current { get; private set; } = UserActivityState.Active;

    public event Action<UserActivityState>? OnTransition;

    public void Observe(long idleSeconds, bool isLocked)
    {
        var next = (isLocked || idleSeconds >= _threshold.TotalSeconds)
            ? UserActivityState.Idle
            : UserActivityState.Active;

        if (next == Current) return;
        Current = next;
        OnTransition?.Invoke(next);
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~IdleStateMachineTests"`
Expected: all 7 tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src tests
git commit -m "feat(logic): pure idle-state-machine with lock + threshold inputs"
```

---

### Task 1.6: SQLite schema + connection factory + initializer

**Files:**
- Create: `src/TmTimeTracker/Data/SqliteConnectionFactory.cs`
- Create: `src/TmTimeTracker/Data/DatabaseInitializer.cs`
- Create: `src/TmTimeTracker/Data/Schema.sql` (embedded)
- Create: `tests/TmTimeTracker.Tests/Data/DatabaseInitializerTests.cs`

- [ ] **Step 1: Write the schema SQL**

Create `src/TmTimeTracker/Data/Schema.sql`:

```sql
CREATE TABLE IF NOT EXISTS ticket_time (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    ticket_key        TEXT NOT NULL,
    cycle_started     TEXT NOT NULL,
    minutes_active    INTEGER NOT NULL DEFAULT 0,
    last_seen_status  TEXT,
    last_polled       TEXT,
    submitted_at      TEXT,
    worklog_id        TEXT,
    submitted_minutes INTEGER,
    UNIQUE(ticket_key, cycle_started)
);

CREATE INDEX IF NOT EXISTS idx_ticket_unsubmitted
    ON ticket_time(ticket_key) WHERE submitted_at IS NULL;

CREATE TABLE IF NOT EXISTS remember_entry (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    ticket_key          TEXT NOT NULL,
    timestamp_local     TEXT NOT NULL,
    entry_date          TEXT NOT NULL,
    body                TEXT NOT NULL,
    source_file         TEXT NOT NULL,
    consumed_in_worklog INTEGER NULL,
    UNIQUE(source_file, entry_date, timestamp_local, ticket_key)
);

CREATE TABLE IF NOT EXISTS minute_sample (
    sampled_at      TEXT PRIMARY KEY,
    ticket_key      TEXT,
    is_idle         INTEGER NOT NULL,
    git_branch      TEXT,
    claude_running  INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS oauth_state (
    id                  INTEGER PRIMARY KEY CHECK(id = 1),
    cloud_id            TEXT NOT NULL,
    access_token_dpapi  BLOB NOT NULL,
    refresh_token_dpapi BLOB NOT NULL,
    access_expires_at   TEXT NOT NULL,
    scope               TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS config (
    id                          INTEGER PRIMARY KEY CHECK(id = 1),
    idle_threshold_seconds      INTEGER NOT NULL DEFAULT 600,
    jira_poll_interval_seconds  INTEGER NOT NULL DEFAULT 90,
    repo_path                   TEXT NOT NULL,
    remember_path               TEXT NOT NULL,
    in_progress_status_name     TEXT NOT NULL DEFAULT 'In Progress',
    transition_to_status_name   TEXT NOT NULL DEFAULT 'Review'
);
```

- [ ] **Step 2: Mark schema as embedded resource**

Modify `src/TmTimeTracker/TmTimeTracker.csproj` — add inside the existing `<Project>`:

```xml
  <ItemGroup>
    <EmbeddedResource Include="Data\Schema.sql" />
  </ItemGroup>
```

- [ ] **Step 3: Write connection factory**

Create `src/TmTimeTracker/Data/SqliteConnectionFactory.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace TmTimeTracker.Data;

public interface ISqliteConnectionFactory
{
    SqliteConnection Open();
}

public sealed class SqliteConnectionFactory : ISqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(string connectionString) => _connectionString = connectionString;

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return c;
    }
}
```

- [ ] **Step 4: Write initializer**

Create `src/TmTimeTracker/Data/DatabaseInitializer.cs`:

```csharp
using System.Reflection;
using Dapper;

namespace TmTimeTracker.Data;

public sealed class DatabaseInitializer
{
    private readonly ISqliteConnectionFactory _factory;

    public DatabaseInitializer(ISqliteConnectionFactory factory) => _factory = factory;

    public void EnsureCreated()
    {
        using var conn = _factory.Open();
        var sql = LoadEmbeddedSql();
        conn.Execute(sql);
    }

    private static string LoadEmbeddedSql()
    {
        var asm = typeof(DatabaseInitializer).Assembly;
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("Schema.sql", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
```

- [ ] **Step 5: Write test**

Create `tests/TmTimeTracker.Tests/Data/DatabaseInitializerTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Data.Sqlite;
using TmTimeTracker.Data;
using Dapper;
using Xunit;

namespace TmTimeTracker.Tests.Data;

public class DatabaseInitializerTests
{
    [Fact]
    public void Creates_all_expected_tables()
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();

        using var conn = ds.Open();
        var tables = conn.Query<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name").ToList();

        tables.Should().Contain(new[]
        {
            "config", "minute_sample", "oauth_state",
            "remember_entry", "ticket_time"
        });
    }

    [Fact]
    public void Is_idempotent()
    {
        var ds = SharedSqlite.NewInMemory();
        var init = new DatabaseInitializer(ds);
        init.EnsureCreated();
        Action again = () => init.EnsureCreated();
        again.Should().NotThrow();
    }
}
```

- [ ] **Step 6: Add shared in-memory helper**

Create `tests/TmTimeTracker.Tests/Data/SharedSqlite.cs`:

```csharp
using Microsoft.Data.Sqlite;
using TmTimeTracker.Data;

namespace TmTimeTracker.Tests.Data;

internal static class SharedSqlite
{
    public static ISqliteConnectionFactory NewInMemory()
    {
        // Use a shared cache + private named in-memory DB so multiple connections in the same
        // test see the same data. Cache=shared keeps the DB alive while at least one connection
        // exists; each test gets a unique name to stay isolated from siblings.
        var name = $"db-{Guid.NewGuid():N}";
        return new SqliteConnectionFactory(
            $"Data Source=file:{name}?mode=memory&cache=shared");
    }
}
```

- [ ] **Step 7: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~DatabaseInitializerTests"`
Expected: 2 pass.

- [ ] **Step 8: Commit**

```powershell
git add src tests
git commit -m "feat(data): SQLite schema, connection factory, and idempotent initializer"
```

---

### Task 1.7: TicketTimeRepository (TDD)

The single point where `ticket_time` rows are read and written. **Critical invariant:** at most one open cycle per ticket. Enforce via a single "open-or-create" method.

**Files:**
- Create: `src/TmTimeTracker/Data/TicketTimeRepository.cs`
- Create: `src/TmTimeTracker/Data/TicketCycle.cs`
- Create: `tests/TmTimeTracker.Tests/Data/TicketTimeRepositoryTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/TmTimeTracker.Tests/Data/TicketTimeRepositoryTests.cs`:

```csharp
using FluentAssertions;
using TmTimeTracker.Data;
using Xunit;

namespace TmTimeTracker.Tests.Data;

public class TicketTimeRepositoryTests
{
    private static (TicketTimeRepository repo, ISqliteConnectionFactory ds) New()
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        return (new TicketTimeRepository(ds), ds);
    }

    [Fact]
    public void OpenOrCreate_creates_new_row_when_none_exists()
    {
        var (repo, _) = New();
        var cycle = repo.OpenOrCreateCycle("TM-29", DateTime.UtcNow);
        cycle.TicketKey.Should().Be("TM-29");
        cycle.MinutesActive.Should().Be(0);
        cycle.SubmittedAt.Should().BeNull();
    }

    [Fact]
    public void OpenOrCreate_returns_existing_open_row_on_second_call()
    {
        var (repo, _) = New();
        var first = repo.OpenOrCreateCycle("TM-29", DateTime.UtcNow);
        var second = repo.OpenOrCreateCycle("TM-29", DateTime.UtcNow.AddHours(1));
        second.Id.Should().Be(first.Id);
    }

    [Fact]
    public void IncrementMinute_adds_one_minute()
    {
        var (repo, _) = New();
        var cycle = repo.OpenOrCreateCycle("TM-29", DateTime.UtcNow);
        repo.IncrementMinute(cycle.Id);
        repo.IncrementMinute(cycle.Id);
        var reloaded = repo.GetById(cycle.Id);
        reloaded!.MinutesActive.Should().Be(2);
    }

    [Fact]
    public void MarkSubmitted_closes_cycle_and_opens_new_one_next_time()
    {
        var (repo, _) = New();
        var first = repo.OpenOrCreateCycle("TM-29", DateTime.UtcNow);
        repo.MarkSubmitted(first.Id, worklogId: "wl-1",
            submittedMinutes: 30, submittedAtUtc: DateTime.UtcNow);

        var next = repo.OpenOrCreateCycle("TM-29", DateTime.UtcNow.AddMinutes(1));
        next.Id.Should().NotBe(first.Id);
    }

    [Fact]
    public void GetAllOpen_returns_only_unsubmitted_rows()
    {
        var (repo, _) = New();
        var a = repo.OpenOrCreateCycle("TM-29", DateTime.UtcNow);
        var b = repo.OpenOrCreateCycle("TM-30", DateTime.UtcNow);
        repo.MarkSubmitted(b.Id, "wl-1", 10, DateTime.UtcNow);

        var open = repo.GetAllOpen().Select(c => c.Id).ToList();
        open.Should().ContainSingle().Which.Should().Be(a.Id);
    }

    [Fact]
    public void UpdateStatusSnapshot_persists_last_seen_status()
    {
        var (repo, _) = New();
        var c = repo.OpenOrCreateCycle("TM-29", DateTime.UtcNow);
        var now = DateTime.UtcNow;
        repo.UpdateStatusSnapshot(c.Id, "In Progress", now);

        var reloaded = repo.GetById(c.Id)!;
        reloaded.LastSeenStatus.Should().Be("In Progress");
        reloaded.LastPolled.Should().BeCloseTo(now, TimeSpan.FromSeconds(1));
    }
}
```

- [ ] **Step 2: Run and verify fail**

Run: `dotnet test --filter "FullyQualifiedName~TicketTimeRepositoryTests"`
Expected: compile failures.

- [ ] **Step 3: Implement record**

Create `src/TmTimeTracker/Data/TicketCycle.cs`:

```csharp
namespace TmTimeTracker.Data;

public sealed record TicketCycle(
    long Id,
    string TicketKey,
    DateTime CycleStarted,
    int MinutesActive,
    string? LastSeenStatus,
    DateTime? LastPolled,
    DateTime? SubmittedAt,
    string? WorklogId,
    int? SubmittedMinutes);
```

- [ ] **Step 4: Implement repository**

Create `src/TmTimeTracker/Data/TicketTimeRepository.cs`:

```csharp
using Dapper;

namespace TmTimeTracker.Data;

public sealed class TicketTimeRepository
{
    private readonly ISqliteConnectionFactory _factory;
    private readonly object _openOrCreateGate = new();

    public TicketTimeRepository(ISqliteConnectionFactory factory) => _factory = factory;

    public TicketCycle OpenOrCreateCycle(string ticketKey, DateTime cycleStartedUtc)
    {
        // Lock at the process level: SELECT-then-INSERT must be atomic across all callers.
        lock (_openOrCreateGate)
        {
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();

            var existing = conn.QueryFirstOrDefault<TicketCycleRow>(
                "SELECT * FROM ticket_time WHERE ticket_key=@k AND submitted_at IS NULL LIMIT 1",
                new { k = ticketKey }, tx);

            if (existing is not null)
            {
                tx.Commit();
                return existing.ToCycle();
            }

            var iso = cycleStartedUtc.ToString("O");
            var id = conn.ExecuteScalar<long>(
                @"INSERT INTO ticket_time (ticket_key, cycle_started, minutes_active)
                  VALUES (@k, @c, 0);
                  SELECT last_insert_rowid();",
                new { k = ticketKey, c = iso }, tx);
            tx.Commit();

            return new TicketCycle(id, ticketKey, cycleStartedUtc, 0, null, null, null, null, null);
        }
    }

    public void IncrementMinute(long cycleId)
    {
        using var conn = _factory.Open();
        conn.Execute("UPDATE ticket_time SET minutes_active = minutes_active + 1 WHERE id=@id",
            new { id = cycleId });
    }

    public TicketCycle? GetById(long id)
    {
        using var conn = _factory.Open();
        return conn.QueryFirstOrDefault<TicketCycleRow>(
            "SELECT * FROM ticket_time WHERE id=@id", new { id })?.ToCycle();
    }

    public IReadOnlyList<TicketCycle> GetAllOpen()
    {
        using var conn = _factory.Open();
        return conn.Query<TicketCycleRow>(
            "SELECT * FROM ticket_time WHERE submitted_at IS NULL ORDER BY id")
            .Select(r => r.ToCycle()).ToList();
    }

    public void MarkSubmitted(long cycleId, string worklogId, int submittedMinutes, DateTime submittedAtUtc)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"UPDATE ticket_time
              SET worklog_id=@w, submitted_minutes=@m, submitted_at=@s
              WHERE id=@id",
            new { id = cycleId, w = worklogId, m = submittedMinutes, s = submittedAtUtc.ToString("O") });
    }

    public void UpdateStatusSnapshot(long cycleId, string status, DateTime polledAtUtc)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"UPDATE ticket_time
              SET last_seen_status=@s, last_polled=@p
              WHERE id=@id",
            new { id = cycleId, s = status, p = polledAtUtc.ToString("O") });
    }

    // Dapper row class: column names match SQLite snake_case via custom map below.
    private sealed class TicketCycleRow
    {
        public long id { get; set; }
        public string ticket_key { get; set; } = "";
        public string cycle_started { get; set; } = "";
        public int minutes_active { get; set; }
        public string? last_seen_status { get; set; }
        public string? last_polled { get; set; }
        public string? submitted_at { get; set; }
        public string? worklog_id { get; set; }
        public int? submitted_minutes { get; set; }

        public TicketCycle ToCycle() => new(
            Id: id,
            TicketKey: ticket_key,
            CycleStarted: DateTime.Parse(cycle_started, null,
                System.Globalization.DateTimeStyles.RoundtripKind),
            MinutesActive: minutes_active,
            LastSeenStatus: last_seen_status,
            LastPolled: last_polled is null ? null
                : DateTime.Parse(last_polled, null,
                    System.Globalization.DateTimeStyles.RoundtripKind),
            SubmittedAt: submitted_at is null ? null
                : DateTime.Parse(submitted_at, null,
                    System.Globalization.DateTimeStyles.RoundtripKind),
            WorklogId: worklog_id,
            SubmittedMinutes: submitted_minutes);
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~TicketTimeRepositoryTests"`
Expected: all 6 pass.

- [ ] **Step 6: Commit**

```powershell
git add src tests
git commit -m "feat(data): TicketTimeRepository with single-open-cycle invariant"
```

---

### Task 1.8: RememberEntryRepository (TDD)

**Files:**
- Create: `src/TmTimeTracker/Data/RememberEntryRepository.cs`
- Create: `tests/TmTimeTracker.Tests/Data/RememberEntryRepositoryTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/TmTimeTracker.Tests/Data/RememberEntryRepositoryTests.cs`:

```csharp
using FluentAssertions;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Data;

public class RememberEntryRepositoryTests
{
    private static (RememberEntryRepository repo, ISqliteConnectionFactory ds) New()
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        return (new RememberEntryRepository(ds), ds);
    }

    private static RememberEntry E(string ticket, string time, string body, string file) =>
        new(time, $"{ticket}-tag", ticket, body, file);

    [Fact]
    public void Upsert_inserts_new_entry()
    {
        var (repo, _) = New();
        repo.UpsertMany(new[] { E("TM-29", "09:00", "started", "today-2026-05-25.md") },
            entryDate: "2026-05-25");
        var got = repo.GetForTicketAndDate("TM-29", "2026-05-25");
        got.Should().ContainSingle();
        got[0].Body.Should().Be("started");
    }

    [Fact]
    public void Upsert_is_idempotent_on_repeated_calls()
    {
        var (repo, _) = New();
        var e = new[] { E("TM-29", "09:00", "started", "today-2026-05-25.md") };
        repo.UpsertMany(e, "2026-05-25");
        repo.UpsertMany(e, "2026-05-25");
        repo.GetForTicketAndDate("TM-29", "2026-05-25").Should().ContainSingle();
    }

    [Fact]
    public void GetForTicketAndDate_returns_only_matching_rows()
    {
        var (repo, _) = New();
        repo.UpsertMany(new[]
        {
            E("TM-29", "09:00", "a", "today-2026-05-25.md"),
            E("TM-30", "10:00", "b", "today-2026-05-25.md"),
            E("TM-29", "09:00", "a", "today-2026-05-24.md"),
        }, "2026-05-25");

        repo.GetForTicketAndDate("TM-29", "2026-05-25")
            .Should().ContainSingle()
            .Which.Body.Should().Be("a");
    }

    [Fact]
    public void TagConsumed_links_entry_to_worklog_row()
    {
        var (repo, _) = New();
        repo.UpsertMany(new[] { E("TM-29", "09:00", "a", "f.md") }, "2026-05-25");
        var entry = repo.GetForTicketAndDate("TM-29", "2026-05-25").Single();

        repo.TagConsumed(new[] { entry.Id }, ticketTimeRowId: 42);
        var after = repo.GetForTicketAndDate("TM-29", "2026-05-25").Single();
        after.ConsumedInWorklog.Should().Be(42);
    }
}
```

- [ ] **Step 2: Run and verify fail**

Run: `dotnet test --filter "FullyQualifiedName~RememberEntryRepositoryTests"`
Expected: compile failures.

- [ ] **Step 3: Implement**

Create `src/TmTimeTracker/Data/RememberEntryRepository.cs`:

```csharp
using Dapper;
using TmTimeTracker.Logic;

namespace TmTimeTracker.Data;

public sealed record StoredRememberEntry(
    long Id,
    string TicketKey,
    string TimestampLocal,
    string EntryDate,
    string Body,
    string SourceFile,
    long? ConsumedInWorklog);

public sealed class RememberEntryRepository
{
    private readonly ISqliteConnectionFactory _factory;

    public RememberEntryRepository(ISqliteConnectionFactory factory) => _factory = factory;

    public void UpsertMany(IEnumerable<RememberEntry> entries, string entryDate)
    {
        using var conn = _factory.Open();
        using var tx = conn.BeginTransaction();

        foreach (var e in entries)
        {
            if (e.TicketKey is null) continue;
            conn.Execute(
                @"INSERT OR IGNORE INTO remember_entry
                    (ticket_key, timestamp_local, entry_date, body, source_file)
                  VALUES (@k, @t, @d, @b, @f)",
                new
                {
                    k = e.TicketKey,
                    t = e.TimeOfDay,
                    d = entryDate,
                    b = e.Body,
                    f = e.SourceFile
                }, tx);
        }
        tx.Commit();
    }

    public IReadOnlyList<StoredRememberEntry> GetForTicketAndDate(string ticketKey, string entryDate)
    {
        using var conn = _factory.Open();
        return conn.Query<RememberRow>(
            @"SELECT * FROM remember_entry
              WHERE ticket_key=@k AND entry_date=@d
              ORDER BY timestamp_local",
            new { k = ticketKey, d = entryDate })
            .Select(r => r.ToRecord()).ToList();
    }

    public IReadOnlyList<StoredRememberEntry> GetUnconsumedForTicket(string ticketKey)
    {
        using var conn = _factory.Open();
        return conn.Query<RememberRow>(
            @"SELECT * FROM remember_entry
              WHERE ticket_key=@k AND consumed_in_worklog IS NULL
              ORDER BY entry_date, timestamp_local",
            new { k = ticketKey })
            .Select(r => r.ToRecord()).ToList();
    }

    public void TagConsumed(IEnumerable<long> entryIds, long ticketTimeRowId)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE remember_entry SET consumed_in_worklog=@w WHERE id IN @ids",
            new { w = ticketTimeRowId, ids = entryIds.ToArray() });
    }

    private sealed class RememberRow
    {
        public long id { get; set; }
        public string ticket_key { get; set; } = "";
        public string timestamp_local { get; set; } = "";
        public string entry_date { get; set; } = "";
        public string body { get; set; } = "";
        public string source_file { get; set; } = "";
        public long? consumed_in_worklog { get; set; }

        public StoredRememberEntry ToRecord() => new(
            id, ticket_key, timestamp_local, entry_date, body, source_file, consumed_in_worklog);
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~RememberEntryRepositoryTests"`
Expected: all 4 pass.

- [ ] **Step 5: Commit**

```powershell
git add src tests
git commit -m "feat(data): RememberEntryRepository with idempotent upsert and consumption tagging"
```

---

### Task 1.9: ConfigRepository + OAuthStateRepository + MinuteSampleRepository (TDD, compact)

Three small repositories grouped because each has 2–3 trivial methods.

**Files:**
- Create: `src/TmTimeTracker/Data/ConfigRepository.cs`
- Create: `src/TmTimeTracker/Data/OAuthStateRepository.cs`
- Create: `src/TmTimeTracker/Data/MinuteSampleRepository.cs`
- Create: `tests/TmTimeTracker.Tests/Data/SmallRepositoriesTests.cs`

- [ ] **Step 1: Write tests**

Create `tests/TmTimeTracker.Tests/Data/SmallRepositoriesTests.cs`:

```csharp
using FluentAssertions;
using TmTimeTracker.Data;
using Xunit;

namespace TmTimeTracker.Tests.Data;

public class SmallRepositoriesTests
{
    private static ISqliteConnectionFactory Setup()
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        return ds;
    }

    [Fact]
    public void Config_round_trips_with_defaults_when_unset()
    {
        var repo = new ConfigRepository(Setup());
        repo.SetIfMissing(new AppConfig(
            IdleThresholdSeconds: 600,
            JiraPollIntervalSeconds: 90,
            RepoPath: @"c:\projects\training-manager",
            RememberPath: @"c:\projects\training-manager\.remember",
            InProgressStatusName: "In Progress",
            TransitionToStatusName: "Review"));

        var loaded = repo.Get();
        loaded.RepoPath.Should().Be(@"c:\projects\training-manager");
        loaded.IdleThresholdSeconds.Should().Be(600);
    }

    [Fact]
    public void Config_SetIfMissing_does_not_overwrite()
    {
        var repo = new ConfigRepository(Setup());
        repo.SetIfMissing(new AppConfig(600, 90, "a", "b", "ip", "rev"));
        repo.SetIfMissing(new AppConfig(999, 999, "x", "y", "z", "w"));
        repo.Get().RepoPath.Should().Be("a");
    }

    [Fact]
    public void OAuthState_round_trip()
    {
        var repo = new OAuthStateRepository(Setup());
        var dummyAccess = new byte[] { 1, 2, 3 };
        var dummyRefresh = new byte[] { 4, 5 };
        var expires = DateTime.UtcNow.AddHours(1);
        repo.Save(new OAuthState("cloud-xyz", dummyAccess, dummyRefresh, expires, "read:jira-work"));
        var loaded = repo.Load();
        loaded!.CloudId.Should().Be("cloud-xyz");
        loaded.AccessTokenDpapi.Should().BeEquivalentTo(dummyAccess);
        loaded.RefreshTokenDpapi.Should().BeEquivalentTo(dummyRefresh);
        loaded.AccessExpiresAt.Should().BeCloseTo(expires, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void OAuthState_load_returns_null_when_absent()
    {
        var repo = new OAuthStateRepository(Setup());
        repo.Load().Should().BeNull();
    }

    [Fact]
    public void MinuteSample_insert_and_prune()
    {
        var repo = new MinuteSampleRepository(Setup());
        var old = DateTime.UtcNow.AddDays(-40);
        var fresh = DateTime.UtcNow;
        repo.Insert(new MinuteSample(old,    "TM-29", IsIdle: false, "main", ClaudeRunning: true));
        repo.Insert(new MinuteSample(fresh,  "TM-29", IsIdle: false, "main", ClaudeRunning: true));

        var deleted = repo.PruneOlderThan(DateTime.UtcNow.AddDays(-30));
        deleted.Should().Be(1);
        repo.Count().Should().Be(1);
    }
}
```

- [ ] **Step 2: Run and verify fail**

Run: `dotnet test --filter "FullyQualifiedName~SmallRepositoriesTests"`
Expected: compile failures.

- [ ] **Step 3: Implement ConfigRepository**

Create `src/TmTimeTracker/Data/ConfigRepository.cs`:

```csharp
using Dapper;

namespace TmTimeTracker.Data;

public sealed record AppConfig(
    int IdleThresholdSeconds,
    int JiraPollIntervalSeconds,
    string RepoPath,
    string RememberPath,
    string InProgressStatusName,
    string TransitionToStatusName);

public sealed class ConfigRepository
{
    private readonly ISqliteConnectionFactory _factory;
    public ConfigRepository(ISqliteConnectionFactory factory) => _factory = factory;

    public void SetIfMissing(AppConfig defaults)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT OR IGNORE INTO config
                (id, idle_threshold_seconds, jira_poll_interval_seconds,
                 repo_path, remember_path, in_progress_status_name, transition_to_status_name)
              VALUES (1, @it, @pi, @rp, @rm, @ip, @tr)",
            new
            {
                it = defaults.IdleThresholdSeconds,
                pi = defaults.JiraPollIntervalSeconds,
                rp = defaults.RepoPath,
                rm = defaults.RememberPath,
                ip = defaults.InProgressStatusName,
                tr = defaults.TransitionToStatusName
            });
    }

    public AppConfig Get()
    {
        using var conn = _factory.Open();
        var row = conn.QuerySingle<ConfigRow>("SELECT * FROM config WHERE id=1");
        return new AppConfig(
            row.idle_threshold_seconds, row.jira_poll_interval_seconds,
            row.repo_path, row.remember_path,
            row.in_progress_status_name, row.transition_to_status_name);
    }

    private sealed class ConfigRow
    {
        public int idle_threshold_seconds { get; set; }
        public int jira_poll_interval_seconds { get; set; }
        public string repo_path { get; set; } = "";
        public string remember_path { get; set; } = "";
        public string in_progress_status_name { get; set; } = "";
        public string transition_to_status_name { get; set; } = "";
    }
}
```

- [ ] **Step 4: Implement OAuthStateRepository**

Create `src/TmTimeTracker/Data/OAuthStateRepository.cs`:

```csharp
using Dapper;

namespace TmTimeTracker.Data;

public sealed record OAuthState(
    string CloudId,
    byte[] AccessTokenDpapi,
    byte[] RefreshTokenDpapi,
    DateTime AccessExpiresAt,
    string Scope);

public sealed class OAuthStateRepository
{
    private readonly ISqliteConnectionFactory _factory;
    public OAuthStateRepository(ISqliteConnectionFactory factory) => _factory = factory;

    public void Save(OAuthState s)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO oauth_state
                (id, cloud_id, access_token_dpapi, refresh_token_dpapi,
                 access_expires_at, scope)
              VALUES (1, @c, @a, @r, @e, @s)
              ON CONFLICT(id) DO UPDATE SET
                cloud_id=excluded.cloud_id,
                access_token_dpapi=excluded.access_token_dpapi,
                refresh_token_dpapi=excluded.refresh_token_dpapi,
                access_expires_at=excluded.access_expires_at,
                scope=excluded.scope",
            new
            {
                c = s.CloudId,
                a = s.AccessTokenDpapi,
                r = s.RefreshTokenDpapi,
                e = s.AccessExpiresAt.ToString("O"),
                s = s.Scope
            });
    }

    public OAuthState? Load()
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<OAuthRow>("SELECT * FROM oauth_state WHERE id=1");
        if (row is null) return null;
        return new OAuthState(
            row.cloud_id,
            row.access_token_dpapi,
            row.refresh_token_dpapi,
            DateTime.Parse(row.access_expires_at, null,
                System.Globalization.DateTimeStyles.RoundtripKind),
            row.scope);
    }

    private sealed class OAuthRow
    {
        public string cloud_id { get; set; } = "";
        public byte[] access_token_dpapi { get; set; } = Array.Empty<byte>();
        public byte[] refresh_token_dpapi { get; set; } = Array.Empty<byte>();
        public string access_expires_at { get; set; } = "";
        public string scope { get; set; } = "";
    }
}
```

- [ ] **Step 5: Implement MinuteSampleRepository**

Create `src/TmTimeTracker/Data/MinuteSampleRepository.cs`:

```csharp
using Dapper;

namespace TmTimeTracker.Data;

public sealed record MinuteSample(
    DateTime SampledAt,
    string? TicketKey,
    bool IsIdle,
    string? GitBranch,
    bool ClaudeRunning);

public sealed class MinuteSampleRepository
{
    private readonly ISqliteConnectionFactory _factory;
    public MinuteSampleRepository(ISqliteConnectionFactory factory) => _factory = factory;

    public void Insert(MinuteSample s)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT OR REPLACE INTO minute_sample
                (sampled_at, ticket_key, is_idle, git_branch, claude_running)
              VALUES (@t, @k, @i, @b, @c)",
            new
            {
                t = s.SampledAt.ToString("O"),
                k = s.TicketKey,
                i = s.IsIdle ? 1 : 0,
                b = s.GitBranch,
                c = s.ClaudeRunning ? 1 : 0
            });
    }

    public int PruneOlderThan(DateTime cutoffUtc)
    {
        using var conn = _factory.Open();
        return conn.Execute("DELETE FROM minute_sample WHERE sampled_at < @c",
            new { c = cutoffUtc.ToString("O") });
    }

    public int Count()
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<int>("SELECT COUNT(*) FROM minute_sample");
    }
}
```

- [ ] **Step 6: Run all data tests**

Run: `dotnet test --filter "FullyQualifiedName~Tests.Data"`
Expected: ~12 tests pass.

- [ ] **Step 7: Commit**

```powershell
git add src tests
git commit -m "feat(data): config, oauth-state, and minute-sample repositories"
```

---

### Phase 1 checkpoint

- [ ] Run full test suite: `dotnet test`
- [ ] Expected: 30+ tests pass, 0 fail.
- [ ] Review the diff so far — three pure-logic modules + five repositories.
- [ ] **STOP** and confirm with human partner before starting Phase 2.

---

# Phase 2: Activity Monitoring

Goal of phase: minute-by-minute tracking of (idle, branch, ticket, remember entries) wired through an in-process `EventBus`, with a console runner that demonstrates the loop end-to-end without Jira.

### Task 2.1: EventBus + domain events + SystemClock

**Files:**
- Create: `src/TmTimeTracker/Services/EventBus.cs`
- Create: `src/TmTimeTracker/Services/DomainEvents.cs`
- Create: `src/TmTimeTracker/Services/SystemClock.cs`

- [ ] **Step 1: Define events**

Create `src/TmTimeTracker/Services/DomainEvents.cs`:

```csharp
using TmTimeTracker.Logic;

namespace TmTimeTracker.Services;

public abstract record DomainEvent(DateTime AtUtc);

public sealed record ActivityChanged(UserActivityState State, DateTime AtUtc) : DomainEvent(AtUtc);
public sealed record BranchChanged(string? Branch, string? TicketKey, DateTime AtUtc) : DomainEvent(AtUtc);
public sealed record RememberEntriesObserved(IReadOnlyList<RememberEntry> Entries, string EntryDate, DateTime AtUtc) : DomainEvent(AtUtc);
public sealed record JiraStatusTransition(string TicketKey, string FromStatus, string ToStatus, DateTime AtUtc) : DomainEvent(AtUtc);
public sealed record WorklogSubmitted(string TicketKey, string WorklogId, int Minutes, DateTime AtUtc) : DomainEvent(AtUtc);
public sealed record AuthenticationRequired(string Reason, DateTime AtUtc) : DomainEvent(AtUtc);
```

- [ ] **Step 2: Implement bus**

Create `src/TmTimeTracker/Services/EventBus.cs`:

```csharp
using System.Threading.Channels;

namespace TmTimeTracker.Services;

public interface IEventBus
{
    ValueTask PublishAsync(DomainEvent evt, CancellationToken ct = default);
    IAsyncEnumerable<DomainEvent> Subscribe(CancellationToken ct = default);
}

public sealed class EventBus : IEventBus
{
    private readonly List<Channel<DomainEvent>> _subscribers = new();
    private readonly object _lock = new();

    public async ValueTask PublishAsync(DomainEvent evt, CancellationToken ct = default)
    {
        Channel<DomainEvent>[] snapshot;
        lock (_lock) snapshot = _subscribers.ToArray();
        foreach (var c in snapshot)
            await c.Writer.WriteAsync(evt, ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<DomainEvent> Subscribe(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<DomainEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        lock (_lock) _subscribers.Add(channel);

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return evt;
        }
        finally
        {
            lock (_lock) _subscribers.Remove(channel);
            channel.Writer.TryComplete();
        }
    }
}
```

- [ ] **Step 3: Implement clock abstraction**

Create `src/TmTimeTracker/Services/SystemClock.cs`:

```csharp
namespace TmTimeTracker.Services;

public interface IClock
{
    DateTime UtcNow { get; }
    DateTimeOffset LocalNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
    public DateTimeOffset LocalNow => DateTimeOffset.Now;
}
```

- [ ] **Step 4: No tests yet**

The bus and clock are too thin to merit unit tests on their own — they will be exercised in Task 2.7 via the aggregator's integration tests. Skip TDD here per YAGNI.

- [ ] **Step 5: Verify build**

Run: `dotnet build`
Expected: success.

- [ ] **Step 6: Commit**

```powershell
git add src
git commit -m "feat(services): channel-based EventBus, domain events, IClock abstraction"
```

---

### Task 2.2: Win32IdleProbe + GitBranchProbe + IdleMonitor BackgroundService

Two thin platform probes, wired into one BackgroundService that emits `ActivityChanged` and another that emits `BranchChanged`. Probes are interface-fronted so the services can be tested with fakes.

**Files:**
- Create: `src/TmTimeTracker/Platform/IIdleProbe.cs`
- Create: `src/TmTimeTracker/Platform/Win32IdleProbe.cs`
- Create: `src/TmTimeTracker/Platform/IGitBranchProbe.cs`
- Create: `src/TmTimeTracker/Platform/GitBranchProbe.cs`
- Create: `src/TmTimeTracker/Services/IdleMonitor.cs`
- Create: `src/TmTimeTracker/Services/BranchWatcher.cs`

- [ ] **Step 1: Idle probe interface and Win32 impl**

Create `src/TmTimeTracker/Platform/IIdleProbe.cs`:

```csharp
namespace TmTimeTracker.Platform;

public interface IIdleProbe
{
    long SecondsSinceLastInput();
    bool IsSessionLocked();
}
```

Create `src/TmTimeTracker/Platform/Win32IdleProbe.cs`:

```csharp
using System.Runtime.InteropServices;

namespace TmTimeTracker.Platform;

public sealed class Win32IdleProbe : IIdleProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    public long SecondsSinceLastInput()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        var deltaMs = unchecked(GetTickCount() - info.dwTime);
        return deltaMs / 1000;
    }

    public bool IsSessionLocked()
    {
        // OpenInputDesktop returns 0 when current session input desktop is the secure desktop
        // (lock screen / UAC). This is a coarse signal that works for the lock case.
        const uint DESKTOP_SWITCHDESKTOP = 0x0100;
        var hDesk = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
        if (hDesk == IntPtr.Zero) return true;
        CloseDesktop(hDesk);
        return false;
    }
}
```

- [ ] **Step 2: Git branch probe interface and impl**

Create `src/TmTimeTracker/Platform/IGitBranchProbe.cs`:

```csharp
namespace TmTimeTracker.Platform;

public interface IGitBranchProbe
{
    /// <summary>Returns the current branch name, or null if the repo path is invalid/detached.</summary>
    string? GetCurrentBranch(string repoPath);
}
```

Create `src/TmTimeTracker/Platform/GitBranchProbe.cs`:

```csharp
using System.Diagnostics;

namespace TmTimeTracker.Platform;

public sealed class GitBranchProbe : IGitBranchProbe
{
    public string? GetCurrentBranch(string repoPath)
    {
        if (!Directory.Exists(repoPath)) return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse --abbrev-ref HEAD",
                WorkingDirectory = repoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            if (p.ExitCode != 0 || string.IsNullOrEmpty(output) || output == "HEAD")
                return null;
            return output;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
```

- [ ] **Step 3: IdleMonitor BackgroundService**

Create `src/TmTimeTracker/Services/IdleMonitor.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using TmTimeTracker.Platform;

namespace TmTimeTracker.Services;

public sealed class IdleMonitor : BackgroundService
{
    private readonly IIdleProbe _probe;
    private readonly IEventBus _bus;
    private readonly IClock _clock;
    private readonly ConfigRepository _config;
    private readonly ILogger<IdleMonitor> _log;
    private readonly TimeSpan _sampleInterval = TimeSpan.FromSeconds(5);

    public IdleMonitor(IIdleProbe probe, IEventBus bus, IClock clock,
        ConfigRepository config, ILogger<IdleMonitor> log)
    {
        _probe = probe; _bus = bus; _clock = clock; _config = config; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var threshold = TimeSpan.FromSeconds(_config.Get().IdleThresholdSeconds);
        var sm = new IdleStateMachine(threshold);
        sm.OnTransition += state =>
        {
            _ = _bus.PublishAsync(new ActivityChanged(state, _clock.UtcNow));
            _log.LogInformation("Activity state -> {State}", state);
        };

        // Emit initial state too, so downstream knows where we start.
        await _bus.PublishAsync(new ActivityChanged(sm.Current, _clock.UtcNow), stoppingToken)
            .ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                sm.Observe(_probe.SecondsSinceLastInput(), _probe.IsSessionLocked());
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Idle probe failed; assuming idle this tick");
                sm.Observe(long.MaxValue, isLocked: true);
            }
            await Task.Delay(_sampleInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}
```

- [ ] **Step 4: BranchWatcher BackgroundService**

Create `src/TmTimeTracker/Services/BranchWatcher.cs`:

```csharp
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
```

- [ ] **Step 5: Verify build**

Run: `dotnet build`
Expected: success.

- [ ] **Step 6: Commit**

```powershell
git add src
git commit -m "feat(services): idle and branch background services with Win32/git probes"
```

---

### Task 2.3: RememberWatcher

Watches `.remember/today-YYYY-MM-DD.md` and `now.md`, parses changes, persists, and emits events.

**Files:**
- Create: `src/TmTimeTracker/Services/RememberWatcher.cs`

- [ ] **Step 1: Implement**

Create `src/TmTimeTracker/Services/RememberWatcher.cs`:

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
    private readonly ConfigRepository _config;
    private readonly RememberEntryRepository _entries;
    private readonly ILogger<RememberWatcher> _log;
    private FileSystemWatcher? _fsw;

    public RememberWatcher(IEventBus bus, IClock clock, ConfigRepository config,
        RememberEntryRepository entries, ILogger<RememberWatcher> log)
    {
        _bus = bus; _clock = clock; _config = config; _entries = entries; _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = _config.Get().RememberPath;
        if (!Directory.Exists(path))
        {
            _log.LogWarning("Remember path {Path} not found; watcher disabled", path);
            return Task.CompletedTask;
        }

        ScanAll(path);

        _fsw = new FileSystemWatcher(path)
        {
            Filter = "*.md",
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
        };
        _fsw.Changed += (_, e) => SafeScanFile(e.FullPath);
        _fsw.Created += (_, e) => SafeScanFile(e.FullPath);

        stoppingToken.Register(() =>
        {
            _fsw.EnableRaisingEvents = false;
            _fsw.Dispose();
        });

        return Task.Delay(Timeout.Infinite, stoppingToken);
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
            try
            {
                content = File.ReadAllText(fullPath);
            }
            catch (IOException)
            {
                // File still being written; ignore — next event will pick it up.
                return;
            }

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
        // today-2026-05-25.md -> "2026-05-25"; now.md -> null (caller defaults to today)
        if (!name.StartsWith("today-", StringComparison.OrdinalIgnoreCase)) return null;
        var datePart = Path.GetFileNameWithoutExtension(name).Substring("today-".Length);
        return DateTime.TryParse(datePart, out _) ? datePart : null;
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: success.

- [ ] **Step 3: Commit**

```powershell
git add src
git commit -m "feat(services): RememberWatcher with FileSystemWatcher and parser pipeline"
```

---

### Task 2.4: TimeAggregator (TDD with fake clock and fake bus)

The core orchestrator. Subscribes to `ActivityChanged` and `BranchChanged`, maintains an internal "Active + has-ticket" predicate, and runs a 60-second timer that increments the appropriate ticket cycle when the predicate is true.

**Files:**
- Create: `src/TmTimeTracker/Services/TimeAggregator.cs`
- Create: `tests/TmTimeTracker.Tests/Services/TimeAggregatorTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/TmTimeTracker.Tests/Services/TimeAggregatorTests.cs`:

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
    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 5, 25, 9, 0, 0, DateTimeKind.Utc);
        public DateTimeOffset LocalNow => new(UtcNow, TimeSpan.Zero);
    }

    private static (TimeAggregator agg, FakeClock clock, TicketTimeRepository tickets, EventBus bus) Build()
    {
        var ds = SharedSqlite.NewInMemory();
        new DatabaseInitializer(ds).EnsureCreated();
        var tickets = new TicketTimeRepository(ds);
        var bus = new EventBus();
        var clock = new FakeClock();
        var agg = new TimeAggregator(bus, tickets, clock, NullLogger<TimeAggregator>.Instance);
        return (agg, clock, tickets, bus);
    }

    [Fact]
    public async Task Increments_when_active_and_has_ticket()
    {
        var (agg, _, tickets, bus) = Build();
        await bus.PublishAsync(new ActivityChanged(UserActivityState.Active, DateTime.UtcNow));
        await bus.PublishAsync(new BranchChanged("TM-29-x", "TM-29", DateTime.UtcNow));

        await agg.TickAsync();

        var open = tickets.GetAllOpen().Should().ContainSingle().Subject;
        open.TicketKey.Should().Be("TM-29");
        open.MinutesActive.Should().Be(1);
    }

    [Fact]
    public async Task Does_not_increment_when_idle()
    {
        var (agg, _, tickets, bus) = Build();
        await bus.PublishAsync(new ActivityChanged(UserActivityState.Idle, DateTime.UtcNow));
        await bus.PublishAsync(new BranchChanged("TM-29-x", "TM-29", DateTime.UtcNow));

        await agg.TickAsync();
        tickets.GetAllOpen().Should().BeEmpty();
    }

    [Fact]
    public async Task Does_not_increment_when_no_ticket()
    {
        var (agg, _, tickets, bus) = Build();
        await bus.PublishAsync(new ActivityChanged(UserActivityState.Active, DateTime.UtcNow));
        await bus.PublishAsync(new BranchChanged("main", null, DateTime.UtcNow));

        await agg.TickAsync();
        tickets.GetAllOpen().Should().BeEmpty();
    }

    [Fact]
    public async Task Switching_branch_opens_new_cycle_for_new_ticket()
    {
        var (agg, _, tickets, bus) = Build();
        await bus.PublishAsync(new ActivityChanged(UserActivityState.Active, DateTime.UtcNow));

        await bus.PublishAsync(new BranchChanged("feature/TM-29", "TM-29", DateTime.UtcNow));
        await agg.TickAsync();

        await bus.PublishAsync(new BranchChanged("feature/TM-30", "TM-30", DateTime.UtcNow));
        await agg.TickAsync();
        await agg.TickAsync();

        var open = tickets.GetAllOpen();
        open.Should().HaveCount(2);
        open.Single(c => c.TicketKey == "TM-29").MinutesActive.Should().Be(1);
        open.Single(c => c.TicketKey == "TM-30").MinutesActive.Should().Be(2);
    }

    [Fact]
    public async Task Switching_back_resumes_existing_cycle()
    {
        var (agg, _, tickets, bus) = Build();
        await bus.PublishAsync(new ActivityChanged(UserActivityState.Active, DateTime.UtcNow));

        await bus.PublishAsync(new BranchChanged("TM-29-x", "TM-29", DateTime.UtcNow));
        await agg.TickAsync();
        await bus.PublishAsync(new BranchChanged("TM-30-x", "TM-30", DateTime.UtcNow));
        await agg.TickAsync();
        await bus.PublishAsync(new BranchChanged("TM-29-x", "TM-29", DateTime.UtcNow));
        await agg.TickAsync();

        var open = tickets.GetAllOpen();
        open.Single(c => c.TicketKey == "TM-29").MinutesActive.Should().Be(2);
        open.Single(c => c.TicketKey == "TM-30").MinutesActive.Should().Be(1);
    }
}
```

- [ ] **Step 2: Run and verify fail**

Run: `dotnet test --filter "FullyQualifiedName~TimeAggregatorTests"`
Expected: compile failure.

- [ ] **Step 3: Implement**

Create `src/TmTimeTracker/Services/TimeAggregator.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;

namespace TmTimeTracker.Services;

public sealed class TimeAggregator : BackgroundService
{
    private readonly IEventBus _bus;
    private readonly TicketTimeRepository _tickets;
    private readonly IClock _clock;
    private readonly ILogger<TimeAggregator> _log;

    private UserActivityState _activity = UserActivityState.Active;
    private string? _currentTicket;

    public TimeAggregator(IEventBus bus, TicketTimeRepository tickets, IClock clock,
        ILogger<TimeAggregator> log)
    {
        _bus = bus; _tickets = tickets; _clock = clock; _log = log;
    }

    /// <summary>Process one minute-tick. Public for test access.</summary>
    public Task TickAsync()
    {
        if (_activity == UserActivityState.Active && _currentTicket is not null)
        {
            var cycle = _tickets.OpenOrCreateCycle(_currentTicket, _clock.UtcNow);
            _tickets.IncrementMinute(cycle.Id);
            _log.LogDebug("+1 min on {Ticket} (cycle {Id})", _currentTicket, cycle.Id);
        }
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = Task.Run(async () =>
        {
            await foreach (var evt in _bus.Subscribe(stoppingToken).ConfigureAwait(false))
            {
                switch (evt)
                {
                    case ActivityChanged a: _activity = a.State; break;
                    case BranchChanged b: _currentTicket = b.TicketKey; break;
                }
            }
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

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~TimeAggregatorTests"`
Expected: all 5 pass. (Note: tests publish events then immediately call `TickAsync` — there's a race with the subscriber. Add an awaited "flush" helper in the test if any flake: drop a 50ms `Task.Delay` after each Publish in the test.)

If tests flake due to the publish/subscribe race, add to `TimeAggregator`:

```csharp
public Task ApplyEventForTestAsync(DomainEvent evt)
{
    switch (evt)
    {
        case ActivityChanged a: _activity = a.State; break;
        case BranchChanged b: _currentTicket = b.TicketKey; break;
    }
    return Task.CompletedTask;
}
```

…and rewrite tests to call `agg.ApplyEventForTestAsync(...)` instead of `bus.PublishAsync(...)`. This is the preferred path — it removes the race entirely. Update tests if so.

- [ ] **Step 5: Commit**

```powershell
git add src tests
git commit -m "feat(services): TimeAggregator with subscription + per-minute tick"
```

---

### Task 2.5: Phase-2 smoke harness (console mode)

A throwaway `--smoke-activity` CLI mode that wires only Phase 2 components and prints state changes to console. Lets us eyeball the loop on real input before adding Jira.

**Files:**
- Modify: `src/TmTimeTracker/Program.cs`
- Create: `src/TmTimeTracker/Configuration/AppPaths.cs`
- Create: `src/TmTimeTracker/HostingExtensions.cs`

- [ ] **Step 1: AppPaths**

Create `src/TmTimeTracker/Configuration/AppPaths.cs`:

```csharp
namespace TmTimeTracker.Configuration;

public static class AppPaths
{
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TmTimeTracker");

    public static string DatabasePath => Path.Combine(DataDir, "state.db");
    public static string LogsDir => Path.Combine(DataDir, "logs");

    public static void EnsureExists()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
    }
}
```

- [ ] **Step 2: HostingExtensions (Phase 2 subset)**

Create `src/TmTimeTracker/HostingExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TmTimeTracker.Configuration;
using TmTimeTracker.Data;
using TmTimeTracker.Platform;
using TmTimeTracker.Services;

namespace TmTimeTracker;

public static class HostingExtensions
{
    public static IHostBuilder AddTmTimeTrackerCore(this IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            AppPaths.EnsureExists();
            services.AddSingleton<ISqliteConnectionFactory>(
                new SqliteConnectionFactory($"Data Source={AppPaths.DatabasePath}"));
            services.AddSingleton<DatabaseInitializer>();
            services.AddSingleton<TicketTimeRepository>();
            services.AddSingleton<RememberEntryRepository>();
            services.AddSingleton<ConfigRepository>();
            services.AddSingleton<OAuthStateRepository>();
            services.AddSingleton<MinuteSampleRepository>();
            services.AddSingleton<IClock, SystemClock>();
            services.AddSingleton<IEventBus, EventBus>();
            services.AddSingleton<IIdleProbe, Win32IdleProbe>();
            services.AddSingleton<IGitBranchProbe, GitBranchProbe>();
        });
        return builder;
    }

    public static IHostBuilder AddActivityServices(this IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddHostedService<IdleMonitor>();
            services.AddHostedService<BranchWatcher>();
            services.AddHostedService<RememberWatcher>();
            services.AddHostedService<TimeAggregator>();
        });
        return builder;
    }
}
```

- [ ] **Step 3: Update Program.cs for smoke mode**

Replace `src/TmTimeTracker/Program.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker;
using TmTimeTracker.Data;
using TmTimeTracker.Services;

if (args.Length == 1 && args[0] == "--smoke-activity")
{
    await RunActivitySmoke();
    return;
}

Console.WriteLine("TmTimeTracker scaffold OK. Try --smoke-activity once Phase 2 is wired.");

async Task RunActivitySmoke()
{
    var host = Host.CreateDefaultBuilder()
        .ConfigureLogging(b => b.ClearProviders().AddSimpleConsole())
        .AddTmTimeTrackerCore()
        .AddActivityServices()
        .Build();

    using var scope = host.Services.CreateScope();
    var init = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    init.EnsureCreated();
    var cfg = scope.ServiceProvider.GetRequiredService<ConfigRepository>();
    cfg.SetIfMissing(new AppConfig(
        IdleThresholdSeconds: 600,
        JiraPollIntervalSeconds: 90,
        RepoPath: Environment.CurrentDirectory,
        RememberPath: Path.Combine(Environment.CurrentDirectory, ".remember"),
        InProgressStatusName: "In Progress",
        TransitionToStatusName: "Review"));

    var bus = host.Services.GetRequiredService<IEventBus>();
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    _ = Task.Run(async () =>
    {
        await foreach (var evt in bus.Subscribe(cts.Token))
            Console.WriteLine($"[{evt.AtUtc:HH:mm:ss}] {evt.GetType().Name}: {evt}");
    });

    await host.RunAsync(cts.Token);
}
```

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: success.

- [ ] **Step 5: Smoke test (manual)**

Run: `dotnet run --project src\TmTimeTracker -- --smoke-activity`
Expected: console prints `ActivityChanged: Active` immediately, then prints `BranchChanged` when you `git checkout` to a different branch in another shell. Switch to/from a `TM-XX` branch and back. Press Ctrl+C to stop.

- [ ] **Step 6: Commit**

```powershell
git add src
git commit -m "feat(app): activity smoke harness via --smoke-activity CLI flag"
```

---

### Phase 2 checkpoint

- [ ] `dotnet test` — all unit tests pass
- [ ] Manual smoke ran and produced expected events
- [ ] **STOP** for review before Phase 3.

---

# Phase 3: Jira Integration + OAuth

Goal of phase: a CLI command `--login` that drives the OAuth 3LO flow end-to-end (opens browser, captures callback, stores tokens) and `--probe-jira TM-29` that fetches a ticket's status to prove API plumbing.

### Task 3.1: Wiring secrets.json + AppSecrets

**Files:**
- Create: `secrets.example.json`
- Create: `src/TmTimeTracker/Configuration/AppSecrets.cs`

- [ ] **Step 1: secrets.example.json**

Create `secrets.example.json` at solution root:

```json
{
  "Atlassian": {
    "OAuthClientId": "REPLACE_ME",
    "OAuthClientSecret": "REPLACE_ME",
    "RedirectUri": "http://localhost:53682/callback"
  }
}
```

- [ ] **Step 2: AppSecrets**

Create `src/TmTimeTracker/Configuration/AppSecrets.cs`:

```csharp
namespace TmTimeTracker.Configuration;

public sealed class AppSecrets
{
    public AtlassianSecrets Atlassian { get; init; } = new();
}

public sealed class AtlassianSecrets
{
    public string OAuthClientId { get; init; } = "";
    public string OAuthClientSecret { get; init; } = "";
    public string RedirectUri { get; init; } = "http://localhost:53682/callback";
}
```

- [ ] **Step 3: Make Program load secrets**

Modify `src/TmTimeTracker/Program.cs` at the top, before host build. (We'll fully replace Program in Task 4.x; for now extend the existing smoke path.)

The full update will be done as part of Task 3.10. For now, leave Program as-is; just having `AppSecrets.cs` compiled is enough.

- [ ] **Step 4: Commit**

```powershell
git add secrets.example.json src
git commit -m "build: secrets template and AppSecrets binding for Atlassian OAuth"
```

---

### Task 3.2: DPAPI TokenProtector + JiraDtos

**Files:**
- Create: `src/TmTimeTracker/Platform/DpapiTokenProtector.cs`
- Create: `src/TmTimeTracker/Jira/JiraDtos.cs`

- [ ] **Step 1: DPAPI wrapper**

Create `src/TmTimeTracker/Platform/DpapiTokenProtector.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace TmTimeTracker.Platform;

public interface ITokenProtector
{
    byte[] Protect(string plaintext);
    string Unprotect(byte[] ciphertext);
}

public sealed class DpapiTokenProtector : ITokenProtector
{
    public byte[] Protect(string plaintext) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser);

    public string Unprotect(byte[] ciphertext) =>
        Encoding.UTF8.GetString(
            ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser));
}
```

- [ ] **Step 2: DTOs**

Create `src/TmTimeTracker/Jira/JiraDtos.cs`:

```csharp
using System.Text.Json.Serialization;

namespace TmTimeTracker.Jira;

public sealed record AtlassianResource(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("scopes")] string[] Scopes);

public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresInSeconds,
    [property: JsonPropertyName("scope")] string Scope);

public sealed record IssueStatus(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("statusCategory")] StatusCategory Category);

public sealed record StatusCategory(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name);

public sealed record IssueFields(
    [property: JsonPropertyName("status")] IssueStatus Status);

public sealed record Issue(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("fields")] IssueFields Fields);

public sealed record WorklogRequest(
    [property: JsonPropertyName("timeSpentSeconds")] int TimeSpentSeconds,
    [property: JsonPropertyName("started")] string StartedIso,
    [property: JsonPropertyName("comment")] WorklogComment Comment);

public sealed record WorklogComment(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("content")] WorklogContent[] Content);

public sealed record WorklogContent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("content")] WorklogTextNode[] Content);

public sealed record WorklogTextNode(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text);

public sealed record WorklogResponse(
    [property: JsonPropertyName("id")] string Id);
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: success.

- [ ] **Step 4: Commit**

```powershell
git add src
git commit -m "feat(jira): DPAPI token protector and Jira REST DTOs"
```

---

### Task 3.3: LocalCallbackListener (HttpListener for OAuth callback)

**Files:**
- Create: `src/TmTimeTracker/Jira/LocalCallbackListener.cs`

- [ ] **Step 1: Implement**

Create `src/TmTimeTracker/Jira/LocalCallbackListener.cs`:

```csharp
using System.Net;

namespace TmTimeTracker.Jira;

public sealed class LocalCallbackListener
{
    public sealed record CallbackResult(string Code, string State);

    /// <summary>
    /// Listens for ONE callback at <paramref name="prefix"/> (must end with /), returns the
    /// authorization code or throws on cancellation. Renders a tiny HTML success page.
    /// </summary>
    public async Task<CallbackResult> ListenOnceAsync(string prefix, CancellationToken ct)
    {
        if (!prefix.EndsWith("/")) prefix += "/";

        using var http = new HttpListener();
        http.Prefixes.Add(prefix);
        http.Start();

        var ctxTask = http.GetContextAsync();
        using var reg = ct.Register(() => http.Stop());
        var ctx = await ctxTask.ConfigureAwait(false);

        var query = ctx.Request.QueryString;
        var code = query["code"];
        var state = query["state"];
        var error = query["error"];

        string body;
        if (!string.IsNullOrEmpty(error))
        {
            ctx.Response.StatusCode = 400;
            body = $"<html><body><h1>Authorization failed</h1><p>{WebUtility.HtmlEncode(error)}</p></body></html>";
        }
        else
        {
            ctx.Response.StatusCode = 200;
            body = "<html><body><h2>TmTimeTracker connected. You can close this tab.</h2></body></html>";
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentType = "text/html";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        ctx.Response.OutputStream.Close();

        if (string.IsNullOrEmpty(code))
            throw new InvalidOperationException($"OAuth callback returned no code (error={error}).");
        return new CallbackResult(code!, state ?? "");
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: success.

- [ ] **Step 3: Commit**

```powershell
git add src
git commit -m "feat(jira): local HTTP listener for OAuth callback"
```

---

### Task 3.4: JiraOAuthClient (token exchange + refresh)

**Files:**
- Create: `src/TmTimeTracker/Jira/JiraOAuthClient.cs`
- Create: `tests/TmTimeTracker.Tests/Jira/JiraOAuthClientTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/TmTimeTracker.Tests/Jira/JiraOAuthClientTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using TmTimeTracker.Configuration;
using TmTimeTracker.Jira;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace TmTimeTracker.Tests.Jira;

public class JiraOAuthClientTests : IDisposable
{
    private readonly WireMockServer _server;

    public JiraOAuthClientTests()
    {
        _server = WireMockServer.Start();
    }

    public void Dispose() => _server.Dispose();

    private JiraOAuthClient NewClient() => new(
        new HttpClient { BaseAddress = new Uri(_server.Url!) },
        new AtlassianSecrets
        {
            OAuthClientId = "client-id",
            OAuthClientSecret = "client-secret",
            RedirectUri = "http://localhost:53682/callback"
        },
        oauthBaseOverride: _server.Url!);

    [Fact]
    public async Task ExchangeCode_returns_tokens()
    {
        _server.Given(Request.Create().WithPath("/oauth/token").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithBodyAsJson(new
                   {
                       access_token = "at",
                       refresh_token = "rt",
                       expires_in = 3600,
                       scope = "read:jira-work"
                   }));

        var resp = await NewClient().ExchangeCodeAsync("auth-code", CancellationToken.None);

        resp.AccessToken.Should().Be("at");
        resp.RefreshToken.Should().Be("rt");
        resp.ExpiresInSeconds.Should().Be(3600);
    }

    [Fact]
    public async Task Refresh_returns_new_tokens()
    {
        _server.Given(Request.Create().WithPath("/oauth/token").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithBodyAsJson(new
                   {
                       access_token = "new-at",
                       refresh_token = "new-rt",
                       expires_in = 3600,
                       scope = "read:jira-work"
                   }));

        var resp = await NewClient().RefreshAsync("old-rt", CancellationToken.None);
        resp.AccessToken.Should().Be("new-at");
        resp.RefreshToken.Should().Be("new-rt");
    }

    [Fact]
    public void BuildAuthorizationUrl_includes_all_required_params()
    {
        var url = NewClient().BuildAuthorizationUrl(state: "xyz");
        url.Should().Contain("audience=api.atlassian.com");
        url.Should().Contain("client_id=client-id");
        url.Should().Contain("scope=read%3Ajira-work");
        url.Should().Contain("scope=write%3Ajira-work");
        url.Should().Contain("offline_access");
        url.Should().Contain("state=xyz");
        url.Should().Contain("response_type=code");
        url.Should().Contain("prompt=consent");
    }
}
```

- [ ] **Step 2: Run and verify fail**

Run: `dotnet test --filter "FullyQualifiedName~JiraOAuthClientTests"`
Expected: compile failures.

- [ ] **Step 3: Implement**

Create `src/TmTimeTracker/Jira/JiraOAuthClient.cs`:

```csharp
using System.Net.Http.Json;
using System.Text;
using TmTimeTracker.Configuration;

namespace TmTimeTracker.Jira;

public sealed class JiraOAuthClient
{
    private const string DefaultOAuthBase = "https://auth.atlassian.com";
    private const string AuthorizePath = "/authorize";
    private const string TokenPath = "/oauth/token";

    private readonly HttpClient _http;
    private readonly AtlassianSecrets _secrets;
    private readonly string _oauthBase;

    public JiraOAuthClient(HttpClient http, AtlassianSecrets secrets, string? oauthBaseOverride = null)
    {
        _http = http; _secrets = secrets;
        _oauthBase = oauthBaseOverride ?? DefaultOAuthBase;
    }

    public string BuildAuthorizationUrl(string state)
    {
        var qs = new StringBuilder()
            .Append("audience=api.atlassian.com")
            .Append("&client_id=").Append(Uri.EscapeDataString(_secrets.OAuthClientId))
            .Append("&scope=").Append(Uri.EscapeDataString("read:jira-work write:jira-work offline_access"))
            .Append("&redirect_uri=").Append(Uri.EscapeDataString(_secrets.RedirectUri))
            .Append("&state=").Append(Uri.EscapeDataString(state))
            .Append("&response_type=code&prompt=consent");
        return $"{_oauthBase}{AuthorizePath}?{qs}";
    }

    public async Task<TokenResponse> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        var payload = new
        {
            grant_type = "authorization_code",
            client_id = _secrets.OAuthClientId,
            client_secret = _secrets.OAuthClientSecret,
            code,
            redirect_uri = _secrets.RedirectUri
        };
        var resp = await _http.PostAsJsonAsync($"{_oauthBase}{TokenPath}", payload, ct)
                              .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct)
                              .ConfigureAwait(false))!;
    }

    public async Task<TokenResponse> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var payload = new
        {
            grant_type = "refresh_token",
            client_id = _secrets.OAuthClientId,
            client_secret = _secrets.OAuthClientSecret,
            refresh_token = refreshToken
        };
        var resp = await _http.PostAsJsonAsync($"{_oauthBase}{TokenPath}", payload, ct)
                              .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct)
                              .ConfigureAwait(false))!;
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~JiraOAuthClientTests"`
Expected: 3 pass.

- [ ] **Step 5: Commit**

```powershell
git add src tests
git commit -m "feat(jira): OAuth 3LO client with code exchange and refresh"
```

---

### Task 3.5: JiraApiClient with auto-refresh on 401

**Files:**
- Create: `src/TmTimeTracker/Jira/IAccessTokenSource.cs`
- Create: `src/TmTimeTracker/Jira/JiraApiClient.cs`
- Create: `tests/TmTimeTracker.Tests/Jira/JiraApiClientTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/TmTimeTracker.Tests/Jira/JiraApiClientTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TmTimeTracker.Jira;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace TmTimeTracker.Tests.Jira;

public class JiraApiClientTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Dispose();

    private JiraApiClient New(IAccessTokenSource src) =>
        new(new HttpClient(), src, NullLogger<JiraApiClient>.Instance,
            apiBaseOverride: _server.Url + "/ex/jira");

    [Fact]
    public async Task GetIssue_uses_bearer_token_and_returns_issue()
    {
        var src = new Mock<IAccessTokenSource>();
        src.Setup(s => s.GetAccessTokenAsync(It.IsAny<CancellationToken>()))
           .ReturnsAsync(("at-1", "cloud-1"));

        _server.Given(Request.Create().WithPath("/ex/jira/cloud-1/rest/api/3/issue/TM-29").UsingGet()
                        .WithHeader("Authorization", "Bearer at-1"))
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   key = "TM-29",
                   fields = new { status = new { name = "In Progress",
                       statusCategory = new { key = "indeterminate", name = "In Progress" } } }
               }));

        var issue = await New(src.Object).GetIssueAsync("TM-29", CancellationToken.None);
        issue.Key.Should().Be("TM-29");
        issue.Fields.Status.Name.Should().Be("In Progress");
    }

    [Fact]
    public async Task On_401_refresh_is_requested_and_request_retried()
    {
        var src = new Mock<IAccessTokenSource>();
        src.SetupSequence(s => s.GetAccessTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(("stale", "cloud-1"))
            .ReturnsAsync(("fresh", "cloud-1"));
        src.Setup(s => s.ForceRefreshAsync(It.IsAny<CancellationToken>()))
           .Returns(Task.CompletedTask);

        _server.Given(Request.Create().WithPath("/ex/jira/cloud-1/rest/api/3/issue/TM-29")
                        .WithHeader("Authorization", "Bearer stale"))
               .RespondWith(Response.Create().WithStatusCode(401));
        _server.Given(Request.Create().WithPath("/ex/jira/cloud-1/rest/api/3/issue/TM-29")
                        .WithHeader("Authorization", "Bearer fresh"))
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
               {
                   key = "TM-29",
                   fields = new { status = new { name = "Review",
                       statusCategory = new { key = "indeterminate", name = "Review" } } }
               }));

        var issue = await New(src.Object).GetIssueAsync("TM-29", CancellationToken.None);
        issue.Fields.Status.Name.Should().Be("Review");
        src.Verify(s => s.ForceRefreshAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PostWorklog_returns_worklog_id()
    {
        var src = new Mock<IAccessTokenSource>();
        src.Setup(s => s.GetAccessTokenAsync(It.IsAny<CancellationToken>()))
           .ReturnsAsync(("at-1", "cloud-1"));

        _server.Given(Request.Create().WithPath("/ex/jira/cloud-1/rest/api/3/issue/TM-29/worklog")
                                       .UsingPost())
               .RespondWith(Response.Create().WithStatusCode(201)
                   .WithBodyAsJson(new { id = "10001" }));

        var req = new WorklogRequest(
            TimeSpentSeconds: 1800,
            StartedIso: "2026-05-25T09:00:00.000+0300",
            Comment: new WorklogComment("doc", 1, new[] {
                new WorklogContent("paragraph", new[] { new WorklogTextNode("text", "hi") }) }));
        var resp = await New(src.Object).PostWorklogAsync("TM-29", req, CancellationToken.None);
        resp.Id.Should().Be("10001");
    }
}
```

- [ ] **Step 2: Run and verify fail**

Run: `dotnet test --filter "FullyQualifiedName~JiraApiClientTests"`
Expected: compile errors.

- [ ] **Step 3: Implement IAccessTokenSource**

Create `src/TmTimeTracker/Jira/IAccessTokenSource.cs`:

```csharp
namespace TmTimeTracker.Jira;

public interface IAccessTokenSource
{
    /// <summary>Returns (accessToken, cloudId). May trigger silent refresh internally.</summary>
    Task<(string AccessToken, string CloudId)> GetAccessTokenAsync(CancellationToken ct);

    /// <summary>Forces a refresh on the next GetAccessToken call (used after a 401).</summary>
    Task ForceRefreshAsync(CancellationToken ct);
}
```

- [ ] **Step 4: Implement JiraApiClient**

Create `src/TmTimeTracker/Jira/JiraApiClient.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace TmTimeTracker.Jira;

public sealed class JiraApiClient
{
    private const string DefaultApiBase = "https://api.atlassian.com/ex/jira";
    private readonly HttpClient _http;
    private readonly IAccessTokenSource _tokens;
    private readonly ILogger<JiraApiClient> _log;
    private readonly string _apiBase;

    public JiraApiClient(HttpClient http, IAccessTokenSource tokens, ILogger<JiraApiClient> log,
        string? apiBaseOverride = null)
    {
        _http = http; _tokens = tokens; _log = log;
        _apiBase = apiBaseOverride ?? DefaultApiBase;
    }

    public async Task<Issue> GetIssueAsync(string key, CancellationToken ct)
    {
        var (resp, _) = await SendAsync(HttpMethod.Get,
            cloudId => $"{_apiBase}/{cloudId}/rest/api/3/issue/{key}?fields=status",
            content: null, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<Issue>(cancellationToken: ct).ConfigureAwait(false))!;
    }

    public async Task<WorklogResponse> PostWorklogAsync(string key, WorklogRequest body, CancellationToken ct)
    {
        var (resp, _) = await SendAsync(HttpMethod.Post,
            cloudId => $"{_apiBase}/{cloudId}/rest/api/3/issue/{key}/worklog",
            content: JsonContent.Create(body), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<WorklogResponse>(cancellationToken: ct).ConfigureAwait(false))!;
    }

    private async Task<(HttpResponseMessage Response, string CloudId)> SendAsync(
        HttpMethod method, Func<string, string> urlBuilder, HttpContent? content, CancellationToken ct)
    {
        async Task<(HttpResponseMessage, string)> One()
        {
            var (token, cloud) = await _tokens.GetAccessTokenAsync(ct).ConfigureAwait(false);
            var req = new HttpRequestMessage(method, urlBuilder(cloud));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (content is not null) req.Content = content;
            var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            return (resp, cloud);
        }

        var first = await One().ConfigureAwait(false);
        if (first.Item1.StatusCode != HttpStatusCode.Unauthorized) return first;

        _log.LogWarning("Jira 401 on {Method} — forcing token refresh and retrying once", method);
        first.Item1.Dispose();
        await _tokens.ForceRefreshAsync(ct).ConfigureAwait(false);
        return await One().ConfigureAwait(false);
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~JiraApiClientTests"`
Expected: 3 pass.

- [ ] **Step 6: Commit**

```powershell
git add src tests
git commit -m "feat(jira): API client with bearer auth, auto-refresh on 401, and worklog POST"
```

---

### Task 3.6: OAuthCoordinator (the IAccessTokenSource impl)

**Files:**
- Create: `src/TmTimeTracker/Services/OAuthCoordinator.cs`

- [ ] **Step 1: Implement**

Create `src/TmTimeTracker/Services/OAuthCoordinator.cs`:

```csharp
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;
using TmTimeTracker.Platform;

namespace TmTimeTracker.Services;

public sealed class OAuthCoordinator : IAccessTokenSource
{
    private readonly OAuthStateRepository _state;
    private readonly ITokenProtector _protector;
    private readonly JiraOAuthClient _oauth;
    private readonly HttpClient _httpForCloud;
    private readonly ILogger<OAuthCoordinator> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _forceRefreshOnNext;

    public OAuthCoordinator(OAuthStateRepository state, ITokenProtector protector,
        JiraOAuthClient oauth, HttpClient httpForCloud, ILogger<OAuthCoordinator> log)
    {
        _state = state; _protector = protector; _oauth = oauth;
        _httpForCloud = httpForCloud; _log = log;
    }

    public bool IsAuthenticated => _state.Load() is not null;

    public async Task CompleteFirstRunAsync(string authorizationCode, CancellationToken ct)
    {
        var token = await _oauth.ExchangeCodeAsync(authorizationCode, ct).ConfigureAwait(false);
        var cloudId = await FetchCloudIdAsync(token.AccessToken, ct).ConfigureAwait(false);
        SaveTokens(token, cloudId);
        _log.LogInformation("OAuth bootstrap complete; cloudId={CloudId}", cloudId);
    }

    public async Task<(string AccessToken, string CloudId)> GetAccessTokenAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var s = _state.Load() ?? throw new InvalidOperationException("Not authenticated; run OAuth first.");
            if (_forceRefreshOnNext || s.AccessExpiresAt <= DateTime.UtcNow.AddMinutes(1))
            {
                var refresh = _protector.Unprotect(s.RefreshTokenDpapi);
                var fresh = await _oauth.RefreshAsync(refresh, ct).ConfigureAwait(false);
                SaveTokens(fresh, s.CloudId);
                _forceRefreshOnNext = false;
                return (fresh.AccessToken, s.CloudId);
            }
            var access = _protector.Unprotect(s.AccessTokenDpapi);
            return (access, s.CloudId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task ForceRefreshAsync(CancellationToken ct)
    {
        _forceRefreshOnNext = true;
        return Task.CompletedTask;
    }

    private async Task<string> FetchCloudIdAsync(string accessToken, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get,
            "https://api.atlassian.com/oauth/token/accessible-resources");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var resp = await _httpForCloud.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var resources = await resp.Content.ReadFromJsonAsync<AtlassianResource[]>(cancellationToken: ct)
                                          .ConfigureAwait(false);
        if (resources is null || resources.Length == 0)
            throw new InvalidOperationException("No accessible Atlassian sites returned by /accessible-resources.");
        return resources[0].Id;
    }

    private void SaveTokens(TokenResponse token, string cloudId) =>
        _state.Save(new OAuthState(
            CloudId: cloudId,
            AccessTokenDpapi: _protector.Protect(token.AccessToken),
            RefreshTokenDpapi: _protector.Protect(token.RefreshToken),
            AccessExpiresAt: DateTime.UtcNow.AddSeconds(token.ExpiresInSeconds),
            Scope: token.Scope));
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: success.

- [ ] **Step 3: Commit**

```powershell
git add src
git commit -m "feat(services): OAuthCoordinator with token caching, refresh, and cloud-id discovery"
```

---

### Task 3.7: JiraPollService (transition detector)

**Files:**
- Create: `src/TmTimeTracker/Services/JiraPollService.cs`

- [ ] **Step 1: Implement**

Create `src/TmTimeTracker/Services/JiraPollService.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;

namespace TmTimeTracker.Services;

public sealed class JiraPollService : BackgroundService
{
    private readonly TicketTimeRepository _tickets;
    private readonly JiraApiClient _api;
    private readonly ConfigRepository _config;
    private readonly IEventBus _bus;
    private readonly IClock _clock;
    private readonly ILogger<JiraPollService> _log;

    public JiraPollService(TicketTimeRepository tickets, JiraApiClient api,
        ConfigRepository config, IEventBus bus, IClock clock, ILogger<JiraPollService> log)
    {
        _tickets = tickets; _api = api; _config = config;
        _bus = bus; _clock = clock; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_config.Get().JiraPollIntervalSeconds);
        var transitionTo = _config.Get().TransitionToStatusName;

        using var timer = new PeriodicTimer(interval);
        do
        {
            try { await PollOnce(transitionTo, stoppingToken).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogError(ex, "Poll cycle failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async Task PollOnce(string transitionTo, CancellationToken ct)
    {
        var open = _tickets.GetAllOpen();
        foreach (var cycle in open)
        {
            ct.ThrowIfCancellationRequested();
            var issue = await _api.GetIssueAsync(cycle.TicketKey, ct).ConfigureAwait(false);
            var nowUtc = _clock.UtcNow;
            var previous = cycle.LastSeenStatus;
            _tickets.UpdateStatusSnapshot(cycle.Id, issue.Fields.Status.Name, nowUtc);

            // First-run guard (per spec 9): seed last_seen_status on first poll only.
            if (previous is null) continue;

            // Trigger on transition INTO transitionTo from any other status.
            if (issue.Fields.Status.Name == transitionTo && previous != transitionTo)
            {
                await _bus.PublishAsync(
                    new JiraStatusTransition(cycle.TicketKey, previous, issue.Fields.Status.Name, nowUtc),
                    ct).ConfigureAwait(false);
            }
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: success.

- [ ] **Step 3: Commit**

```powershell
git add src
git commit -m "feat(services): JiraPollService with first-run seed and transition emission"
```

---

### Task 3.8: WorklogDescriptionBuilder (TDD)

**Files:**
- Create: `src/TmTimeTracker/Logic/WorklogDescriptionBuilder.cs`
- Create: `tests/TmTimeTracker.Tests/Logic/WorklogDescriptionBuilderTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/TmTimeTracker.Tests/Logic/WorklogDescriptionBuilderTests.cs`:

```csharp
using FluentAssertions;
using TmTimeTracker.Data;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class WorklogDescriptionBuilderTests
{
    private static StoredRememberEntry E(string time, string date, string body) =>
        new(0, "TM-29", time, date, body, "x.md", null);

    [Fact]
    public void Joins_entries_as_bullets_with_time_prefix()
    {
        var entries = new[]
        {
            E("09:00", "2026-05-25", "first body"),
            E("10:15", "2026-05-25", "second body line 1\nsecond body line 2"),
        };
        var result = WorklogDescriptionBuilder.Build(entries);
        result.Should().Be(
            "- [09:00] first body\n" +
            "- [10:15] second body line 1\n  second body line 2");
    }

    [Fact]
    public void Empty_entries_produces_default_text()
    {
        WorklogDescriptionBuilder.Build(Array.Empty<StoredRememberEntry>())
            .Should().Be("(no .remember/ entries captured)");
    }

    [Fact]
    public void Sorts_by_date_then_time()
    {
        var entries = new[]
        {
            E("10:15", "2026-05-25", "later"),
            E("09:00", "2026-05-25", "earlier"),
            E("17:00", "2026-05-24", "yesterday"),
        };
        var result = WorklogDescriptionBuilder.Build(entries);
        result.Should().Be(
            "- [2026-05-24 17:00] yesterday\n" +
            "- [09:00] earlier\n" +
            "- [10:15] later");
    }

    [Fact]
    public void Truncates_with_marker_when_over_byte_limit()
    {
        var hugeBody = new string('x', 35_000);
        var entries = new[] { E("09:00", "2026-05-25", hugeBody) };
        var result = WorklogDescriptionBuilder.Build(entries);
        result.Length.Should().BeLessThan(33_000);
        result.Should().EndWith("…(truncated)");
    }
}
```

- [ ] **Step 2: Run and verify fail**

Run: `dotnet test --filter "FullyQualifiedName~WorklogDescriptionBuilderTests"`
Expected: compile failures.

- [ ] **Step 3: Implement**

Create `src/TmTimeTracker/Logic/WorklogDescriptionBuilder.cs`:

```csharp
using System.Text;
using TmTimeTracker.Data;

namespace TmTimeTracker.Logic;

public static class WorklogDescriptionBuilder
{
    private const int MaxBytes = 32_000;  // conservative under Jira's worklog comment limit.
    private const string TruncMarker = "…(truncated)";

    public static string Build(IReadOnlyList<StoredRememberEntry> entries)
    {
        if (entries.Count == 0) return "(no .remember/ entries captured)";

        var dateGroups = entries
            .GroupBy(e => e.EntryDate)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        var today = dateGroups[^1].Key;

        var sb = new StringBuilder();
        foreach (var group in dateGroups)
        {
            var sorted = group.OrderBy(e => e.TimestampLocal, StringComparer.Ordinal);
            var prefixDate = group.Key != today;
            foreach (var e in sorted)
            {
                if (sb.Length > 0) sb.Append('\n');
                var label = prefixDate ? $"{group.Key} {e.TimestampLocal}" : e.TimestampLocal;
                sb.Append("- [").Append(label).Append("] ");
                var bodyLines = e.Body.Split('\n');
                for (var i = 0; i < bodyLines.Length; i++)
                {
                    if (i > 0) sb.Append("\n  ");
                    sb.Append(bodyLines[i]);
                }
            }
        }

        return Truncate(sb.ToString());
    }

    private static string Truncate(string s)
    {
        var bytes = Encoding.UTF8.GetByteCount(s);
        if (bytes <= MaxBytes) return s;
        var keepBytes = MaxBytes - Encoding.UTF8.GetByteCount(TruncMarker);
        // Trim by character count first (close enough), then verify.
        var approxChars = (int)((double)keepBytes / bytes * s.Length);
        var trimmed = s.Substring(0, Math.Min(approxChars, s.Length));
        while (Encoding.UTF8.GetByteCount(trimmed) + Encoding.UTF8.GetByteCount(TruncMarker) > MaxBytes)
            trimmed = trimmed.Substring(0, trimmed.Length - 1);
        return trimmed + TruncMarker;
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~WorklogDescriptionBuilderTests"`
Expected: 4 pass.

- [ ] **Step 5: Commit**

```powershell
git add src tests
git commit -m "feat(logic): WorklogDescriptionBuilder with date-grouping and UTF8 truncation"
```

---

### Task 3.9: Phase-3 CLI smoke modes (`--login` and `--probe-jira`)

**Files:**
- Modify: `src/TmTimeTracker/Program.cs`
- Modify: `src/TmTimeTracker/HostingExtensions.cs`

- [ ] **Step 1: Extend HostingExtensions with Jira services**

Add to `src/TmTimeTracker/HostingExtensions.cs`:

```csharp
    public static IHostBuilder AddJiraServices(this IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddHttpClient();
            services.AddSingleton<ITokenProtector, DpapiTokenProtector>();
            services.AddSingleton(sp =>
            {
                var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("jira-oauth");
                var secrets = sp.GetRequiredService<AppSecrets>();
                return new JiraOAuthClient(http, secrets.Atlassian);
            });
            services.AddSingleton<OAuthCoordinator>();
            services.AddSingleton<IAccessTokenSource>(sp => sp.GetRequiredService<OAuthCoordinator>());
            services.AddSingleton(sp =>
            {
                var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("jira-api");
                var tokens = sp.GetRequiredService<IAccessTokenSource>();
                var log = sp.GetRequiredService<ILogger<JiraApiClient>>();
                return new JiraApiClient(http, tokens, log);
            });
            services.AddSingleton<LocalCallbackListener>();
        });
        return builder;
    }
```

Add the required `using` directives at the top of the file:

```csharp
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Jira;
```

- [ ] **Step 2: Replace Program.cs**

Replace `src/TmTimeTracker/Program.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker;
using TmTimeTracker.Configuration;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;
using TmTimeTracker.Services;

var hostBuilder = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration(c => c.AddJsonFile("secrets.json", optional: true, reloadOnChange: false))
    .ConfigureServices((ctx, services) =>
    {
        var secrets = ctx.Configuration.Get<AppSecrets>() ?? new AppSecrets();
        services.AddSingleton(secrets);
    })
    .ConfigureLogging(b => b.ClearProviders().AddSimpleConsole())
    .AddTmTimeTrackerCore()
    .AddJiraServices();

if (args.Length >= 1 && args[0] == "--login")
{
    var host = hostBuilder.Build();
    InitDb(host);
    await RunLogin(host);
    return;
}

if (args.Length == 2 && args[0] == "--probe-jira")
{
    var host = hostBuilder.Build();
    InitDb(host);
    await RunProbe(host, args[1]);
    return;
}

if (args.Length == 1 && args[0] == "--smoke-activity")
{
    var host = hostBuilder.AddActivityServices().Build();
    InitDb(host);
    SeedConfigIfMissing(host);
    await RunSmokeActivity(host);
    return;
}

Console.WriteLine("Usage: --login | --probe-jira TM-NN | --smoke-activity");

static void InitDb(IHost host)
{
    using var scope = host.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().EnsureCreated();
}

static void SeedConfigIfMissing(IHost host)
{
    using var scope = host.Services.CreateScope();
    var cfg = scope.ServiceProvider.GetRequiredService<ConfigRepository>();
    cfg.SetIfMissing(new AppConfig(
        IdleThresholdSeconds: 600,
        JiraPollIntervalSeconds: 90,
        RepoPath: Environment.CurrentDirectory,
        RememberPath: Path.Combine(Environment.CurrentDirectory, ".remember"),
        InProgressStatusName: "In Progress",
        TransitionToStatusName: "Review"));
}

static async Task RunLogin(IHost host)
{
    var coord = host.Services.GetRequiredService<OAuthCoordinator>();
    var oauth = host.Services.GetRequiredService<JiraOAuthClient>();
    var listener = host.Services.GetRequiredService<LocalCallbackListener>();
    var secrets = host.Services.GetRequiredService<AppSecrets>();

    var state = Guid.NewGuid().ToString("N");
    var url = oauth.BuildAuthorizationUrl(state);
    Console.WriteLine("Opening browser to:");
    Console.WriteLine(url);
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = url,
        UseShellExecute = true
    });

    var callbackPrefix = secrets.Atlassian.RedirectUri.TrimEnd('/') + "/";
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    var cb = await listener.ListenOnceAsync(callbackPrefix.Replace("/callback/", "/"), cts.Token);
    if (cb.State != state) { Console.WriteLine("State mismatch — aborting."); return; }
    await coord.CompleteFirstRunAsync(cb.Code, cts.Token);
    Console.WriteLine("Login complete. Tokens stored.");
}

static async Task RunProbe(IHost host, string ticketKey)
{
    var api = host.Services.GetRequiredService<JiraApiClient>();
    var issue = await api.GetIssueAsync(ticketKey, CancellationToken.None);
    Console.WriteLine($"{issue.Key}: {issue.Fields.Status.Name} ({issue.Fields.Status.Category.Key})");
}

static async Task RunSmokeActivity(IHost host)
{
    var bus = host.Services.GetRequiredService<IEventBus>();
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    _ = Task.Run(async () =>
    {
        await foreach (var evt in bus.Subscribe(cts.Token))
            Console.WriteLine($"[{evt.AtUtc:HH:mm:ss}] {evt.GetType().Name}: {evt}");
    });
    await host.RunAsync(cts.Token);
}
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: success.

- [ ] **Step 4: Manual smoke — register OAuth app first**

This is a one-time real-world step. Open `https://developer.atlassian.com/console/myapps/`, create an OAuth 2.0 (3LO) app:
- Name: `TmTimeTracker (lefteris-personal)`
- Callback URL: `http://localhost:53682/callback`
- Permissions → Jira API: add `read:jira-work`, `write:jira-work`, `offline_access`

Copy `Client ID` and `Secret` into `secrets.json` at solution root (mirror `secrets.example.json`).

- [ ] **Step 5: Manual smoke — login**

Run: `dotnet run --project src\TmTimeTracker -- --login`
Expected: browser opens, you approve, browser shows "TmTimeTracker connected", console prints "Login complete."

- [ ] **Step 6: Manual smoke — probe**

Run: `dotnet run --project src\TmTimeTracker -- --probe-jira TM-29`
Expected: prints e.g. `TM-29: Review (indeterminate)`.

- [ ] **Step 7: Commit**

```powershell
git add src
git commit -m "feat(app): --login and --probe-jira CLI smoke modes for OAuth and REST"
```

---

### Task 3.10: Wire JiraPollService into a `--smoke-poll` mode

**Files:** modify `src/TmTimeTracker/HostingExtensions.cs` + `src/TmTimeTracker/Program.cs`

- [ ] **Step 1: Add poll services builder**

Add to `HostingExtensions.cs`:

```csharp
    public static IHostBuilder AddPollServices(this IHostBuilder builder)
    {
        builder.ConfigureServices(services => services.AddHostedService<JiraPollService>());
        return builder;
    }
```

- [ ] **Step 2: Add CLI arm in Program**

In `Program.cs`, replace the existing routing block with one additional arm:

```csharp
if (args.Length == 1 && args[0] == "--smoke-poll")
{
    var host = hostBuilder.AddActivityServices().AddPollServices().Build();
    InitDb(host); SeedConfigIfMissing(host);
    await RunSmokeActivity(host);   // same console event printer reused
    return;
}
```

- [ ] **Step 3: Manual smoke**

Run: `dotnet run --project src\TmTimeTracker -- --smoke-poll`

Expected: with `TM-29` on the current branch and Jira status currently "In Progress", change it in the Jira web UI to "Review". Within 90 seconds the console prints `JiraStatusTransition: TM-29 In Progress -> Review`.

- [ ] **Step 4: Commit**

```powershell
git add src
git commit -m "feat(app): --smoke-poll wires JiraPollService end-to-end"
```

---

### Phase 3 checkpoint

- [ ] `dotnet test` — all tests pass
- [ ] OAuth login flow works against real Atlassian
- [ ] Status transition detected end-to-end in console
- [ ] **STOP** for review before Phase 4 (tray UI).

---

# Phase 4: Tray UI, Edit Form, Auto-start

Goal of phase: full end-to-end happy path with a real tray icon + balloon + form, plus single-instance guard and auto-start registration.

### Task 4.1: SingleInstanceGuard + AutoStartRegistrar

**Files:**
- Create: `src/TmTimeTracker/Platform/SingleInstanceGuard.cs`
- Create: `src/TmTimeTracker/Platform/AutoStartRegistrar.cs`

- [ ] **Step 1: SingleInstanceGuard**

Create `src/TmTimeTracker/Platform/SingleInstanceGuard.cs`:

```csharp
namespace TmTimeTracker.Platform;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    public bool IsPrimary { get; }

    public SingleInstanceGuard(string name = "Global\\TmTimeTracker.SingleInstance.v1")
    {
        _mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        IsPrimary = createdNew;
    }

    public void Dispose()
    {
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
```

- [ ] **Step 2: AutoStartRegistrar**

Create `src/TmTimeTracker/Platform/AutoStartRegistrar.cs`:

```csharp
using Microsoft.Win32;

namespace TmTimeTracker.Platform;

public static class AutoStartRegistrar
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TmTimeTracker";

    public static bool IsRegistered()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key, writable: false);
        return k?.GetValue(ValueName) is string;
    }

    public static void Register(string exePath)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key, writable: true)
                   ?? Registry.CurrentUser.CreateSubKey(Key);
        k!.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
    }

    public static void Unregister()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key, writable: true);
        k?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: success.

- [ ] **Step 4: Commit**

```powershell
git add src
git commit -m "feat(platform): single-instance guard and autostart registry helper"
```

---

### Task 4.2: WorklogEditForm (WinForms)

**Files:**
- Create: `src/TmTimeTracker/UI/WorklogEditForm.cs`

- [ ] **Step 1: Implement**

Create `src/TmTimeTracker/UI/WorklogEditForm.cs`:

```csharp
using System.ComponentModel;

namespace TmTimeTracker.UI;

public sealed class WorklogEditForm : Form
{
    public string TicketKey { get; }
    public int ObservedMinutes { get; }
    public int SubmittedMinutes { get; private set; }
    public string Description { get; private set; }
    public bool Submitted { get; private set; }

    private readonly NumericUpDown _minutes;
    private readonly TextBox _description;
    private readonly Label _warn;

    public WorklogEditForm(string ticketKey, int observedMinutes, string initialDescription)
    {
        TicketKey = ticketKey;
        ObservedMinutes = observedMinutes;
        SubmittedMinutes = observedMinutes;
        Description = initialDescription;

        Text = $"Log time on {ticketKey}";
        Width = 600; Height = 480;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;

        Controls.Add(new Label { Text = $"Ticket: {ticketKey}", Top = 10, Left = 10, AutoSize = true });
        Controls.Add(new Label { Text = "Minutes:", Top = 40, Left = 10, AutoSize = true });

        _minutes = new NumericUpDown
        {
            Top = 38, Left = 100, Width = 80,
            Minimum = 1, Maximum = observedMinutes, Value = observedMinutes
        };
        _minutes.ValueChanged += (_, _) => UpdateWarning();
        Controls.Add(_minutes);

        Controls.Add(new Label
        { Text = $"(observed {observedMinutes}; you may reduce, not increase)",
          Top = 40, Left = 200, AutoSize = true, ForeColor = SystemColors.GrayText });

        Controls.Add(new Label { Text = "Description:", Top = 70, Left = 10, AutoSize = true });
        _description = new TextBox
        {
            Top = 90, Left = 10, Width = 560, Height = 280,
            Multiline = true, ScrollBars = ScrollBars.Vertical,
            Text = initialDescription
        };
        Controls.Add(_description);

        _warn = new Label { Top = 380, Left = 10, AutoSize = true, ForeColor = Color.DarkRed };
        Controls.Add(_warn);

        var submit = new Button { Text = "Submit", Top = 405, Left = 380, Width = 90 };
        submit.Click += (_, _) =>
        {
            SubmittedMinutes = (int)_minutes.Value;
            Description = _description.Text;
            Submitted = true;
            DialogResult = DialogResult.OK;
            Close();
        };
        var later = new Button { Text = "Edit later", Top = 405, Left = 480, Width = 90 };
        later.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        Controls.Add(submit);
        Controls.Add(later);
        AcceptButton = submit;
        CancelButton = later;
    }

    private void UpdateWarning()
    {
        _warn.Text = (int)_minutes.Value < ObservedMinutes
            ? $"Reducing observed time by {ObservedMinutes - (int)_minutes.Value} min."
            : string.Empty;
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: success.

- [ ] **Step 3: Commit**

```powershell
git add src
git commit -m "feat(ui): WorklogEditForm with minutes-down validation"
```

---

### Task 4.3: TrayIconHost + BalloonNotifier

**Files:**
- Create: `src/TmTimeTracker/UI/TrayIconHost.cs`
- Create: `src/TmTimeTracker/UI/BalloonNotifier.cs`

- [ ] **Step 1: BalloonNotifier**

Create `src/TmTimeTracker/UI/BalloonNotifier.cs`:

```csharp
namespace TmTimeTracker.UI;

public interface IBalloonNotifier
{
    void Show(string title, string body, ToolTipIcon icon = ToolTipIcon.Info);
}

public sealed class BalloonNotifier : IBalloonNotifier
{
    private readonly NotifyIcon _icon;
    public BalloonNotifier(NotifyIcon icon) => _icon = icon;
    public void Show(string title, string body, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = body;
        _icon.BalloonTipIcon = icon;
        _icon.ShowBalloonTip(8_000);
    }
}
```

- [ ] **Step 2: TrayIconHost**

Create `src/TmTimeTracker/UI/TrayIconHost.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;
using TmTimeTracker.Logic;
using TmTimeTracker.Platform;
using TmTimeTracker.Services;

namespace TmTimeTracker.UI;

public sealed class TrayIconHost : BackgroundService
{
    private readonly IEventBus _bus;
    private readonly IServiceProvider _sp;
    private readonly ILogger<TrayIconHost> _log;
    private readonly IHostApplicationLifetime _lifetime;
    private NotifyIcon? _icon;
    private SynchronizationContext? _uiCtx;

    public TrayIconHost(IEventBus bus, IServiceProvider sp, ILogger<TrayIconHost> log,
        IHostApplicationLifetime lifetime)
    {
        _bus = bus; _sp = sp; _log = log; _lifetime = lifetime;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run the WinForms message loop on this thread.
        var thread = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            _uiCtx = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(_uiCtx);

            _icon = BuildIcon();
            Application.ApplicationExit += (_, _) => _icon.Dispose();
            Application.Run();
        });
        thread.IsBackground = false;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // Subscribe on a background task.
        _ = Task.Run(async () =>
        {
            await foreach (var evt in _bus.Subscribe(stoppingToken).ConfigureAwait(false))
            {
                if (evt is JiraStatusTransition t)
                    OnUiThread(() => PromptForWorklog(t.TicketKey));
            }
        }, stoppingToken);

        stoppingToken.Register(() => OnUiThread(Application.Exit));
        return Task.CompletedTask;
    }

    private void OnUiThread(Action action)
    {
        if (_uiCtx is null) action();
        else _uiCtx.Post(_ => action(), null);
    }

    private NotifyIcon BuildIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Pending worklogs…", null, (_, _) => ShowPending());
        menu.Items.Add("Open log folder", null, (_, _) =>
            System.Diagnostics.Process.Start("explorer.exe", Configuration.AppPaths.LogsDir));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => _lifetime.StopApplication());

        var icon = new NotifyIcon
        {
            Visible = true,
            Text = "TmTimeTracker",
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu
        };
        return icon;
    }

    private void ShowPending()
    {
        using var scope = _sp.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<TicketTimeRepository>();
        var rows = repo.GetAllOpen();
        if (rows.Count == 0)
        {
            MessageBox.Show("No pending worklogs.", "TmTimeTracker");
            return;
        }
        var menu = new ContextMenuStrip();
        foreach (var r in rows)
        {
            menu.Items.Add($"{r.TicketKey} — {r.MinutesActive} min", null,
                (_, _) => PromptForWorklog(r.TicketKey));
        }
        menu.Show(Cursor.Position);
    }

    private void PromptForWorklog(string ticketKey)
    {
        using var scope = _sp.CreateScope();
        var tickets = scope.ServiceProvider.GetRequiredService<TicketTimeRepository>();
        var entries = scope.ServiceProvider.GetRequiredService<RememberEntryRepository>();
        var api = scope.ServiceProvider.GetRequiredService<JiraApiClient>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var cycle = tickets.GetAllOpen().FirstOrDefault(c => c.TicketKey == ticketKey);
        if (cycle is null) return;

        var unconsumed = entries.GetUnconsumedForTicket(ticketKey);
        var description = WorklogDescriptionBuilder.Build(unconsumed);

        using var form = new WorklogEditForm(ticketKey, cycle.MinutesActive, description);
        if (form.ShowDialog() != DialogResult.OK) return;

        try
        {
            var req = BuildAdfRequest(form.SubmittedMinutes, form.Description, clock.LocalNow);
            var resp = api.PostWorklogAsync(ticketKey, req, CancellationToken.None)
                          .GetAwaiter().GetResult();
            tickets.MarkSubmitted(cycle.Id, resp.Id, form.SubmittedMinutes, clock.UtcNow);
            entries.TagConsumed(unconsumed.Select(e => e.Id).ToList(), cycle.Id);
            _log.LogInformation("Worklog {Id} posted to {Ticket}", resp.Id, ticketKey);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Worklog submit failed for {Ticket}", ticketKey);
            MessageBox.Show($"Submit failed: {ex.Message}", "TmTimeTracker",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static WorklogRequest BuildAdfRequest(int minutes, string description, DateTimeOffset localNow)
    {
        // Atlassian requires this Atlassian Document Format wrapping for worklog comments in v3.
        var content = new WorklogContent("paragraph",
            new[] { new WorklogTextNode("text", description) });
        var comment = new WorklogComment("doc", 1, new[] { content });
        // Jira expects "yyyy-MM-ddTHH:mm:ss.fffzzzz" with no colon in zzzz.
        var started = localNow.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz").Replace(":", "", 9, 1)
                              .TrimEnd();
        // The hack above strips the colon from the offset (e.g. +03:00 -> +0300) — required by Jira.
        return new WorklogRequest(TimeSpentSeconds: minutes * 60, StartedIso: started, Comment: comment);
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: success.

- [ ] **Step 4: Commit**

```powershell
git add src
git commit -m "feat(ui): tray icon host with balloon-driven worklog prompt"
```

---

### Task 4.4: Final Program.cs (Generic Host + tray, no CLI switches)

**Files:**
- Modify: `src/TmTimeTracker/Program.cs`
- Modify: `src/TmTimeTracker/HostingExtensions.cs`

- [ ] **Step 1: Add tray to hosting extensions**

Add to `HostingExtensions.cs`:

```csharp
    public static IHostBuilder AddTrayUI(this IHostBuilder builder)
    {
        builder.ConfigureServices(services => services.AddHostedService<TrayIconHost>());
        return builder;
    }
```

Update the using block at the top of the file to include `TmTimeTracker.UI;`.

- [ ] **Step 2: Replace Program.cs**

Replace `src/TmTimeTracker/Program.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using TmTimeTracker;
using TmTimeTracker.Configuration;
using TmTimeTracker.Data;
using TmTimeTracker.Jira;
using TmTimeTracker.Platform;
using TmTimeTracker.Services;

AppPaths.EnsureExists();

using var guard = new SingleInstanceGuard();
if (!guard.IsPrimary)
{
    // Quietly exit; an existing instance is already running.
    return;
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(
        Path.Combine(AppPaths.LogsDir, "daemon-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30)
    .CreateLogger();

try
{
    var hostBuilder = Host.CreateDefaultBuilder(args)
        .UseSerilog()
        .ConfigureAppConfiguration(c => c.AddJsonFile("secrets.json", optional: true, reloadOnChange: false))
        .ConfigureServices((ctx, services) =>
        {
            var secrets = ctx.Configuration.Get<AppSecrets>() ?? new AppSecrets();
            services.AddSingleton(secrets);
        })
        .AddTmTimeTrackerCore()
        .AddJiraServices()
        .AddActivityServices()
        .AddPollServices()
        .AddTrayUI();

    var host = hostBuilder.Build();

    using (var scope = host.Services.CreateScope())
    {
        scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().EnsureCreated();
        var cfg = scope.ServiceProvider.GetRequiredService<ConfigRepository>();
        cfg.SetIfMissing(new AppConfig(
            IdleThresholdSeconds: 600,
            JiraPollIntervalSeconds: 90,
            RepoPath: @"c:\projects\training-manager",
            RememberPath: @"c:\projects\training-manager\.remember",
            InProgressStatusName: "In Progress",
            TransitionToStatusName: "Review"));
    }

    AutoStartRegistrar.Register(System.Reflection.Assembly.GetExecutingAssembly().Location);

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "TmTimeTracker crashed");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: success.

- [ ] **Step 4: Manual smoke (full daemon)**

Run: `dotnet run --project src\TmTimeTracker`
Expected: tray icon appears (look in the system tray); right-click shows menu. Move TM-29 in Jira UI from "In Progress" to "Review" → within 90s an edit form pops; click Submit; verify worklog appears in Jira.

- [ ] **Step 5: Commit**

```powershell
git add src
git commit -m "feat(app): final Program.cs wiring all services with Serilog, autostart, tray UI"
```

---

### Task 4.5: MaintenanceService (minute-sample pruning)

**Files:**
- Create: `src/TmTimeTracker/Services/MaintenanceService.cs`
- Modify: `src/TmTimeTracker/HostingExtensions.cs`

- [ ] **Step 1: Implement**

Create `src/TmTimeTracker/Services/MaintenanceService.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmTimeTracker.Data;

namespace TmTimeTracker.Services;

public sealed class MaintenanceService : BackgroundService
{
    private readonly MinuteSampleRepository _samples;
    private readonly IClock _clock;
    private readonly ILogger<MaintenanceService> _log;

    public MaintenanceService(MinuteSampleRepository samples, IClock clock, ILogger<MaintenanceService> log)
    {
        _samples = samples; _clock = clock; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            try
            {
                var cutoff = _clock.UtcNow.AddDays(-30);
                var deleted = _samples.PruneOlderThan(cutoff);
                if (deleted > 0) _log.LogInformation("Pruned {N} minute_sample rows older than {C}", deleted, cutoff);
            }
            catch (Exception ex) { _log.LogError(ex, "Maintenance failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
```

- [ ] **Step 2: Register**

In `HostingExtensions.cs`, inside `AddActivityServices`, append `services.AddHostedService<MaintenanceService>();`.

- [ ] **Step 3: Build + commit**

Run: `dotnet build`

```powershell
git add src
git commit -m "feat(services): daily maintenance to prune minute_sample older than 30d"
```

---

### Phase 4 checkpoint

- [ ] Full daemon runs end-to-end with tray + balloon + form
- [ ] Worklog posted to Jira after Submit click
- [ ] Single-instance guard works (run twice — second instance exits silently)
- [ ] Auto-start registry key present (`reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v TmTimeTracker`)
- [ ] **STOP** for review before Phase 5.

---

# Phase 5: CI + Packaging + Smoke

### Task 5.1: GitHub Actions CI

**Files:**
- Create: `.github/workflows/build.yml`

- [ ] **Step 1: Workflow file**

Create `.github/workflows/build.yml`:

```yaml
name: build

on:
  push:
    branches: [ main, master ]
  pull_request:
    branches: [ main, master ]

jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Restore
        run: dotnet restore

      - name: Build
        run: dotnet build --configuration Release --no-restore

      - name: Test
        run: dotnet test --configuration Release --no-build --logger "trx;LogFileName=test-results.trx"

      - name: Upload test results
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: test-results
          path: '**/test-results.trx'

      - name: Publish self-contained exe
        run: >
          dotnet publish src/TmTimeTracker/TmTimeTracker.csproj
          -c Release -r win-x64 --self-contained
          /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
          -o ./publish

      - name: Upload artifact
        uses: actions/upload-artifact@v4
        with:
          name: TmTimeTracker-win-x64
          path: ./publish/TmTimeTracker.exe
```

- [ ] **Step 2: Commit**

```powershell
git add .github
git commit -m "ci: GitHub Actions build + test + self-contained publish"
```

---

### Task 5.2: Manual smoke checklist file

**Files:**
- Create: `docs/smoke-tests.md`

- [ ] **Step 1: Smoke checklist**

Create `docs/smoke-tests.md`:

```markdown
# Manual smoke tests (run before each release)

For each item, check the box only after you observed the expected outcome.

## 1. First-run consent
- [ ] Delete `%LOCALAPPDATA%\TmTimeTracker\state.db`.
- [ ] Run `TmTimeTracker.exe`.
- [ ] Tray icon appears. No balloons yet.
- [ ] Run `dotnet run --project src\TmTimeTracker -- --login` (or use a "Sign in" tray menu item if added later).
- [ ] Browser opens Atlassian consent → approve → page shows "TmTimeTracker connected".
- [ ] `state.db` now has a row in `oauth_state`.

## 2. Live status transition
- [ ] On a TM-XX branch in training-manager, work for 5+ min (verify by querying `ticket_time.minutes_active`).
- [ ] Move the ticket "In Progress" → "Review" in Jira UI.
- [ ] Within 90 seconds, the daemon prompts.
- [ ] Submit. Verify worklog visible at `https://<your-site>.atlassian.net/browse/TM-XX?focusedWorklogId=<id>`.

## 3. Lock/unlock
- [ ] Lock the workstation (Win+L) while running.
- [ ] Wait 2 minutes.
- [ ] Unlock.
- [ ] Inspect `minute_sample` rows for the 2-minute window: `is_idle=1`.

## 4. Sleep/resume
- [ ] Put laptop to sleep for 10+ minutes.
- [ ] Wake.
- [ ] No spurious minute-tick increments during sleep window (`ticket_time.minutes_active` did not jump).

## 5. Crash recovery
- [ ] Kill the daemon process mid-day.
- [ ] Restart it.
- [ ] Open `ticket_time` rows still present and submittable; no duplicate cycles.
```

- [ ] **Step 2: Commit**

```powershell
git add docs
git commit -m "docs: pre-release manual smoke-tests checklist"
```

---

### Phase 5 checkpoint

- [ ] CI passes on GitHub
- [ ] Self-contained exe artifact downloaded from CI runs standalone
- [ ] Smoke checklist completed locally
- [ ] **STOP** — v1 acceptance criteria from spec section 10 should now be checkable.

---

# Self-review notes (author's own)

Cross-checked against spec section 10 acceptance criteria:

| Spec criterion | Plan task |
|----------------|-----------|
| AC1 OAuth consent + encrypted tokens | 3.3, 3.4, 3.6 + 3.9 manual smoke |
| AC2 Auto-start on login | 4.4 (Register call in Program.cs) |
| AC3 Per-minute increments | 2.4 TimeAggregator |
| AC4 Tray balloon within 90s | 3.7 JiraPollService + 4.3 TrayIconHost |
| AC5 Edit form + worklog POST | 4.2 + 4.3 |
| AC6 No re-prompt after submit | 4.3 TrayIconHost (MarkSubmitted closes cycle) + JiraPollService only iterates open cycles |
| AC7 Tests green on CI | 5.1 workflow |
| AC8 Manual smokes 1-5 | 5.2 checklist |

Spec edge cases:
- 6.1 happy path → exercised by 4.4 manual smoke
- 6.2 ticket switching → covered by tests in 2.4
- 6.3 daemon-offline-during-transition → handled by spec §9 first-run seed + 3.7 first-poll skip; **acknowledged gap:** automated test for the multi-poll gap path not added. Future improvement.
- 6.4 reopened ticket → repository contract allows new open cycle after `submitted_at` is set; verified by `MarkSubmitted_closes_cycle_and_opens_new_one_next_time` in 1.7
- 6.5 dismissed balloon → form Cancel leaves cycle open; tray "Pending worklogs" surfaces them (4.3)
- 6.6 OAuth refresh expired → handled by `OAuthCoordinator.ForceRefreshAsync` + 401 retry in `JiraApiClient` (3.5, 3.6); end-to-end balloon prompt for re-auth not implemented in v1 (logged exception surfaces in tray menu via error MessageBox)
- 6.7 no TM prefix → `TicketKeyExtractor.Extract` returns null; `TimeAggregator` no-ops (verified in 2.4)
