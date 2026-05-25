# TmTimeTracker UI — Design Spec

**Date:** 2026-05-25
**Author:** Lefteris Tsegkos (in collaboration with Claude Code)
**Status:** Approved — implementation in progress
**Parent design:** [2026-05-25-jira-time-tracker-daemon-design.md](2026-05-25-jira-time-tracker-daemon-design.md)

---

## 1. Goal

Replace CLI-driven setup (`secrets.json` + `--login`) and tray-menu-only monitoring (right-click → Pending worklogs) with two WinForms windows: a 3-page **Setup Wizard** and a live **Dashboard**. Setup data moves out of `secrets.json` into a DPAPI-encrypted SQLite table so the published exe is fully portable — no config files needed beside it.

## 2. Non-Goals

- Cross-platform UI (we're WinForms on Windows; explicit non-goal in the parent spec).
- Separate UI process / IPC. Both windows are hosted on the existing daemon's UI thread.
- Multi-user / multi-profile setup. Single user, single Atlassian site.
- Historical reporting / heatmaps / weekly summaries (left for v2 per parent spec §11).

## 3. Confirmed Design Decisions

| Decision | Choice |
| -------- | ------ |
| UI scope | Setup wizard + live dashboard |
| Secrets storage | Encrypted in `state.db` (new `oauth_app_config` table) |
| Manual submit | Per-row "Submit now" button on dashboard (alongside auto-balloon on Review transition) |
| Hosting | In-process on existing WinForms UI thread (no new process) |
| Re-opening Settings | Re-uses `SetupWindow` rendered as tabs instead of step-through |
| First-run behavior | Auto-opens wizard when required rows absent; suppresses `JiraPollService` until setup complete |
| Dashboard refresh | Event-driven (subscribes to `IEventBus`); no polling loop |

## 4. Architecture

### 4.1 New windows

```text
TrayIconHost  (existing, owns UI thread + Application.Run loop)
├── SetupWindow      (singleton; opened on first run OR from "Settings…" menu)
└── DashboardWindow  (singleton; opened from "Open dashboard…" menu OR
                      auto-shown once after wizard Finish)
```

Both windows are managed by a new `WindowsHost : BackgroundService` that owns weak references on the UI thread and exposes `ShowSetup()` / `ShowDashboard()` marshaled via `WindowsFormsSynchronizationContext`. Calling Show when the window is already visible just brings it to front.

### 4.2 Tray menu (revised)

```
TmTimeTracker
├── Open dashboard…          (new — primary action)
├── Settings…                (new — opens SetupWindow in tabbed mode)
├── Open log folder          (unchanged)
├── ─────
└── Quit                     (unchanged)
```

The old **Pending worklogs…** submenu is removed; the dashboard supersedes it.

### 4.3 Out of scope for this iteration

- Visual restyle (using stock WinForms controls)
- Tray icon image change (still `SystemIcons.Application`)
- Localization (English only)

## 5. Data Model

### 5.1 New table

```sql
CREATE TABLE IF NOT EXISTS oauth_app_config (
    id                INTEGER PRIMARY KEY CHECK(id = 1),
    client_id_dpapi   BLOB NOT NULL,
    client_secret_dpapi BLOB NOT NULL,
    redirect_uri      TEXT NOT NULL
);
```

`oauth_state` (access/refresh tokens) is unchanged.

### 5.2 `config` table update support

`ConfigRepository` gains an `Update(AppConfig)` method (existing `SetIfMissing` stays). The settings UI uses `Update`.

### 5.3 `ticket_time` — new lifecycle for "Discard"

The Discard row action sets `submitted_at = now`, `submitted_minutes = 0`, `worklog_id = 'discarded:<guid>'`. No schema change. The marker keeps audit history while distinguishing discarded rows from real Jira submissions for any future "Cleanup duplicates" feature.

## 6. Component Responsibilities

| Component | Responsibility |
|-----------|----------------|
| `OAuthAppConfigRepository` | DPAPI encrypt/decrypt of Client ID + Secret; persist redirect URI |
| `IOAuthAppConfigSource` | Read-only abstraction injected into `JiraOAuthClient` so it picks up fresh creds without app restart |
| `JiraOAuthClient` (modified) | Takes `IOAuthAppConfigSource` instead of cached `AtlassianSecrets`; calls source on every `BuildAuthorizationUrl`/`ExchangeCodeAsync`/`RefreshAsync` |
| `SetupWindow` | 3-page wizard mode + tabbed-settings mode (same controls, different layout) |
| `DashboardWindow` | Live state strip, pending-worklogs table, recent-events log, status bar |
| `WindowsHost` | Owns singleton windows on UI thread; marshals show requests |
| `Program.cs` (modified) | First-run detection: opens wizard before starting `JiraPollService` if required rows missing |

## 7. Setup Wizard — Page Contracts

### 7.1 Page 1: Atlassian OAuth app

| Control | Behavior |
|---------|----------|
| Info label | Explains why credentials are needed; mentions DPAPI encryption |
| "Open developer.atlassian.com" button | Launches default browser to `https://developer.atlassian.com/console/myapps/` |
| Client ID textbox | Required, non-empty |
| Client Secret textbox | Required, non-empty, password-masked (`UseSystemPasswordChar = true`) |
| Redirect URI textbox | Read-only, defaulted to `http://localhost:53682/callback` |
| [Next] | Enabled only when both Client ID and Secret are non-empty |
| [Cancel] | Closes wizard without saving (only allowed when setup is partially complete; on first run the X button quits the app to avoid leaving the daemon in a partly-configured state) |

On Next: `OAuthAppConfigRepository.Save(...)` is called, then advance to page 2.

### 7.2 Page 2: Connect to Atlassian

| Control | Behavior |
|---------|----------|
| Status label | "Not connected" / spinner during flow / "Connected to <cloudId>" / error message |
| "Sign in to Atlassian" button | Runs the existing `JiraOAuthClient.BuildAuthorizationUrl` → `Process.Start(browser)` → `LocalCallbackListener.ListenOnceAsync` → `OAuthCoordinator.CompleteFirstRunAsync` flow on a background task; UI shows spinner |
| "Retry" button | Visible only on error |
| [Next] | Enabled only after successful login |
| [Back] | Returns to page 1 |

### 7.3 Page 3: Paths & timing

| Control | Default |
|---------|---------|
| Repo path (textbox + Browse) | `c:\projects\training-manager` |
| Remember path (textbox + Browse) | `<repo>\.remember` (auto-fills when Repo path changes if currently equals previous default) |
| Idle threshold (minutes) | 10 |
| Jira poll interval (seconds) | 90 |
| "In Progress" status name | `In Progress` |
| Transition target status name | `Review` |
| [Finish] | Calls `ConfigRepository.Update(...)`; closes wizard; raises a `SetupCompleted` event that `Program`/`WindowsHost` uses to start `JiraPollService` and show the dashboard |
| [Back] | Returns to page 2 |

### 7.4 Settings (tabbed) mode

When opened via tray "Settings…" after setup is complete, the same controls render as a `TabControl` with three tabs (OAuth app, Connection, Paths & timing). [Save] saves all three; [Cancel] discards changes. Page 2's "Sign in" button re-runs the OAuth flow on demand (re-consent path).

## 8. Dashboard — Component Contracts

### 8.1 State strip (top)

| Element | Source |
|---------|--------|
| Activity indicator (●Active / ●Idle) | Last `ActivityChanged` event |
| Current branch | Last `BranchChanged.Branch` |
| Current ticket | Last `BranchChanged.TicketKey`, or "(none)" |

### 8.2 Pending worklogs table

`DataGridView` bound to `TicketTimeRepository.GetAllOpen()`. Columns: Ticket, Minutes, First seen (cycle_started, formatted), Last activity (max of remember-entry-tagged times + tick events). Refreshed on `ActivityChanged`/`BranchChanged`/timer (5s fallback).

Selecting a row enables the three buttons below:

- **Submit now** — fetches unconsumed remember entries, builds description via `WorklogDescriptionBuilder`, calls `JiraApiClient.PostWorklogAsync` with full observed minutes; on success calls `MarkSubmitted` + `TagConsumed`. No edit form. Errors surface as MessageBox.
- **Edit & submit** — opens the existing `WorklogEditForm` (same path as the balloon-triggered flow).
- **Discard** — confirmation MessageBox ("Discard X minutes on TM-NN? This cannot be undone.") → on Yes: `MarkSubmitted(cycleId, "discarded:" + Guid.NewGuid("N"), 0, _clock.UtcNow)`.

### 8.3 Recent events log

`ListView` (Detail view) backed by a bounded in-memory ring of the last 50 events from `IEventBus.Subscribe`. Filter dropdown (All / Activity / Branch / Jira / Worklog) is a client-side filter.

### 8.4 Status bar

- "Auth ✓" / "Auth ✗ — click to sign in" (clicking opens Settings → page 2)
- "Last Jira poll: HH:MM:SS" (from `cycle.LastPolled` max)
- "Next: in Ns" (derived from poll interval and last poll)
- Errors fade in/out for 10s

## 9. First-Run Flow

`Program.cs` (after `SingleInstanceGuard` + DB init):

```text
needSetup := false
needsLogin := false
needsConfig := false

if OAuthAppConfigRepository.Load() is null → needsSetup = needsLogin = needsConfig = true
else if OAuthStateRepository.Load() is null → needsSetup = needsLogin = true
else if ConfigRepository row missing → needsSetup = needsConfig = true

if needsSetup:
    WindowsHost.ShowSetup(startPage: needsLogin ? (needsConfig ? Page1 : Page2) : Page3)
    Suspend JiraPollService (use a gate flag)
    Wait for SetupCompleted event → start JiraPollService → ShowDashboard()
else:
    Start full daemon as today; no window auto-opens
```

`Page1`/`Page2`/`Page3` are the wizard's initial page. The Cancel/X behavior on first run shuts down the app cleanly (since a partially configured daemon is not useful).

## 10. Error Handling

| Failure | Response |
|---------|----------|
| OAuth app creds saved but Jira login fails (4xx) | Page 2 shows error + Retry; user can also Back to fix creds |
| `Process.Start(browser)` fails (no default browser) | Page 2 shows error + the URL as selectable text |
| `LocalCallbackListener` port in use | Catch in OAuth flow → show "Port 53682 in use — close other apps and Retry" |
| DPAPI Unprotect fails (token corruption / different user) | OAuthAppConfigRepository.Load() returns null → first-run flow reopens setup |
| `JiraApiClient.PostWorklogAsync` 4xx/5xx on Submit Now | MessageBox with status code + body; row stays open and retryable |
| Discard confirmed accidentally | No undo in v1 (acknowledged trade-off; future "Cleanup duplicates" tool may surface discarded rows for restore) |
| Dashboard subscribes to bus before any event fires | UI shows "(no events yet)" placeholders; populates on first event |

## 11. Testing Strategy

| Component | Tests |
|-----------|-------|
| `OAuthAppConfigRepository` | Round-trip with mocked `ITokenProtector`; idempotent save; load returns null when absent |
| `ConfigRepository.Update` | Overwrites all fields; new row created if Update called when no row exists |
| `JiraOAuthClient` (modified) | Existing tests adapted to use a fake `IOAuthAppConfigSource`; verify creds are re-read on each call (no stale cache) |
| Wizard / Dashboard | Manual / visual only (consistent with parent spec §8.4) |
| Smoke checklist additions | First-run shows wizard; Submit-now posts worklog; Discard marks row without posting |

## 12. Acceptance Criteria

This UI work is "done" when:

1. Fresh install (deleted `state.db`) launches the wizard on page 1.
2. Completing all 3 pages saves creds (encrypted), tokens (encrypted), and config, then opens the dashboard.
3. Tray "Settings…" opens the same controls in tabbed form; saving updates the corresponding tables.
4. Dashboard reflects live state within 1s of an `ActivityChanged` or `BranchChanged` event.
5. Per-row "Submit now" posts a worklog to Jira visible in the issue; row no longer appears as open.
6. Per-row "Discard" marks the row submitted without an API call; row no longer appears as open.
7. `secrets.json` is no longer required; if present, it's ignored (or honored only as a one-time bootstrap fallback — TBD during implementation).
8. All existing unit + integration tests pass plus 4 new repository tests.
9. Manual smoke tests 8–10 (new) pass.

## 13. Migration / Compatibility

- Users with an existing `secrets.json` + populated `oauth_state` will be migrated transparently: on first launch after upgrade, `Program.cs` reads `secrets.json` (if present), writes the values to `oauth_app_config` via DPAPI, and then ignores `secrets.json` going forward. After successful migration, `secrets.json` is renamed to `secrets.json.migrated` so the user can delete it.
- Existing config rows (`config`) work as-is.
- The CLI smoke modes (`--login`, `--probe-jira`, `--smoke-*`) remain functional for development.

## 14. Open Questions / Deferred

- Tray icon image (still `SystemIcons.Application`; custom icon is a v2 polish).
- Auto-update of the published exe — out of scope; user manually replaces `TmTimeTracker.exe` on Desktop.
- "Cleanup duplicates" tray menu item from parent spec §7.2 — deferred to a follow-up.
