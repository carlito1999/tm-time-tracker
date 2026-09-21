# Claude ticket estimation — design

**Date:** 2026-09-03
**Status:** Approved

## Goal

Every 5 minutes, find Jira tickets sitting in **To Do** for each tracked repo, ask Claude
Code — running against the latest `origin/<default>` of that repo — how long *Claude itself*
would take to implement the ticket, test it, and get it through code review, and write the
total to the ticket's **Original Estimate**.

The estimate must be an honest best guess: neither padded nor optimistic.

## Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Where the estimate goes | Jira `timetracking.originalEstimate` | User's call. It becomes real project data. |
| Overwrite protection | Write **only when the field is empty** | Never clobber a human estimate. Also makes the 5-minute loop idempotent without a confirmation dialog. |
| Repo to Jira project | Tiered: exact display name, then exact key, then prefix either direction; a hand-set mapping always wins | See Project matching. |
| Ticket attachments | Images and PDFs downloaded into the worktree; spreadsheets converted to text | A ticket's description is often nothing but a screenshot. See Attachments. |
| Claude auth | Ambient CLI login; optional token override | Works out of the box. Token field on the OAuth tab covers the headless-daemon case where the interactive login has expired. |
| Where Claude runs | Detached `git worktree` under `%LOCALAPPDATA%\TmTimeTracker\estimates\<repo>` | Prevents the estimator from corrupting the app's own time tracking (see Isolation). Also guarantees the user's working copy is untouched. |
| Which revision | Newest branch in the repo's trunk *family*, after `git fetch` | origin/HEAD proved unreliable - see Trunk selection. |
| Model | Latest Sonnet, by bare alias | Sizing a ticket is judgement over code already read, not hard reasoning. Runs unattended on the user's own quota. |
| Subagents | Disallowed | Fan-out was the dominant cost: one spawned Task spent 28k tokens over 24 tool calls before the budget stopped the run. |
| Doneness | Derived from artifacts in code; Claude is never asked | A self-report cannot be verified. |
| Retry | 2 attempts max, targeted at the failing layer | User's rule. |
| Failure surfacing | Windows toast via existing `IUserNotifier` | User's requirement. Deduped — see Notifications. |

## Isolation (why this cannot corrupt time tracking)

`FileClaudeCodeActivityProbe` reads mtimes of `~/.claude/projects/<slug>/*.jsonl`.
`RepoActivityMonitor.Sample` (`Services/RepoActivityMonitor.cs:62`) then credits a repo only
when `ClaudeProjectSlug.FromPath(repo.Path)` matches a slug in that snapshot — and it only
ever computes slugs for **tracked repo paths**.

A Claude session whose working directory is a worktree under `%LOCALAPPDATA%` therefore
produces a slug that is never queried. No minutes can be credited from it.

Two supporting facts, verified empirically on a throwaway clone on 2026-09-03:

- `git fetch origin` does **not** modify `.git/HEAD`.
- `git worktree add --detach` does **not** modify `.git/HEAD`.

This matters because `ActiveRepoResolver.TryReadHeadMtime`
(`Services/ActiveRepoResolver.cs:96`) uses the mtime of exactly `.git/HEAD` as its Tier-1
activity signal. Had either command touched that file, the estimator's own git activity would
have registered as user activity and inflated worklogs.

## Flow

For each tracked repo, every 5 minutes:

1. Resolve the repo's Jira project (stored mapping, else auto-match, else skip + notify once).
2. JQL: `project = "<KEY>" AND status = "To Do" ORDER BY created ASC`. Deliberately `status`,
   not `statusCategory`: the category "To Do" also covers Backlog, Open and Selected for
   Development, and each extra ticket costs a real Claude session.
3. For each returned issue with no terminal `ticket_estimate` row:
   1. Read the issue; if `timetracking.originalEstimateSeconds` is already set, mark
      `skipped_existing` and move on.
   2. `git fetch origin`, resolve default branch, create the detached worktree.
   3. Run Claude (see Invocation). Gates 1-3.
   4. Write the estimate to Jira. Gate 4 (read-back).
   5. Mark `done`; remove the worktree.

Runs are serialised by the `PeriodicTimer` loop (`await RunOnceAsync` inside the tick), the
same shape as `PrAnnouncementWorker`. One ticket is estimated at a time.

## Claude invocation

Working directory is the worktree. Command:

```
claude -p "<prompt>"
       --output-format json
       --json-schema '<schema>'
       --restricted
       --permission-mode plan
       --strict-mcp-config
       --max-budget-usd <cap>
       --model <configured>
```

- `--restricted` removes Bash/PowerShell/REPL — the run is read-only by construction.
- `--permission-mode plan` blocks writes as a second barrier.
- `--max-budget-usd` bounds cost per ticket.
- `CLAUDE_CODE_OAUTH_TOKEN` is set on the child **only** when a token is stored; otherwise the
  child inherits the machine's Claude Code login.
- Hard wall-clock timeout (default 10 min). Kill uses `Process.Kill(entireProcessTree: true)`.
  On this machine `claude` is a native `claude.exe` rather than a node wrapper, so PATH lookup
  works directly - but it still spawns children, and killing only the parent would leave them
  holding the worktree open and break the next sweep's cleanup.

The CLI emits a JSON **array** of events; the payload is on the final element whose type is
`result`, which carries an already-parsed `structured_output` object beside the `result`
string. Confirmed against a live run.

Output schema:

```json
{
  "type": "object",
  "properties": {
    "implementation_minutes": { "type": "integer", "minimum": 1 },
    "testing_minutes":        { "type": "integer", "minimum": 1 },
    "review_minutes":         { "type": "integer", "minimum": 1 },
    "confidence":             { "type": "string", "enum": ["low", "medium", "high"] },
    "rationale":              { "type": "string", "minLength": 1 }
  },
  "required": ["implementation_minutes", "testing_minutes", "review_minutes",
               "confidence", "rationale"],
  "additionalProperties": false
}
```

The prompt supplies the ticket key, summary and description, and asks for the time **Claude
Code itself** would need in this codebase to implement, test, and pass code review —
explicitly instructing against padding or optimism.

## Trunk selection

`origin/HEAD` is not a reliable pointer to the branch carrying current work. In one tracked repo
it points at `mainTraining`, a stub holding a single `.gitignore` from an October "Initial
commit", while live work lands on rolling dated branches. Following it produced a confident,
plausible estimate of an empty directory - and no verification gate could have caught it, since
nothing downstream distinguishes an empty repository from an easy ticket.

Two mechanisms replace it:

**Family detection.** Trunk-shaped branches are grouped by their leading word and the family with
the most members wins - a repo that cuts dated snapshots repeats its prefix many times. Then the
newest branch in that family by commit date is used. Splitting at the first non-letter keeps
`mainTraining` out of the `main` family. Measured across the tracked repos: `master`(1);
`main`(57) beside `master`(1); `dev`(17) beside `main`(1); `dev`(15) beside `mainTraining`(1);
`main`(1) - each resolving to a real codebase with no configuration.

**An emptiness guard.** A prepared worktree with fewer than five tracked files is refused with a
notification naming the branch, rather than estimated.

`repo_branch` holds an optional per-repo override for a repo neither rule fits.

## Project matching

Three tiers, strongest first, each consulted only when the previous found nothing. A tier that
finds several candidates stops the search rather than falling through to a looser rule.

1. Exact normalised display name - `training-manager` to "Training Manager".
2. Exact normalised project key.
3. Prefix either direction - `dynatag` to "Dynatag-Codes".

The ordering is load-bearing. This account has both "Sheeponline" and "SheepOnline New", and the
repo `sheeponline-new` extends the first while exactly matching the second; resolving exact names
first is what stops the older board winning.

Pairings no rule can infer - `payload-site` is tracked in a board called "New site" - are set by
hand on the Repositories tab and stored with `auto_matched = 0`, so the matcher never replaces
them.

## Attachments

TM-50's entire description is a single ADF media node carrying no text: flattening it yields an
empty string, and the ticket was estimated from its summary alone. The attachment is frequently
the only statement of what the work is.

Readable attachments are downloaded into the worktree - inside it, because the run is sandboxed
to its working directory - and named in the prompt. Images and PDFs Claude reads natively. A
spreadsheet it cannot: the run is `--restricted`, with no Bash or REPL to open a workbook, so
`XlsxText` extracts the text (an `.xlsx` is a zip of XML, needing no new dependency) and writes
it beside as `.txt`. CSV and other text arrive as-is.

Bounded throughout, because attachments come from whoever filed the ticket: a fixed set of kinds,
six files per ticket, per-kind size ceilings, and filenames reduced to a bare name before they
build a path. Kind is decided by extension before MIME, since Jira reports many uploads as
`application/octet-stream`. No attachment failure fails an estimate.

Verified live on TM-50: the rationale cited `SmartyModifiers.php:202`, a line number that exists
only in the stack trace inside the screenshot, and reconciled it against the current source.

## Verification chain

A ticket reaches `done` only when all four gates pass.

| Gate | Check | Failure it catches |
|---|---|---|
| 1 Process | Exit code 0, JSON parses, `is_error` false | Timeout, crash, budget exhausted |
| 2 Schema | All five fields present, correctly typed | Run died before CLI-side validation |
| 3 Sanity | Minutes positive and within bounds; confidence in enum; rationale non-empty and not a placeholder | Structurally valid but useless output |
| 4 Effect | Re-read issue; `originalEstimateSeconds` equals what was written | **Jira accepts the PUT and silently ignores `timetracking` when the field is not on the edit screen or time tracking is disabled.** A 2xx does not mean the estimate landed. |

Gate 4 is the only gate backed by evidence rather than by Claude's word.

### Retry

- Gates 1-3 fail: re-run Claude once (a second session is the right fix).
- Gate 4 fails: retry the **write** once, not the session. Re-running a 10-minute session
  cannot fix a field missing from a Jira screen, and the estimate already in hand is fine.
- Two attempts maximum, then `status = failed` plus notification.

## Notifications

Via the existing `IUserNotifier`. Conditions: no `claude` executable, auth failure, fetch
failure, worktree failure, timeout, unparseable output, sanity failure, Jira write rejected,
project unmapped.

**Each distinct failure notifies once, not every 5 minutes.**

Per-ticket failures dedupe on `ticket_estimate.warned_at` (the pattern from
`PrAnnouncementWorker.cs:246`), so a restart cannot re-notify about the same ticket.

Repo-level faults - unmappable project, failed fetch, missing worktree - have no ticket row to
hold a flag, so they dedupe in memory for the lifetime of the process. A restart therefore
re-notifies once about a still-broken repo. That is the right side to err on: these are
configuration problems the user has to fix, and silence after a restart would hide them.

`urgent: true` for auth and missing-executable failures — nothing works until the user acts.
Normal priority for per-ticket failures. Messages name the gate and the underlying error,
e.g. "TM-42: estimate rejected by Jira (Original Estimate not on the edit screen)".

## Data model

New tables in `Data/Schema.sql`. Separate tables rather than `ALTER TABLE tracked_repo`:
`DatabaseInitializer` only runs `CREATE TABLE IF NOT EXISTS`, and SQLite has no
`ADD COLUMN IF NOT EXISTS`, so a new table stays idempotent with no migration machinery.

```sql
CREATE TABLE IF NOT EXISTS repo_project (
    repo_path    TEXT PRIMARY KEY COLLATE NOCASE,
    project_key  TEXT NOT NULL,
    auto_matched INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS claude_auth (
    id          INTEGER PRIMARY KEY CHECK(id = 1),
    token_dpapi BLOB NOT NULL
);

CREATE TABLE IF NOT EXISTS ticket_estimate (
    ticket_key      TEXT PRIMARY KEY,
    repo_path       TEXT NOT NULL,
    status          TEXT NOT NULL,          -- pending|done|failed|skipped_existing
    attempts        INTEGER NOT NULL DEFAULT 0,
    impl_minutes    INTEGER,
    test_minutes    INTEGER,
    review_minutes  INTEGER,
    confidence      TEXT,
    rationale       TEXT,
    raw_output      TEXT,
    error           TEXT,
    failed_gate     TEXT,
    estimated_at    TEXT,
    warned_at       TEXT
);
```

## Components

| File | Purpose |
|---|---|
| `Data/Schema.sql` | The three tables above |
| `Data/TicketEstimateRepository.cs` | Durable per-ticket state |
| `Data/ClaudeAuthRepository.cs` | Optional DPAPI token, `id=1` pattern from `AtlassianTokenRepository` |
| `Data/RepoProjectRepository.cs` | Repo path to project key |
| `Logic/RepoProjectMatcher.cs` | Pure: folder name against project name/key, normalised |
| `Logic/EstimateResult.cs` | Pure: parse plus gates 2 and 3 |
| `Platform/IClaudeEstimator.cs` | Seam for testing |
| `Platform/ClaudeCliEstimator.cs` | Spawn, timeout, process-tree kill |
| `Platform/IGitWorktreeManager.cs` plus impl | fetch, default-branch resolution, add/remove |
| `Jira/IJiraSearchSource.cs` | `SearchIssuesAsync(jql)` seam |
| `Jira/JiraApiClient.cs` | Add `SearchIssuesAsync`, `SetOriginalEstimateAsync`, `timetracking` on reads |
| `Services/TicketEstimationWorker.cs` | `BackgroundService`, `PeriodicTimer(5m)`, `internal RunOnceAsync` |
| `HostingExtensions.cs` | `AddClaudeServices` |
| `UI/SettingsPages` OAuth tab | Claude token section plus Test button |
| `UI/SettingsWindow.cs` Repositories tab | Project column, editable |

`git` is invoked via `ProcessStartInfo { FileName = "git" }`, matching `GitBranchProbe`.
The fetch runs with `GIT_TERMINAL_PROMPT=0` and `GCM_INTERACTIVE=never` so a private remote
without cached credentials fails fast instead of popping a Credential Manager dialog from a
tray app with no visible parent.

The `claude` executable is located by PATH with a settings override, since the daemon starts
from `HKCU\Run` where a user-local bin directory may not be on PATH.

## Testing

- `RepoProjectMatcher` and `EstimateResult` are pure — they carry most of the coverage.
- `TicketEstimationWorker` is tested against fake `IClaudeEstimator`, `IJiraSearchSource`,
  `IGitWorktreeManager` and `IUserNotifier`: no process spawn, no HTTP. Same approach as the
  existing `PrAnnouncementWorker` tests.
- Explicit cases: gate-4 failure (Jira silently drops the estimate), retry targeting, the
  two-attempt cap, notification dedupe, existing-estimate skip, unmapped repo.

## Out of scope

- Re-estimating when a ticket's description changes (store Jira `updated` if wanted later).
- A dashboard panel for estimates.
- Waiting on PollServiceGate before the first sweep. The five-minute first tick covers setup in
  practice; a first run during the wizard could raise one spurious projects-unreadable toast.
- Estimating anything outside `statusCategory = "To Do"`.
- Posting the rationale to Jira as a comment.
