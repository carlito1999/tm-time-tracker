# Claude "idle" while a tool call is running

## Root cause (confirmed empirically)

The only liveness signal the daemon reads is the mtime of the session transcript
`~/.claude/projects/<slug>/*.jsonl`. Claude Code appends to that file when a message
completes, so the mtime **freezes for the entire duration of a tool call**.

Measured inside one 100s Bash call: mtime stuck at 13:37:17 while the clock ran to 13:38:34
(77s stale). A scan of every directory under `~/.claude` during a 75s call found exactly one
file written - the transcript itself, at the *start* of the call. There is no heartbeat file.

Consequences, both from the same cause:
- `DashboardWindow.UpdateClaudeBadge` (60s window) shows "Claude idle" after one minute.
- `RepoActivityMonitor.Sample` credits a Claude repo only when `mtime > lastTickUtc`, and the
  tick is 1 minute - so a 10-minute test run earns **zero minutes**. This is the real damage;
  the badge is only the visible half.

## The signal we should have been reading

`~/.claude/sessions/<pid>.json` is maintained by Claude Code itself and carries:

    { "pid": 13328, "cwd": "C:\projects\tm-time-tracker",
      "procStart": "134329052784643818", "status": "busy", ... }

`status` is a **latch, not a heartbeat**: measured `busy` across an entire 80s call with the
file mtime frozen. It flips to `idle` when the turn ends - exactly "until Claude specifically
stops". `cwd` maps to the repo directly, and `pid` + `procStart` give a liveness oracle.

## Rule

A repo is Claude-active this tick iff **either**:
- `transcript mtime > lastTickUtc` - today's rule, unchanged, and
- some session has `status == "busy"` AND `cwd` == the repo path AND the pid is alive with a
  start time matching `procStart`.

No time cap: the user chose "forever until Claude stops". Process death is the safety valve -
without it a session killed mid-tool-call would bill indefinitely, since it never writes a
stop record.

Deliberate choices:
- **Positive match on `busy` only.** Statuses seen: `busy`, `idle`, absent (v2.1.258 and fresh
  sdk-cli files). Any unknown/absent value falls through to the mtime rule, so every failure of
  the new signal degrades to exactly today's behaviour.
- **Exact `cwd` match, not prefix.** A worktree session under a repo would be billed against
  the *main* checkout's branch, i.e. the wrong ticket, and would break the one-ticket-per-repo
  invariant `TimeAggregator` keys on. Worktrees earn nothing today; that stays true here.
- **Strict liveness.** Any exception resolving the process = not verifiably alive = not active.
  This is what makes an uncapped window safe against PID reuse.

## Steps

1. `Logic/ClaudeSessionActivity.cs` - pure: sessions + repos + liveness -> busy repo set.
2. `Logic/ClaudeRepoActivity.cs` - the one shared combined rule both consumers call.
3. `Platform/IClaudeSessionProbe` + `FileClaudeSessionProbe` (fake-root overload for tests).
4. `Platform/IProcessLiveness` + `Win32ProcessLiveness` (FILETIME vs Process.StartTime, +/-2s).
5. Wire `RepoActivityMonitor.Sample` and `DashboardWindow.UpdateClaudeBadge` to the shared rule.
6. DI in `HostingExtensions`, constructor threading through `WindowsHost`.
7. Spec update.

## Verification

`tm-time-tracker` is not a tracked repo, so this session must NOT light the badge - that is
correct behaviour, not a failure. The live probe is the user's sheeponline-new session: badge
reads "Claude active" while its session json says `busy`, a minute lands for that repo during
a >60s tool call, and the badge follows back to idle within one tick when status flips.
