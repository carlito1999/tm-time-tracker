# TmTimeTracker

A Windows tray daemon that auto-posts Jira worklogs based on real work activity.
It watches your git branches for `TM-NNN` ticket keys, counts the minutes you
spend actively working on each, and posts a worklog to Jira when you move the
ticket to **Review**. Time you spend idle, locked, or away never gets credited.

> Built for personal use against a specific Jira project (training-manager, tickets
> prefixed `TM-`). Easy to fork for a different prefix — see [Adapting for your project](#adapting-for-your-project).

---

## What it does

- **Detects the active repo** via your VS Code foreground window (with a `.git/HEAD`
  mtime fallback when VS Code isn't focused). Switches automatically when you
  alt-tab between projects.
- **Extracts the ticket** from your branch name with the regex `\bTM-\d+\b` (e.g.
  `feature/TM-29-add-worklog` → `TM-29`).
- **Counts only active minutes** — Windows idle threshold (default 10 min) and lock
  screen both pause the counter; sleep/hibernate are correctly not credited.
- **Carries pre-branch time forward** — active minutes on a branch with no ticket in
  its name (`main`, say) are held rather than discarded, and credited to the next
  ticket branch you check out. Capped at 30 minutes, so a long unrelated stretch on
  `main` is not billed to whatever you branch to afterwards. Switching between two
  *ticket* branches moves nothing: the buffer only fills while no ticket is known.
  It is in memory, so a restart discards it.
- **Watches `.remember/` markdown** in each tracked repo for entries tagged with
  the current ticket; uses them as the auto-generated worklog description.
- **Polls Jira every 90s** for the open tickets' status; when `In Progress → Review`
  is detected, pops a balloon and an edit form for you to confirm/edit/submit.
- **Tray dashboard** shows live state (active ticket, pending worklogs, recent
  events, last Jira poll). Supports manual "Submit now" / "Edit & submit" / "Discard".
- **Survives restarts** — accumulated minutes are persisted to SQLite on every
  minute tick. Reboot and the same cycle resumes.
- **Single-file self-contained Windows exe** (~158 MB) — no .NET install needed
  on the target machine. Auto-registers itself to run at Windows login.

---

## Requirements

| | |
|---|---|
| OS | Windows 10 or 11 (x64) |
| Runtime | None — bundled in the self-contained exe |
| Editor (optional) | VS Code, for the foreground-window repo detection. Without VS Code, the daemon falls back to `.git/HEAD` activity polling. |
| Jira | Atlassian Cloud account with permission to write worklogs on the tickets you work on |
| Git | Git CLI on `PATH` (the daemon shells out to `git rev-parse`) |

For building from source: .NET 8 SDK only.

---

## Quick start (5 minutes)

If you already have the published `TmTimeTracker.exe` and an Atlassian account:

1. **Register an OAuth 2.0 app at Atlassian** (one-time, takes ~2 min). Details in
   [Atlassian app registration](#atlassian-app-registration) below.
2. **Copy `TmTimeTracker.exe`** to a stable folder (recommended:
   `%LOCALAPPDATA%\TmTimeTracker\bin\`).
3. **Double-click it**. The setup wizard opens automatically.
4. **Page 1:** paste Client ID + Secret from your Atlassian app. → Next.
5. **Page 2:** click "Sign in to Atlassian" → approve in browser → close tab. → Next.
6. **Page 3:** click "Add…" and pick each git repo you want tracked. Leave the
   idle/poll defaults. → Finish.
7. Dashboard opens. Daemon is running. Right-click the tray icon for menu.

The exe registers itself in `HKCU\…\Run` so it starts on every Windows login from
then on.

---

## Setup walkthrough (detailed)

### Atlassian app registration

This is the only step that touches Atlassian's web console. Your Atlassian
*workspace admin* is **not** involved — OAuth apps are tied to your personal
developer account.

1. Open <https://developer.atlassian.com/console/myapps/> and sign in with your
   normal Atlassian account.
2. **Create** → **OAuth 2.0 integration**. Name it something like
   `TmTimeTracker (personal)`. It's private to your account; no one else sees it.
3. In the left sidebar:
   - **Permissions** → **Jira API** → **Add**. The "classic" Jira platform REST
     API permission is what we need. (If you see a warning about granular scopes,
     ignore it — classic scopes work.)
   - **Authorization** → **OAuth 2.0 (3LO)** → **Configure** → Callback URL:
     ```
     http://localhost:53682/callback
     ```
     (Exact match required — this is the port the daemon's local listener binds to.)
4. **Settings** → **Authentication details** → copy:
   - **Client ID**
   - **Secret** (click "Show")

Keep these two values handy for the wizard.

### Running the daemon for the first time

Double-click `TmTimeTracker.exe`. The setup wizard appears.

**Page 1 — OAuth app credentials**
- Paste Client ID and Secret.
- Redirect URI is fixed at `http://localhost:53682/callback` (must match your
  Atlassian app config exactly).
- Both values are encrypted via Windows DPAPI before being saved to `state.db`
  (tied to your Windows user — unreadable by other users or on other machines).

**Page 2 — Connect to Atlassian**
- Click **Sign in to Atlassian**. Your browser opens to Atlassian's consent page.
- Approve the app's access to your Jira account.
- Browser shows "TmTimeTracker connected. You can close this tab."
- Wizard advances to "Connected!"

**Page 3 — Tracked repos + timing**
- Click **Add…** for each repo you want monitored. The folder must contain a
  `.git` subfolder; otherwise add is rejected.
- Idle threshold (default 10 min): how long with no keyboard/mouse input before
  the daemon stops crediting time.
- Jira poll interval (default 90 s): how often the daemon checks each open
  ticket's status.
- "In Progress" status name / Transition target: leave defaults unless your Jira
  project uses different status names.
- Click **Finish**. Dashboard opens. Daemon is now fully operational and auto-start
  is registered.

---

## Daily use

### Tray menu

Right-click the TmTimeTracker icon (system tray, bottom-right; may be under the
`^` overflow):

- **Open dashboard…** — main monitoring window
- **Settings…** — re-opens the wizard for editing
- **Export report…** — the weekly hour-by-hour spreadsheet (see below)
- **Open log folder** — `%LOCALAPPDATA%\TmTimeTracker\logs\`
- **Quit** — stops the daemon (won't restart until next Windows login, unless you
  launch the exe manually)

### Dashboard

```
● ACTIVE   Branch: feature/TM-29-foo   Ticket: TM-29

Pending worklogs
┌─────────┬─────────┬─────────────┬──────────────────┐
│ Ticket  │ Minutes │ First seen  │ Last Jira poll   │
├─────────┼─────────┼─────────────┼──────────────────┤
│ TM-29   │   183   │ 09:01       │ 14:42:00         │
└─────────┴─────────┴─────────────┴──────────────────┘
                  [Submit now] [Edit & submit] [Discard]

Recent events                                  Filter: [All ▾]
14:42:11  +1 min on TM-29
14:41:08  BranchChanged → feature/TM-29-foo
...

Auth ✓  ·  Last Jira poll: 14:42:00  ·  Next: in 47s
```

- Top strip updates live from internal event bus (~1 s latency).
- Per-row actions:
  - **Submit now** — posts a worklog with the full observed minutes and an
    auto-generated description from your `.remember/` entries.
  - **Edit & submit** — opens a form so you can reduce minutes or edit the
    description before posting.
  - **Discard** — marks the cycle submitted locally without posting to Jira
    (audit trail preserved with `worklog_id = "discarded:<guid>"`).

### Weekly report

**Tray → Export report…** opens a window that turns your tracked time into an
hour-by-hour spreadsheet:

| Date | Time | Repo | Ticket |
|------|------|------|--------|
| `21-09-26` | `08:00–09:00` | `sheeponline-new` | `SN-291-350: isolated-member studbooks` |
| `21-09-26` | `09:00–10:00` | | |
| `21-09-26` | `10:00–11:00` | `sheeponline-new / training-manager` | two tickets, one per line |

Pick a date range and the hours of the day to lay down (defaults: Monday of this
week through today, 08:00–16:00), name the file, and check the live preview
before writing. Reports land in `Documents\TmTimeTracker\` and Explorer opens
with the file selected.

The name is suggested as `Lefteris-<from>-<to>.xlsx` with sortable dates, and
follows the date pickers until you type something of your own.

Notes:

- **An hour with no tracked time keeps its row and leaves the cells blank**, so
  the shape of the day survives and gaps stay visible.
- **Min. minutes** sets how much an entry must earn *within one hour* to be
  printed in that hour's row. It defaults to **0** — print everything tracked —
  because a silently filtered sheet reads as "nothing was recorded" rather than
  "below your threshold", and pruning a full sheet beats missing work in a
  sparse one. Raise it when an hour gets crowded: `TimeAggregator` credits every
  active repo concurrently, so a one-minute Claude write in another repo earns
  that repo a minute. The floor is asymmetric — it tests a repo's hourly total
  but each ticket separately, so an hour split 4 + 4 across two tickets of one
  repo still names the repo at a floor of 5, because 8 minutes were spent there.
  Nothing is ever lost: the ledger keeps every minute, so re-exporting at a
  different floor brings dropped entries straight back. When everything falls
  under the floor the window says so rather than showing a blank sheet.
- Concurrent work is real — `TimeAggregator` credits every active repo at once,
  so a day can total more than the wall clock.
- Minutes on a branch carrying no ticket count towards the repo but print no
  ticket. Those same minutes reach Jira later under the next ticket branch, so
  the spreadsheet and the worklog will disagree by design: the sheet shows when
  the work happened, the worklog shows what it was eventually billed to.
- History is kept for 90 days. `hour_activity` only fills while the daemon runs,
  so it cannot reconstruct hours recorded before this feature existed.
- A ticket with no cached summary renders as the bare key.

### Automatic worklog posting

When the daemon polls Jira and sees a tracked ticket move `In Progress → Review`,
it pops a balloon notification. Clicking it (or waiting ~5s) opens an edit form
pre-filled with observed minutes and the bullet-list description. Submit posts to
Jira; cancel keeps the cycle open and you can submit later via the dashboard.

### `.remember/` integration

If a repo has a `.remember/` folder with `today-YYYY-MM-DD.md` files, the daemon
parses entries shaped like:

```markdown
## 09:00 | TM-29-add-worklog
Started on the worklog endpoint.
Got auth working.

## 10:30 | TM-29-add-worklog
Hit a 401 — root cause was stale refresh token.
```

These bodies are concatenated as a bullet list into the auto-generated worklog
description. Only entries whose tag contains a TM ticket are included.

---

## Slack notifications

Posts a message to a per-project Slack channel when a tracked ticket moves into
your review status. Messages are posted **as you**, not as a bot — so the app
needs no invitation to private channels you are already in.

### Setup (once, about two minutes)

1. Go to <https://api.slack.com/apps> → **Create New App** → **From an app
   manifest**, pick your workspace, and paste
   [`docs/slack-app-manifest.yml`](docs/slack-app-manifest.yml). The scopes come
   with it — there is nothing to choose.
2. Click **Install to Workspace** and approve.
3. On **OAuth & Permissions**, copy the **User OAuth Token** (starts with `xoxp-`).
4. In TmTimeTracker: **Settings → Slack**, paste the token, click **Save & test**.
   You should see `Connected as @you`.
5. Confirm the channel suggested for each project. The project list builds itself
   from tickets you have already tracked, and each channel is pre-matched from the
   Jira project name — you are confirming, not configuring.

Use **Send test message** to check a channel before relying on it. It posts a
clearly-marked test with sample values, so nobody mistakes it for real work.

### Message templates

Each project owns its own message. The default is:

```
Done ✅ {TICKET} — {SUMMARY}
{FROM} -> {TO} · {HOURS}
{URL}
```

| Variable | Meaning | Example |
| -------- | ------- | ------- |
| `{TICKET}` | Issue key | `SN-296` |
| `{PROJECT}` | Project key | `SN` |
| `{SUMMARY}` | Issue summary | `Fix Tolgee warning` |
| `{URL}` | Link to the issue | `https://acme.atlassian.net/browse/SN-296` |
| `{FROM}` | Previous status | `In Progress` |
| `{TO}` | New status | `Review` |
| `{MINUTES}` | Tracked minutes | `137` |
| `{HOURS}` | Tracked time | `2h 17m` |
| `{DATE}` | Local date and time | `2026-09-02 14:31` |
| `{PR_URL}` | Link to the pull request | `https://bitbucket.org/acme/web/pull-requests/360` |
| `{PR_TITLE}` | Pull request title | `SN-296-372: enhance Tolgee caching` |
| `{PR_STATUS}` | Pull request state | `OPEN` |

The three `PR_*` variables need two things, neither of which is a new credential:

1. **Bitbucket connected to Jira** (Jira → Settings → Apps → Bitbucket). A
   product-to-product link — no Bitbucket login is stored by this app.
2. **The `read:dev-info:jira` scope** on your OAuth app. Add it at
   <https://developer.atlassian.com/console/myapps/> → your app → Permissions →
   Jira API → Configure → add `read:dev-info:jira`, then reconnect from
   **Settings → Connection**. Without it the development endpoint answers
   `401 "scope does not match"`, because `read:jira-work` does not cover it.

Check it end to end with:

```powershell
dotnet run --project src\TmTimeTracker -- --probe-pr <numeric issue id> <ticket key>
```

It prints the pull request the announcement would link to. Bitbucket is asked
first and Jira's dev-status only as a fallback, so the log line shows which one
answered: a `200` from `api.bitbucket.org` is the healthy path, while falling
through to dev-status means the Bitbucket token is missing or the repo's git
remote is not on Bitbucket. A `401 "scope does not match"` from dev-status means
the Jira scope is missing or the app has not been reauthorised since it was added.

### How announcing works

A background worker owns the whole job as a durable checklist, so nothing is lost
to a restart:

1. Any tracked ticket sitting in the review status is queued — both from the
   transition event and by a sweep every 30s, so a missed event or a restart
   mid-wait recovers on its own.
2. A queued ticket that has not been announced is polled until its pull request is
   found. Nothing is posted with a literal `{PR_URL}` in it.
3. When the pull request appears, the message is posted and the ticket is marked
   announced — it can never be announced twice.

   The one picked is the **newest pull request whose branch or title carries the
   ticket key**, preferring an open one, ranked by **pull request id** rather than
   by when it was last updated. Ids only ever go up, so a branch that has been
   re-PR'd against successive `main-DD-MM-YYYY` snapshots always resolves to the
   latest — and a comment on a superseded one cannot drag it back to the top.
   Pull requests belonging to other tickets are ignored outright; Jira's
   dev-status files them under the wrong issue often enough to matter.
4. If **10 minutes pass since the branch's last commit** and no pull request is
   reported, an **urgent Windows notification** tells you to announce it
   yourself. Once per ticket, not every poll. Polling continues in case it turns
   up later.
5. If the only pull request on offer **has not changed since more than 4 hours
   before the ticket moved to review**, it is treated as a source that has not
   caught up rather than as an answer, and nothing is posted. After **10 minutes**
   of that, the same urgent notification hands the job to you — and this one
   stops the daemon announcing the ticket at all, so it cannot duplicate a message
   you posted by hand. Moving the ticket out of review and back clears it.

   Urgent means the toast is allowed to break through Do Not Disturb. Windows asks
   you to permit that the first time one arrives; until you do, it behaves like an
   ordinary notification. Nothing else needs installing — the app registers its own
   notification identity on first use. If toasts are unavailable (disabled by
   policy, for instance) it falls back to a tray balloon.

   The app does **not** know whether you already announced a ticket by hand: this
   notification is deliberately not de-duplicated against manual Slack posts.

   Run `TmTimeTracker.exe --test-toast` to fire one on demand.

State lives in the `pr_announcement` table. When a ticket leaves review its row is
forgotten, so returning to review announces again.

Templates containing no `{PR_*}` placeholder skip the waiting entirely and post
on the first pass.

A note on where Jira lags: the issue view's Development panel and JQL both read a
search index that can trail by many minutes — you may see "Create pull request"
on a ticket whose PR already exists. The worker reads the development *detail*
endpoint, which is closer to live, so it usually knows before the panel does.

When a ticket has several pull requests, an `OPEN` one always wins over a merged
or declined one, and the most recently updated wins within that group — so a
reworked ticket links to the live pull request rather than to last month's.

An unknown placeholder is left exactly as you typed it, so `{TIKCET}` shows up
as a visible typo rather than silently vanishing. The live preview under each
template renders against sample values as you type.

### What triggers a message

The existing Jira poller already publishes a status-transition event on the
*edge* — when a tracked ticket's status becomes your review status having
previously been something else. The notifier just listens to that, which is why
it fires exactly once per transition and needs no de-duplication of its own.

Only tickets you actually tracked time on can notify, and a project with no
channel selected simply never notifies. Notification failures are logged and
dropped: they can never disturb time tracking or worklog submission.

---

## Configuration & data

### Where state lives

```
%LOCALAPPDATA%\TmTimeTracker\
├── state.db                  SQLite — all persistent data
├── state.db-wal              SQLite write-ahead log
├── state.db-shm              SQLite shared memory file
└── logs\
    ├── daemon-20260525.log   Today's log
    ├── daemon-20260524.log   Yesterday's
    └── ...                   (30-day rotation)
```

### Schema

Tables in `state.db`:

| Table | Purpose |
|-------|---------|
| `tracked_repo` | List of git repos you want monitored |
| `ticket_time` | Accumulated minutes per (ticket, cycle); cycles span open → submitted |
| `remember_entry` | Parsed `.remember/` entries tagged to tickets |
| `oauth_app_config` | DPAPI-encrypted Client ID + Secret |
| `oauth_state` | DPAPI-encrypted access + refresh tokens, cloud ID |
| `config` | Idle threshold, poll interval, status names |
| `hour_activity` | Minutes per (local hour, repo, ticket) — the weekly report's source (90 d retention) |
| `ticket_summary` | Ticket key → Jira summary, cached by the poll so reports can name a ticket offline |
| `minute_sample` | Legacy per-minute audit trail. Pruned at 30 d, but nothing writes to it — superseded by `hour_activity` |

Open it with [DB Browser for SQLite](https://sqlitebrowser.org/) if you ever
want to inspect or surgically edit anything.

### Resetting

To start completely fresh (loses all accumulated time, tokens, settings):

```powershell
Stop-Process -Name TmTimeTracker -Force
Remove-Item "$env:LOCALAPPDATA\TmTimeTracker\state.db*" -Force
# Relaunch the exe — wizard reopens at page 1
```

### Auto-start

The daemon writes itself to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\TmTimeTracker`
on first successful launch. To disable auto-start:

```powershell
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name TmTimeTracker
```

---

## Troubleshooting

### Nothing happens when I double-click the exe

`TmTimeTracker.exe` is a WinExe (no console), so startup errors don't print to
a terminal. Check the log file:

```powershell
Get-Content "$env:LOCALAPPDATA\TmTimeTracker\logs\daemon-$(Get-Date -Format yyyyMMdd).log" -Tail 50
```

A `[FTL] TmTimeTracker crashed` line followed by a stack trace will tell you why.
The daemon also pops a MessageBox on fatal startup exceptions (added after we hit
exactly this issue during development).

### Tray icon doesn't appear

Windows hides infrequent tray icons in the overflow flyout (the `^` button). Drag
the TmTimeTracker icon out of the flyout and into the always-visible area to pin it.

### "Sign in to Atlassian" fails with port-in-use

Another process is using port 53682. Either close that process or change the
redirect URI (requires also updating the Atlassian app config to match — and the
hardcoded port in [LocalCallbackListener.cs](src/TmTimeTracker/Jira/LocalCallbackListener.cs)
and [AppSecrets.cs](src/TmTimeTracker/Configuration/AppSecrets.cs)).

### OAuth 401 errors after long inactivity (>90 days)

Atlassian invalidates refresh tokens after 90 days of non-use. The daemon will
log the 401 and surface an error in the dashboard's "Auth" status. Re-open
Settings → Connect tab → Sign in again.

### Daemon says "Active repo -> X" but X is wrong

Either:
- Your VS Code window title doesn't match a tracked repo's folder basename (check
  the title bar text — should be `<file> - <repo-folder> - Visual Studio Code`).
- Or VS Code isn't in focus AND another tracked repo's `.git/HEAD` was touched
  more recently. Run `git status` in the repo you want active to update its
  `.git/HEAD` mtime.

---

## Building from source

Prerequisites: .NET 8 SDK on `PATH`, git, Windows x64.

```powershell
git clone <this-repo>
cd tm-time-tracker

# Build + test
dotnet test

# Run from source (with hot-reload-able CLI smoke modes)
dotnet run --project src\TmTimeTracker -- --smoke-activity    # streams events to console
dotnet run --project src\TmTimeTracker                        # full daemon (tray UI)

# Produce a fresh single-file self-contained exe
dotnet publish src\TmTimeTracker\TmTimeTracker.csproj `
    -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o .\publish
# Output: .\publish\TmTimeTracker.exe (~158 MB)
```

CI runs `dotnet test` + the same publish command on `windows-latest` — see
[.github/workflows/build.yml](.github/workflows/build.yml).

---

## Adapting for your project

To track tickets with a different prefix (e.g. `JIRA-123` or `PROJ-456`):

1. Edit the regex in [src/TmTimeTracker/Logic/TicketKeyExtractor.cs](src/TmTimeTracker/Logic/TicketKeyExtractor.cs):
   change `\bTM-\d+\b` to your prefix.
2. Update the tests in [tests/TmTimeTracker.Tests/Logic/TicketKeyExtractorTests.cs](tests/TmTimeTracker.Tests/Logic/TicketKeyExtractorTests.cs).
3. Rebuild + republish.

For a different Atlassian site, just sign in with that account during the wizard
— the daemon discovers the cloud ID from `/oauth/token/accessible-resources`
automatically.

---

## Architecture (one-paragraph)

A .NET 8 Generic Host runs five `BackgroundService`s (`IdleMonitor`, `BranchWatcher`,
`RememberWatcher`, `TimeAggregator`, `JiraPollService`, `MaintenanceService`) plus
a WinForms `TrayIconHost` on a dedicated STA thread. All inter-component
communication flows through a singleton channel-based `EventBus`. SQLite + Dapper
handle persistence; DPAPI handles secret-at-rest. The setup wizard and dashboard
are WinForms windows hosted on the same UI thread as the tray.

Deep dives:

- v1 design — [docs/superpowers/specs/2026-05-25-jira-time-tracker-daemon-design.md](docs/superpowers/specs/2026-05-25-jira-time-tracker-daemon-design.md)
- UI design — [docs/superpowers/specs/2026-05-25-tmtt-ui-design.md](docs/superpowers/specs/2026-05-25-tmtt-ui-design.md)
- Multi-repo detection — [docs/superpowers/specs/2026-05-25-multi-repo-detection-design.md](docs/superpowers/specs/2026-05-25-multi-repo-detection-design.md)
- Manual smoke checklist — [docs/smoke-tests.md](docs/smoke-tests.md)

---

## License

Personal project — no formal license. Use at your own risk.
