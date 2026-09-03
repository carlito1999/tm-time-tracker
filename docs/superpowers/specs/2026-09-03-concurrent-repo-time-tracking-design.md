# Concurrent Per-Repo Time Tracking — Design Spec

**Date:** 2026-09-03
**Author:** Lefteris Tsegkos (in collaboration with Claude Code)
**Status:** Approved — implementation in progress
**Parent designs:**
- [2026-05-25-jira-time-tracker-daemon-design.md](2026-05-25-jira-time-tracker-daemon-design.md)
- [2026-05-25-multi-repo-detection-design.md](2026-05-25-multi-repo-detection-design.md)

---

## 1. Goal

Credit time to every repo that has work happening in it, concurrently. The daemon
currently attributes each minute to exactly one repo; when the user hand-edits one
project while a Claude Code session runs in another, only one of the two is paid.

Two defects follow from the single-slot model, and both are fixed here:

1. **Claude activity hijacks human attribution.** `ActiveRepoResolver` Tier 0 ranks
   Claude Code writes above foreground-window focus, so a long agent run in repo B
   takes the minutes the user is typing into repo A.
2. **The carry buffer leaks across repos.** `BranchChanged` carries no repo path, so
   unattributed minutes banked on repo A's `main` land on the next ticket branch
   checked out *anywhere* — up to 30 minutes onto an unrelated project's ticket.

## 2. Non-Goals

- Sub-minute accounting. A minute is still the indivisible unit.
- Splitting a minute between concurrent repos. Both get a full minute (see 3.1).
- Capping or idle-gating Claude time (see 3.2).
- Detecting *which ticket* within a repo when a repo has several in flight. One repo
  means one ticket at any instant — a stated invariant of the user's workflow.
- Any change to worklog submission, Jira polling, PR announcement, or Slack flows.
- New IDE title parsers, repo auto-discovery, or per-file granularity.

## 3. Confirmed Design Decisions

| Decision | Choice |
| -------- | ------ |
| Concurrent accrual | Full minute to each active repo; the day may exceed wall-clock time |
| Concurrency dimension | Repo path — state is keyed by repo, never by ticket |
| Human stream idle gating | Unchanged from today: idle/locked stops accrual |
| Claude stream idle gating | None. Presence at the desk is irrelevant; no cap |
| Attribution trigger | Pull at the 60s tick, not push on the 2s poll |
| Claude freshness test | `.jsonl` mtime newer than the previous tick (not a fixed window) |
| Carry buffer scope | Per repo, cap stays 30 minutes per repo |
| Resolver Tier 0 | Removed |
| `IdleStateMachine` Claude override | Removed |

### 3.1 Why a full minute to each

The user hand-edits one project while an agent works another; both are real work, and
both should be paid in full. The invariant "minutes logged <= wall-clock elapsed" is
deliberately abandoned. It held before only as an artifact of the single slot.

### 3.2 Why Claude streams ignore idle

Crediting unattended agent work is the primary motivation for the change. Gating it on
the user's presence would defeat it. There is no cap: an agent that runs for four hours
is credited four hours, and the dashboard is the review surface before submission.

The human stream keeps today's idle gating, so two open editors with nothing happening
accrue nothing.

## 4. Architecture

### 4.1 Two streams

```text
Every 60s (TimeAggregator tick):

  +------------------------------+
  | RepoActivityMonitor.Sample() |
  +---------------+--------------+
                  |
      +-----------+------------+
      v                        v
 HUMAN STREAM             CLAUDE STREAMS (0..N)
 exactly one repo         every tracked repo whose
 ActiveRepoResolver       .jsonl mtime > lastTickUtc
 (foreground -> HEAD      - ignores idle
  mtime -> sticky)        - no cap
 gated by idle state
      |                        |
      +-----------+------------+
                  v
        credit set: repo paths
                  v
        git branch -> ticket key (per repo in set)
                  v
      +--------------------------+
      | TimeAggregator           |
      |  per-repo {ticket,banked}|
      |  +1 min per distinct key |
      +--------------------------+
```

### 4.2 Why pull, not push

`TimeAggregator` today *receives* `BranchChanged` and holds one `_currentTicket`.
Under concurrency it instead *asks*, once per tick, which repos deserve a minute.

The rejected alternative is one `BranchWatcher`-style loop per tracked repo.
`GitBranchProbe` spawns a `git.exe` process per call, so N repos polled every 2s is a
process storm; and the aggregator would still have to merge N event streams at tick
time. Pulling costs N spawns per *minute*, and only for repos actually being credited.

For the human stream, pull is provably equivalent to push: `TickAsync` reads the
current ticket at tick time under either design.

`BranchChanged` survives, gains `RepoPath`, and becomes a dashboard-log event only —
nothing consumes it for attribution.

### 4.3 Why mtime-since-last-tick

A 60-second freshness window sampled by a 60-second timer has zero slack: any timer
drift or slow tick drops a minute silently. Comparing `.jsonl` mtime against the
previous tick timestamp tiles the timeline exactly and removes a constant to tune.

## 5. Components

| Component | Change |
|-----------|--------|
| `RepoActivityMonitor` | **New.** Returns the credit set for a tick: the human repo (if active) plus every Claude-active repo, each resolved to `(repoPath, branch, ticketKey)` |
| `ActiveRepoResolver` | Delete Tier 0 (Claude override). Tiers become window title -> HEAD mtime -> sticky |
| `TimeAggregator` | `_currentTicket`/`_carriedMinutes` scalars become `Dictionary<repoPath, RepoState>`; `TickAsync` pulls a sample and credits each distinct ticket |
| `IdleMonitor` | Drop `IsClaudeActiveForCurrentRepo` and the `ActiveRepoResolver`/`IClaudeCodeActivityProbe` dependencies it needed |
| `IdleStateMachine` | Drop the `claudeActive` parameter from `Observe` |
| `BranchChanged` | Add `RepoPath` |
| `DashboardWindow` | `UpdateClaudeBadge` reports Claude across all tracked repos, not just the resolved one |

### 5.1 `RepoActivityMonitor` contract

```csharp
public sealed record RepoActivity(string RepoPath, string? Branch, string? TicketKey);

public sealed class RepoActivityMonitor
{
    // humanActive: current UserActivityState is Active
    IReadOnlyList<RepoActivity> Sample(DateTime lastTickUtc, bool humanActive);
}
```

`Sample` resolves a git branch only for repos in the credit set. Repos that vanish
from disk are skipped and warned once, matching `ActiveRepoResolver`'s behavior.

### 5.2 `TimeAggregator` per-repo state

```csharp
private sealed class RepoState { public string? LastTicket; public int Banked; }
private readonly Dictionary<string, RepoState> _repos = new(StringComparer.OrdinalIgnoreCase);
```

Per tick, for each repo in the credit set:

- Ticket unchanged, or still `null` -> no transition.
- Ticket changed from `null`/other to a key -> `Carry()` that repo's banked minutes
  onto the new ticket, then clear that repo's bank.
- Ticket is `null` -> bank a minute into that repo's bucket, capped at 30.
- Ticket is non-null -> mark it for crediting.

Crediting is `_tickets.OpenOrCreateCycle(key)` + `IncrementMinute` for each key in the
tick's set, **deduplicated by ticket key**. Dedupe is a safety net, not a feature:
`OpenOrCreateCycle` returns the same open cycle for a given key regardless of repo, so
two repos on one ticket would otherwise double-credit it in a single minute.

Per-repo state stays in memory, for the same reason the old buffer did: a restart must
not resurrect ambiguous time and attach it to whatever is checked out next.

## 6. Error Handling

- A repo path that no longer exists is dropped from the credit set, warned once per
  session, and its in-memory state is left alone (a temporarily-unmounted drive
  shouldn't discard banked minutes).
- `GitBranchProbe` returning `null` (detached HEAD, git missing) yields a repo with a
  `null` ticket — it banks rather than credits, as today.
- A throwing Claude probe returns an empty snapshot and is warned once; the human
  stream is unaffected.
- An exception inside one repo's per-tick handling must not abort the tick for the
  others.

## 7. Testing

`TimeAggregatorTests` currently drives every case through
`ProcessEvent(new BranchChanged(...))` — roughly 20 call sites. Pull-at-tick rewrites
all of them against a fake `RepoActivityMonitor` sample source. This is the bulk of the
implementation effort and is expected, not incidental.

New coverage:

- Two repos active in one tick credit two tickets one minute each.
- A repo both focused and Claude-active is credited once.
- Two repos on the same ticket key credit that ticket once (dedupe).
- Banked minutes on repo A's `main` carry to repo A's next ticket and **never** to
  repo B's — the reported defect.
- A Claude-active repo accrues while the human stream is idle.
- A Claude-active repo that moves `main` -> ticket branch carries its own bank.
- The per-repo 30-minute cap holds independently per repo.
- `ActiveRepoResolver` no longer prefers a Claude-active repo over the foreground
  window (Tier 0 removal).

## 8. Known Limits

- A Claude session sitting in a long silent tool call writes no `.jsonl` and so accrues
  nothing for those minutes. This is the existing probe's fidelity and is not addressed.
- Claude Code runs in a terminal, which is not a foreground-window signal. While the
  user types prompts for repo B, the human stream stays sticky on repo A (the last VS
  Code window) while the Claude stream credits repo B. Deliberate.
- Two tickets in flight in one repo would silently pick one. Out of scope by the stated
  workflow invariant.
- Accrual can exceed wall-clock time by design; the dashboard is the review surface.
