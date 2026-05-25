# Multi-Repo Detection — Design Spec

**Date:** 2026-05-25
**Author:** Lefteris Tsegkos (in collaboration with Claude Code)
**Status:** Approved — implementation in progress
**Parent designs:**
- [2026-05-25-jira-time-tracker-daemon-design.md](2026-05-25-jira-time-tracker-daemon-design.md)
- [2026-05-25-tmtt-ui-design.md](2026-05-25-tmtt-ui-design.md)

---

## 1. Goal

Track time across multiple repositories simultaneously and detect which one the user is actively working in, without requiring them to manually switch contexts. The current single-repo lockup (configured `RepoPath`) is the daemon's biggest usability gap.

## 2. Non-Goals

- IDE integration via VS Code extension (deferred — design's option C).
- Per-file or per-symbol granularity (just repo-level for now).
- Auto-discovery of repos by scanning the filesystem (manual add only in v1).
- Detection of non-IDE windows (browser, Slack, etc.) — irrelevant signal.
- IDEs other than VS Code in v1 (user selected VS Code only).

## 3. Confirmed Design Decisions

| Decision | Choice |
| -------- | ------ |
| Detection approach | Hybrid: foreground-window probe (VS Code title parse) + recent-activity polling fallback |
| IDE coverage | VS Code only (Cursor, JetBrains, Visual Studio deferred) |
| Probe interval | 2 seconds |
| No-signal behavior | Sticky — keep crediting the last detected ticket (handles "user reading docs in browser") |
| `.remember/` location | Always `<repo>/.remember` (no per-repo override) |
| Repo collision (same basename) | First match wins; warn once per session |
| Repo source | Manually configured list in Settings; no auto-discovery |
| Active-repo activity window | 5 minutes — older `.git/HEAD` mtimes don't qualify as "recently active" |

## 4. Architecture

### 4.1 Detection pipeline

```text
Every 2s:
  ┌───────────────────────────┐
  │ Win32ForegroundWindowProbe│  → (processName, windowTitle)
  └─────────────┬─────────────┘
                ▼
  ┌───────────────────────────┐
  │ VsCodeWindowTitleParser   │  → folderName | null
  └─────────────┬─────────────┘
                ▼
  ┌───────────────────────────┐
  │ ActiveRepoResolver        │  ─── reads TrackedRepoRepository
  │   1. window-title match   │  ─── reads .git/HEAD mtime per repo
  │   2. recent-activity poll │  ─── reads previous resolution (sticky)
  │   3. sticky fallback      │
  └─────────────┬─────────────┘
                ▼
  ┌───────────────────────────┐
  │ git rev-parse + TicketKey │
  └─────────────┬─────────────┘
                ▼
  ┌───────────────────────────┐
  │ BranchChanged event       │  → existing TimeAggregator, dashboard etc.
  └───────────────────────────┘
```

### 4.2 Component boundaries

`ActiveRepoResolver` is pure logic — it takes the foreground probe and the tracked-repo list as dependencies and returns the active triple. Win32 + git access are pushed into thin probes that can be faked in tests.

The downstream contract — a `BranchChanged(branch, ticketKey, atUtc)` event — does not change. `TimeAggregator`, the dashboard, the JiraPollService, and the tray balloon flow remain untouched.

### 4.3 Out of scope for this iteration

- Visual Studio / JetBrains / Cursor title parsers (one per IDE).
- A "Discover repos" button that scans a parent folder.
- Per-repo `.remember/` overrides.
- Detecting which subfolder within a monorepo you're in.

## 5. Data Model

### 5.1 New table

```sql
CREATE TABLE IF NOT EXISTS tracked_repo (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    path       TEXT NOT NULL UNIQUE COLLATE NOCASE,
    sort_order INTEGER NOT NULL DEFAULT 0
);
```

`COLLATE NOCASE` on `path` because Windows filesystems are case-insensitive — prevents accidental duplicates like `C:\Projects\X` vs `c:\projects\x`.

### 5.2 Migration (idempotent, on every startup)

```text
if tracked_repo is empty AND config.repo_path is set AND that path exists on disk:
    INSERT INTO tracked_repo (path, sort_order) VALUES (config.repo_path, 0)
```

`config.repo_path` and `config.remember_path` columns are retained for backward compat but no longer consumed by the new code paths. They can be dropped in a future cleanup.

## 6. Component Responsibilities

| Component | Responsibility |
|-----------|----------------|
| `TrackedRepoRepository` | CRUD for the `tracked_repo` table; returns repos sorted by `sort_order, id` |
| `IForegroundWindowProbe` | Returns `(processName, windowTitle)` or `null` — one Win32 call boundary |
| `Win32ForegroundWindowProbe` | Calls `GetForegroundWindow` + `GetWindowThreadProcessId` + `Process.ProcessName` + `GetWindowText` |
| `VsCodeWindowTitleParser` | Pure parser: `"file.cs - my-repo - Visual Studio Code"` → `"my-repo"` |
| `ActiveRepoResolver` | Combines probe + tracked repos + git lookup; emits change events only on transitions; remembers last resolution for sticky behavior |
| `BranchWatcher` (modified) | Drop single-repo loop; periodically call `ActiveRepoResolver.Resolve()` and publish `BranchChanged` on change |
| `RememberWatcher` (modified) | Watch all `<repo>/.remember/` paths; one `FileSystemWatcher` per tracked repo; reload on tracked-repo changes |
| `PathsPage` (modified) | Replace single Repo path textbox with ListBox + Add (folder browser) / Remove. Drop Remember path field |

## 7. Detection Algorithm

```text
ActiveRepoResolver.Resolve():
    repos = trackedRepoRepo.GetAll()
    if repos.Empty:
        emit if changed: (null, null, null)
        return

    // Tier 1: VS Code window title
    probe = foregroundProbe.Probe()
    if probe is not null AND probe.ProcessName matches /^Code(\.exe)?$/i:
        folder = vsCodeParser.Parse(probe.Title)
        if folder is not null:
            match = repos.FirstOrDefault(r => Path.GetFileName(r.Path).Equals(folder, OrdinalIgnoreCase))
            if match:
                return Lookup(match)

    // Tier 2: recent .git/HEAD activity
    candidates = repos
        .Select(r => (r, mtime: TryReadHeadMtime(r.Path)))
        .Where(x => x.mtime.HasValue AND x.mtime.Value > clock.UtcNow - 5min)
        .OrderByDescending(x => x.mtime.Value)
    if candidates.Any():
        return Lookup(candidates.First().r)

    // Tier 3: sticky — return previous resolution
    return _lastResolution
```

`Lookup(repo)` runs `git rev-parse --abbrev-ref HEAD` via the existing `IGitBranchProbe`, extracts the ticket via `TicketKeyExtractor`, and returns `(repoPath, branch, ticketKey)`.

`Resolve()` is called every 2s by `BranchWatcher`. When the result differs from `_lastResolution` in either repo or ticket, `BranchChanged` is published.

## 8. Settings UI

`PathsPage` (and the Settings tab of the same content):

```
Tracked repos:                                  [Add repo…] [Remove]
┌─────────────────────────────────────────────────────────────┐
│ c:\projects\training-manager                                │
│ c:\projects\another-repo                                    │
│ c:\projects\third-thing                                     │
└─────────────────────────────────────────────────────────────┘

Idle threshold (min): [10]    Jira poll interval (sec): [90]

"In Progress" status: [In Progress]      Target: [Review]
```

- "Add repo…" opens a `FolderBrowserDialog`. Selected folder must contain a `.git` folder; else show an error MessageBox and don't add.
- "Remove" deletes the selected row (no confirmation — easy to re-add).
- Order is insertion order; no drag-to-reorder in v1.

Validation for the Finish/Save button: must have at least 1 tracked repo.

## 9. Error Handling

| Failure | Response |
|---------|----------|
| `GetForegroundWindow` returns 0 / title empty | Treat as "no IDE match", proceed to polling fallback |
| Process name lookup fails (access denied, race) | Same — fall through to polling |
| Tracked repo path no longer exists on disk | Polling skips it; log warning once per path per session; entry stays in DB until user removes |
| Two tracked repos with same basename | First wins by `sort_order, id`; log warning once per session |
| `git rev-parse` fails (corrupted repo, detached HEAD) | Treat as no current branch → ticket = null → `BranchChanged(branch=null, ticketKey=null)` (downstream stops accumulation) |
| All repos removed via Settings | Resolver emits `BranchChanged(null, null)`; daemon stops accumulating |
| `FileSystemWatcher` setup fails for a repo | Log warning, continue with other repos |
| User adds the same path twice | UNIQUE constraint on `tracked_repo.path` (NOCASE) rejects silently; UI dedupes before insert |

## 10. Testing

| Component | Tests |
|-----------|-------|
| `TrackedRepoRepository` | Round-trip; dedupe on insert (case-insensitive); ordered retrieval; delete-by-path; case-insensitive lookups |
| `VsCodeWindowTitleParser` | `"foo.cs - my-repo - Visual Studio Code"` → `"my-repo"`; `"my-repo - Visual Studio Code"` (no file) → `"my-repo"`; titles without ` - Visual Studio Code` → `null`; titles with dirty marker (`● foo.cs - my-repo - Visual Studio Code`) → `"my-repo"` |
| `ActiveRepoResolver` | Window-title beats polling; polling used when no IDE match; sticky when no signal; change events emitted only on actual transitions; resolver returns null for empty tracked list; collision (same basename, two repos) takes the first |
| Migration | If `tracked_repo` empty and `config.repo_path` set and path exists, migrates; if path missing, doesn't migrate; idempotent (running twice does nothing) |
| Win32 probe + Settings UI | Manual smoke only |

## 11. Acceptance Criteria

1. Opening the new exe with an old `state.db` (single-repo config) migrates that repo into `tracked_repo` and the daemon behaves identically.
2. Settings UI lists tracked repos; Add and Remove work; Finish requires ≥1 repo.
3. With three repos tracked, focusing VS Code on each in turn causes the dashboard "Branch / Ticket" labels to switch within 2s.
4. With VS Code not in focus, the daemon picks the repo whose `.git/HEAD` was modified most recently (e.g. after a `git checkout` in another terminal).
5. With neither signal, the previously detected ticket continues to accumulate (sticky).
6. Tray balloon / worklog submission flow on `In Progress → Review` transition is unaffected.
7. All previous tests pass plus new unit tests for repo repository, parser, and resolver.

## 12. Open Questions / Deferred

- Should we detect when the OS itself is locked (`Win+L`) and force "no active repo"? Currently `IdleStateMachine` already handles this from the time-accumulation side; the resolver doesn't need to know.
- "Discover repos" UI button that scans a parent folder for `.git` directories — useful but YAGNI.
- IDE coverage expansion (Cursor, JetBrains) — straightforward to add new parsers behind a `IWindowTitleParser` interface; deferred until needed.
