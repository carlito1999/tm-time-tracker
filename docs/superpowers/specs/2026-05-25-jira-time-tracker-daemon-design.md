# Jira Time-Tracker Daemon — Design Spec

**Date:** 2026-05-25
**Author:** Lefteris Tsegkos (in collaboration with Claude Code)
**Status:** Draft — awaiting implementation plan
**Target project:** `TmTimeTracker` (new C# .NET 8 solution, separate from training-manager)

---

## 1. Goal

Eliminate manual time entry on Jira tickets. A background daemon on Windows observes the user's actual work (Claude Code activity, branch context, `.remember/` entries, idle state) and posts accurate worklogs to Jira automatically when a ticket transitions from "In Progress" → "Review" — with a tray confirmation step before each push.

## 2. Non-Goals

- Tracking work that happens outside Claude Code sessions (no system-wide app usage logging).
- Tracking other Atlassian projects beyond the user's configured repo (single-project v1).
- Cross-machine sync (state stays local on one PC).
- Team/multi-user features (single-user personal daemon).
- Replacing Jira-side workflow tooling. Daemon only reads status + writes worklogs.

## 3. Confirmed Design Decisions

| Decision | Choice |
| -------- | ------ |
| Language / runtime | C# / .NET 8, self-contained Windows x64 single-file exe |
| Architecture shape | Single tray app — Generic Host + BackgroundServices + WinForms tray (Approach A) |
| Time source | Hybrid: Claude Code session presence + `.remember/` entries + Windows idle cap |
| Idle threshold | 10 minutes (no kbd/mouse input) |
| Push trigger | Jira status transition: "In Progress" → "Review" |
| Detection method | Daemon polls Jira REST API every 90s |
| Authentication | OAuth 2.0 (3LO) — own registered OAuth client app at developer.atlassian.com |
| Confirmation UX | Windows tray balloon + edit form before submit |
| Worklog description source | Concatenated bullet list of `.remember/` entry bodies tagged to ticket |
| Local storage | SQLite at `%LOCALAPPDATA%\TmTimeTracker\state.db` |
| Ticket extraction | Regex `\bTM-\d+\b` against current git branch name; first match wins |
| Idempotency | v1: accept rare duplicates after crash mid-submit + provide "Cleanup duplicates" tray menu |
| Auto-start | Registry `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` |

## 4. Architecture

### 4.1 Process model

A single user-session WinForms-host executable named `TmTimeTracker.exe`. Hosted via .NET Generic Host (`IHost`). The host owns:

- A single tray icon (`NotifyIcon`) on the WinForms UI thread.
- Multiple `BackgroundService` instances running on the .NET ThreadPool.
- A single SQLite connection (or pool) shared via dependency injection.

Runs in user session (NOT a Windows Service) so `GetLastInputInfo` works.

### 4.2 Components

```text
TmTimeTracker.exe
│
├── TrayIcon                  Visible UI: tray icon, balloons, right-click menu, edit form
│
├── BackgroundServices (Generic Host)
│   ├── IdleMonitor           Polls Win32 GetLastInputInfo every 5s; emits Active/Idle
│   ├── BranchWatcher         Polls `git rev-parse --abbrev-ref HEAD` every 10s; emits branch
│   ├── RememberWatcher       FileSystemWatcher on .remember/today-*.md + now.md; parses
│   ├── TimeAggregator        Per-minute tick: if Active AND has ticket, +1 to ticket_time
│   ├── JiraPollService       Every 90s: poll status of open-cycle tickets; detect transitions
│   └── OAuthCoordinator      Owns OAuth lifecycle; first-run consent; access-token refresh
│
└── SQLite at %LOCALAPPDATA%\TmTimeTracker\state.db
```

All components communicate via a singleton in-process EventBus (`System.Threading.Channels` or `IObservable<T>`) — no IPC, no cross-process state.

### 4.3 Out of scope for daemon process

- No Windows Service wrapper (would lose idle detection due to session-0 isolation).
- No public webhook endpoint (no inbound HTTP).
- No MCP client (we use Jira REST API directly).
- No second process for tray (rejected for simplicity).

## 5. Data Model

```sql
-- Per-ticket accumulated time. One row per (ticket_key, accumulation_cycle).
CREATE TABLE ticket_time (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    ticket_key        TEXT NOT NULL,              -- e.g., "TM-29"
    cycle_started     TEXT NOT NULL,              -- ISO-8601 UTC
    minutes_active    INTEGER NOT NULL DEFAULT 0, -- only non-idle minutes
    last_seen_status  TEXT,                       -- last Jira status snapshot
    last_polled       TEXT,                       -- when we last fetched status
    submitted_at      TEXT,                       -- NULL until worklog pushed
    worklog_id        TEXT,                       -- Jira's worklog ID after submit
    submitted_minutes INTEGER,                    -- if user edits down at submit time
    UNIQUE(ticket_key, cycle_started)
);

CREATE INDEX idx_ticket_unsubmitted
    ON ticket_time(ticket_key) WHERE submitted_at IS NULL;

-- Append-only log of .remember/ entries we observed, mapped to ticket.
CREATE TABLE remember_entry (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    ticket_key          TEXT NOT NULL,
    timestamp_local     TEXT NOT NULL,          -- "HH:MM"
    entry_date          TEXT NOT NULL,          -- date of source today-*.md
    body                TEXT NOT NULL,
    source_file         TEXT NOT NULL,
    consumed_in_worklog INTEGER NULL            -- FK to ticket_time.id after submit
);

-- Minute-by-minute samples (audit/debug; 30d retention)
CREATE TABLE minute_sample (
    sampled_at      TEXT PRIMARY KEY,           -- ISO-8601 UTC, minute precision
    ticket_key      TEXT,
    is_idle         INTEGER NOT NULL,           -- 0/1
    git_branch      TEXT,
    claude_running  INTEGER NOT NULL            -- 0/1 (process detection)
);

-- OAuth state (singleton row; tokens DPAPI-encrypted)
CREATE TABLE oauth_state (
    id                  INTEGER PRIMARY KEY CHECK(id = 1),
    cloud_id            TEXT NOT NULL,
    access_token_dpapi  BLOB NOT NULL,
    refresh_token_dpapi BLOB NOT NULL,
    access_expires_at   TEXT NOT NULL,
    scope               TEXT NOT NULL
);

-- App config (singleton row)
CREATE TABLE config (
    id                          INTEGER PRIMARY KEY CHECK(id = 1),
    idle_threshold_seconds      INTEGER NOT NULL DEFAULT 600,
    jira_poll_interval_seconds  INTEGER NOT NULL DEFAULT 90,
    repo_path                   TEXT NOT NULL,
    remember_path               TEXT NOT NULL,
    in_progress_status_name     TEXT NOT NULL DEFAULT 'In Progress',
    transition_to_status_name   TEXT NOT NULL DEFAULT 'Review'
);
```

### 5.1 Invariants

- A `ticket_time` row with `submitted_at IS NULL` is "open" and accumulating.
- At any moment, at most ONE row per `ticket_key` should be open. Enforced by `TimeAggregator` performing a `SELECT id WHERE ticket_key = ? AND submitted_at IS NULL` before any `INSERT`; if a row exists, reuse it instead of creating a new one. All cycle creations route through a single repository method to keep this in one place.
- `submitted_minutes <= minutes_active` (user can edit down at submit, not up). Enforced in the edit form via UI validation AND in the worklog submission handler.
- `minute_sample` rows older than 30 days are pruned by a daily maintenance task.

## 6. Data Flow Scenarios

### 6.1 Happy path: full workday on TM-29

```text
09:00  Daemon starts at user login. OAuth tokens valid. Polls Jira: TM-29 = "In Progress".
       BranchWatcher reads HEAD → "TM-29-1808-..." → extracts TM-29.
       INSERT ticket_time (TM-29, cycle_started=09:00, minutes_active=0).
09:00–11:30  Active. TimeAggregator ticks 150 times → minutes_active = 150.
11:30–12:00  Idle (coffee break). No increment.
12:00–13:00  PC locked (lunch). No increment.
13:00–13:15  Active. minutes_active = 165.
13:15  /remember writes ".remember/today-...md" entry tagged TM-29.
       RememberWatcher inserts remember_entry row.
13:15–17:00  Active. minutes_active = 380.
17:05  User clicks "Move to Review" in Jira UI.
17:06  JiraPollService cycle: TM-29 status = "Review". Transition detected.
       Signal TrayIcon.
17:06  Balloon: "TM-29 moved to Review. Log 6h 20m?"
       Click → edit form (ticket, time-editable, description-editable, [Submit] [Skip] [Edit later]).
17:06  User clicks Submit. POST /rest/api/3/issue/TM-29/worklog.
       UPDATE ticket_time submit fields. Tag remember_entry rows.
```

### 6.2 Edge case: Ticket switching mid-day

Branch checkout from TM-29 to TM-30 → TimeAggregator stops incrementing TM-29's `minutes_active` (no minutes credited while you're not on that branch) but leaves TM-29's `ticket_time` row in the DB with `submitted_at IS NULL` (the cycle stays "open"). It then opens or resumes a TM-30 cycle. Switching back to TM-29's branch later: TimeAggregator finds the existing open TM-29 row and resumes incrementing it. Net effect: a single cycle can span multiple non-contiguous work periods.

### 6.3 Edge case: Daemon offline during transition

Daemon offline when status moved. On startup, JiraPollService compares current Jira status to `last_seen_status` in DB and detects the gap-spanning transition.

### 6.4 Edge case: Ticket reopened after worklog submit

If status returns to "In Progress" after closed cycle, daemon opens a new cycle row (independent accumulation). Next "Review" transition produces a second worklog. Jira allows multiple worklogs per ticket — correct behavior.

### 6.5 Edge case: User dismisses balloon

`ticket_time` stays open with `submitted_at` NULL. Tray menu shows "Pending worklogs (N)" entry. User can submit later. No re-prompt loop.

### 6.6 Edge case: OAuth refresh expired (90d inactivity)

401 on refresh → daemon enters "needs auth" state, balloon prompts re-consent, polling paused until re-auth.

### 6.7 Edge case: Branch has no TM-XX prefix

Regex match fails → no current ticket context → TimeAggregator does not increment.

## 7. Error Handling

| Failure | Response | User visible |
| ------- | -------- | ------------ |
| Jira 401 | Try refresh → if still 401, signal "re-auth needed" balloon | Yes |
| Jira 5xx / network | Exponential backoff 90s → 3m → 10m → 30m cap | Only if >2h |
| Jira 429 | Honor Retry-After header | No |
| Worklog POST fails | Keep submit fields NULL; surface error in form; user retries | Yes |
| OAuth refresh expired | Pause polling; re-consent flow | Yes |
| Git path bad / missing | Stop BranchWatcher; tray config option | Yes |
| `.remember/` missing | Disable RememberWatcher silently; daemon still works | No |
| SQLite corruption | Move db aside + recreate; balloon notification | Yes |
| Double-instance | Named mutex check; second instance exits with balloon | Briefly |
| Clock skew / sleep | Trust real minute ticks (not wall-clock subtractions) | No |
| Multiple TM-XX in branch | Use first regex match; log warning | No |
| User-edited minutes > observed | Form validation, disable Submit | Yes |
| Skipped intermediate status | Trigger on any move out of "indeterminate" category | Yes |
| No open cycle for current ticket | Auto-create if Jira status is "In Progress"; else log warning | No |

### 7.1 Resilience principles

1. Local DB is source of truth. Jira outages don't lose time.
2. Once a minute is incremented in `minutes_active`, it persists.
3. Worklog submission is the ONLY operation needing Jira available.
4. Bounded retry (max ~24h continuous failures before pausing).
5. All errors logged to `%LOCALAPPDATA%\TmTimeTracker\logs\daemon-YYYY-MM-DD.log` with 30d rotation.

### 7.2 Idempotency note

Jira's worklog REST API does not accept client-side `Idempotency-Key`. v1 accepts rare duplicate risk after crash-mid-submit. Mitigation: a tray menu item "Cleanup duplicate worklogs" lists today's worklogs per ticket and allows deletion of extras. Pre-flight check (list-before-post) is a v2 improvement.

## 8. Testing Strategy

### 8.1 Unit tests (xUnit, ~40 tests)

- `TicketKeyExtractor`: `TM-29-1808-foo` → `TM-29`; `feature/TM-31` → `TM-31`; `main` → null; multiple matches → first.
- `RememberEntryParser`: `## 07:20 | TM-29-...\nbody` → structured entry; multi-entry files; missing body.
- `IdleStateMachine`: Active → Idle on 10min no input; Idle → Active on any input; system lock → instant Idle.
- `TimeAggregator` tick: Active + ticket → +1; Idle + ticket → 0; Active + no ticket → 0.
- `OAuthTokenStore`: DPAPI encrypt/decrypt roundtrip; expired access triggers refresh.
- `WorklogDescriptionBuilder`: bullet concat; truncates at Jira's worklog-comment length limit (verify limit during implementation; conservatively assume 32 KB).
- Status transition detector: "In Progress" → "Review" triggers; same-to-same doesn't; "Review" → "Done" doesn't.

### 8.2 Integration tests (in-process, ~15 tests)

WireMock.Net stubs Jira REST; SQLite uses `:memory:`.

- Full lifecycle: open cycle → accumulate → status change → POST.
- Daemon-offline-during-transition gap detection.
- Branch switching mid-session → multi-ticket accumulation.
- Ticket reopened after submit → new cycle row.
- 401 → 401 refresh → needs-auth state.
- 429 + Retry-After → honored.
- Worklog 5xx → submitted_at NULL, retryable.
- OAuth happy-path callback → tokens stored encrypted.

### 8.3 Manual smoke tests (pre-release)

1. First-run consent flow (real Atlassian).
2. Live status transition + balloon + submit + verify in Jira.
3. Lock/unlock Windows → idle samples.
4. Sleep/resume laptop → no false minutes.
5. Crash recovery (kill process mid-accumulation → relaunch → open cycles intact).

### 8.4 Explicitly NOT tested in v1

- DPAPI encryption strength (trust OS).
- WinForms rendering (visual; manual only).
- 90d OAuth refresh expiry path (would require time-warping).
- Jira's own server behavior.

### 8.5 CI

GitHub Actions, `windows-latest`:

- `dotnet test` (unit + integration)
- `dotnet publish -r win-x64 --self-contained` → single-file exe artifact
- No live Atlassian calls in CI.

## 9. Open Questions / Deferred

- **OAuth app registration:** developer.atlassian.com app needs creation; client_id/client_secret need to land in a build-time secret (NOT checked in). Plan to use a `secrets.json` ignored from git, read on first run.
- **Worklog visibility:** Jira worklogs respect issue-level permissions. Assume user's own account has write permission on their assigned tickets. No special handling.
- **Multi-repo support:** v1 single repo (training-manager). v2 may scan multiple repos via config list.
- **Time-zone handling:** worklog `started` field is wall-clock + offset. Use local TZ (Europe/Athens for this user) for `started`; UTC for internal DB timestamps.
- **First-time bootstrap of `last_seen_status`:** On first run, set `last_seen_status` to the current Jira status. This prevents the daemon from "detecting" a transition on its first poll if the ticket happens to already be in "Review" (which is currently the case for TM-29!).

## 10. Acceptance Criteria

This v1 is "done" when:

1. Fresh install runs OAuth consent flow and stores tokens encrypted.
2. Daemon auto-starts on Windows login (registry Run key).
3. Working in Claude Code on a `TM-XX-...` branch with Active input causes per-minute increments visible in `ticket_time.minutes_active`.
4. Manually transitioning a ticket from "In Progress" → "Review" in Jira UI causes a tray balloon within 90 seconds.
5. Clicking the balloon shows a form with proposed time + concatenated description; clicking Submit posts a worklog visible in Jira.
6. After submit, the same ticket does NOT prompt again unless reopened.
7. All unit + integration tests pass on CI.
8. Manual smoke tests 1–5 pass on the user's Windows 11 machine.

## 11. Future Work (Out of v1 Scope)

- Pre-flight worklog list-before-POST for true idempotency.
- Multi-repo / multi-project support.
- Tempo / other Jira time-tracking add-on compatibility.
- Activity heatmap UI / weekly summary.
- Optional Slack DM with daily summary.
- macOS / Linux port (would replace DPAPI and idle detection with platform equivalents).
