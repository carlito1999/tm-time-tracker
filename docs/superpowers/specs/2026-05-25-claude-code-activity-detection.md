# Claude Code Activity Detection — Design Spec

**Date:** 2026-05-25
**Status:** Approved — implementation in progress
**Parent designs:**
- [2026-05-25-jira-time-tracker-daemon-design.md](2026-05-25-jira-time-tracker-daemon-design.md) (§3 originally listed Claude Code presence as a time-source input)
- [2026-05-25-multi-repo-detection-design.md](2026-05-25-multi-repo-detection-design.md)

---

## 1. Goal

Credit time on a TM ticket while Claude Code is actively working on the
corresponding repo, even when the user has no keyboard/mouse activity. Closes
the "I asked Claude to do something and went to grab coffee" gap in the current
daemon, which stops crediting after 10 minutes of input idle.

## 2. Non-Goals

- Credit time when Claude Code is active in a repo that is **not** in the
  tracked-repo list (no automatic enrollment).
- Credit time when the Windows session is locked (lock screen is "provably away"
  and overrides Claude activity).
- Detect Claude Code subprocess-level state (which model, which tool, etc.) —
  only "session file written recently."
- Integrate with Claude Code hooks (`Stop`, `UserPromptSubmit`) — file-mtime
  polling is sufficient and requires zero setup outside the daemon.

## 3. Confirmed Design Decisions

| Decision | Choice |
| -------- | ------ |
| Detection method | Poll `%USERPROFILE%\.claude\projects\*\*.jsonl` mtimes every 5 s |
| Activity threshold | Claude is "active" for a project if its latest `.jsonl` mtime is within the last 60 s |
| Behavior when Claude active for current repo | Override idle threshold — treat user as Active regardless of `GetLastInputInfo` |
| Behavior when session is locked | Lock wins. Locked + Claude-active → still Idle |
| Tier-0 resolver priority | Claude-active beats VS Code window detection (explicit work signal beats focused window) |
| Per-project credit | Yes — slug-to-tracked-repo mapping ensures Claude-active in repo X credits TM ticket on branch of X, not the previously-focused repo |
| Slug derivation | `path.Replace(":", "-").Replace("\\", "-").Replace("/", "-").ToLower()` — mirrors how Claude Code itself derives folder names |

## 4. Architecture

```text
                  ┌─────────────────────────────────────────┐
                  │ %USERPROFILE%\.claude\projects\         │
                  │   c--projects-tm-time-tracker\          │
                  │     f2042f88-...jsonl  (mtime = 13:42)  │
                  │   c--projects-other-repo\               │
                  │     a1b2c3d4-...jsonl  (mtime = 12:01)  │
                  └────────────────┬────────────────────────┘
                                   ▼
                  ┌────────────────────────────────┐
                  │ FileClaudeCodeActivityProbe    │
                  │   Snapshot(): IReadOnlyDict<   │
                  │     ProjectSlug, DateTime>     │
                  └────────────────┬───────────────┘
                                   ▼
              ┌────────────────────────────────────────┐
              │ ActiveRepoResolver (modified)          │
              │   Tier 0: Claude-active for tracked-   │
              │           repo within last 60s         │
              │   Tier 1: VS Code window title         │
              │   Tier 2: .git/HEAD mtime              │
              │   Tier 3: sticky last                  │
              └────────────────┬───────────────────────┘
                               ▼
                      Active repo + ticket
                               ▼
              ┌────────────────────────────────────────┐
              │ IdleMonitor (modified)                 │
              │   ON each tick:                        │
              │     claudeActive = probe.IsActive(     │
              │       Slug(activeRepo), now-60s)       │
              │     stateMachine.Observe(              │
              │       idleSec, locked, claudeActive)   │
              └────────────────┬───────────────────────┘
                               ▼
              ┌────────────────────────────────────────┐
              │ IdleStateMachine (modified)            │
              │   Active when:                         │
              │     !locked AND (idleSec < threshold   │
              │                  OR claudeActive)      │
              └────────────────┬───────────────────────┘
                               ▼
                         ActivityChanged event
                               ▼
                    TimeAggregator increments
```

## 5. Component Responsibilities

| Component | New / Modified | Responsibility |
|-----------|----------------|----------------|
| `Logic/ClaudeProjectSlug.cs` | New | Pure path-to-slug derivation; unit-tested |
| `Platform/IClaudeCodeActivityProbe.cs` | New | Interface: returns slug → latest-mtime snapshot |
| `Platform/FileClaudeCodeActivityProbe.cs` | New | Scans `~/.claude/projects/*/*.jsonl`; caches snapshot per call for cheap repeated lookups |
| `Logic/IdleStateMachine.cs` | Modified | `Observe(idleSec, locked, claudeActive)`. New parameter is opt-out (defaulting to `false` keeps existing behavior + existing tests) |
| `Services/ActiveRepoResolver.cs` | Modified | Tier-0 Claude check before existing tiers. Exposes `LastResolution` getter for `IdleMonitor` |
| `Services/IdleMonitor.cs` | Modified | Per tick: consult probe for current active repo's slug, pass to state machine |
| `UI/DashboardWindow.cs` | Modified | New status-strip indicator: "Claude: ●" |

## 6. Algorithm Details

### 6.1 Project-slug derivation

```csharp
public static string FromPath(string repoPath) =>
    repoPath
        .Replace(":", "-")
        .Replace("\\", "-")
        .Replace("/", "-")
        .ToLowerInvariant();
```

Example: `C:\projects\tm-time-tracker` → `c--projects-tm-time-tracker`

This matches the convention Claude Code itself uses to name project folders
(empirically verified on the development machine 2026-05-25).

### 6.2 Probe snapshot

```text
Snapshot() returns: { slug → maxMtime } for every folder under
%USERPROFILE%\.claude\projects\, taking the max mtime across all .jsonl files
in each folder (handles the case where multiple session files exist per project).

Cost: one EnumerateDirectories call + one EnumerateFiles per project folder.
Typical user has <10 project folders → <50 stat() calls. <10 ms.
```

### 6.3 ActiveRepoResolver tier-0 insertion

```csharp
public ActiveResolution? Resolve()
{
    var tracked = _repos.GetAll();
    if (tracked.Count == 0) { _last = null; return null; }

    var now = _clock.UtcNow;

    // NEW Tier 0: Claude-active for any tracked repo
    var claude = _claudeProbe.Snapshot();
    var freshClaude = tracked
        .Select(r => (r, mtime: GetClaudeMtime(claude, r.Path)))
        .Where(x => x.mtime.HasValue && (now - x.mtime!.Value) <= TimeSpan.FromSeconds(60))
        .OrderByDescending(x => x.mtime!.Value)
        .FirstOrDefault();
    if (freshClaude.r is not null)
    {
        _last = LookupBranch(freshClaude.r.Path);
        return _last;
    }

    // ... existing Tier 1/2/3 unchanged
}

private static DateTime? GetClaudeMtime(IReadOnlyDictionary<string, DateTime> snap, string repoPath)
{
    var slug = ClaudeProjectSlug.FromPath(repoPath);
    return snap.TryGetValue(slug, out var mtime) ? mtime : null;
}
```

### 6.4 IdleStateMachine override

```csharp
public void Observe(long idleSeconds, bool isLocked, bool claudeActive = false)
{
    var next = isLocked
        ? UserActivityState.Idle
        : (claudeActive || idleSeconds < _threshold.TotalSeconds)
            ? UserActivityState.Active
            : UserActivityState.Idle;
    if (next == Current) return;
    Current = next;
    OnTransition?.Invoke(next);
}
```

Default `claudeActive = false` keeps existing callers and tests untouched.

### 6.5 IdleMonitor wiring

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    var threshold = TimeSpan.FromSeconds(_config.Get().IdleThresholdSeconds);
    var sm = new IdleStateMachine(threshold);
    sm.OnTransition += state => { /* unchanged */ };
    await _bus.PublishAsync(new ActivityChanged(sm.Current, _clock.UtcNow), stoppingToken);

    while (!stoppingToken.IsCancellationRequested)
    {
        try
        {
            var idleSec = _probe.SecondsSinceLastInput();
            var locked = _probe.IsSessionLocked();
            var activeRepo = _resolver.LastResolution()?.RepoPath;
            var claudeActive = activeRepo is not null && IsClaudeActiveFor(activeRepo);
            sm.Observe(idleSec, locked, claudeActive);
        }
        catch (Exception ex) { /* unchanged */ }
        await Task.Delay(_sampleInterval, stoppingToken);
    }
}

private bool IsClaudeActiveFor(string repoPath)
{
    var snap = _claudeProbe.Snapshot();
    var slug = ClaudeProjectSlug.FromPath(repoPath);
    if (!snap.TryGetValue(slug, out var mtime)) return false;
    return (_clock.UtcNow - mtime) <= TimeSpan.FromSeconds(60);
}
```

Adds: `ActiveRepoResolver` dependency, `IClaudeCodeActivityProbe` dependency.
Also exposes `ActiveRepoResolver.LastResolution()` so `IdleMonitor` can check
the same repo the resolver picked (avoiding a redundant resolve).

## 7. Error Handling

| Failure | Response |
|---------|----------|
| `~/.claude/projects/` doesn't exist | Probe returns empty dict; daemon behaves as if no Claude is installed |
| Permission denied on a project folder | Skip that folder silently; log warning once per session |
| `.jsonl` file is locked while being written | `File.GetLastWriteTimeUtc` doesn't lock-fight; succeeds regardless |
| Slug derivation produces a name that no Claude folder uses | No match in snapshot → treated as not-active. Safe default |
| `ActiveRepoResolver.LastResolution()` is null (first tick, no tracked repos) | `IdleMonitor` falls back to `claudeActive = false` |

## 8. Testing

| Component | Tests |
|-----------|-------|
| `ClaudeProjectSlug` | Round-trip for typical Windows paths; case insensitivity; forward slashes; colon in drive letter |
| `FileClaudeCodeActivityProbe` | Integration test with temp directory: 0 projects → empty; 1 project with 1 file → snapshot has it; 1 project with 3 files → snapshot has max mtime; missing root → empty |
| `IdleStateMachine` (existing tests + new) | New tests: `claudeActive=true && idleSec=999 → Active`; `claudeActive=true && locked=true → Idle`; existing 7 tests still pass with no claudeActive parameter |
| `ActiveRepoResolver` (existing tests + new) | New tests: Claude-active beats VS Code window; Claude-active beats polling; Claude-active for untracked repo is ignored; 60s threshold honored |
| Dashboard badge | Manual smoke |

## 9. Acceptance Criteria

1. With Claude Code running and writing to a session file for tracked repo X, the daemon credits time to X's current TM ticket even with no kbd/mouse input for 30+ minutes.
2. With Claude Code active but the Windows session locked, no time is credited.
3. With Claude Code active for an UNTRACKED project, no time is credited (and no spurious tracking begins).
4. With Claude Code idle, daemon behavior is unchanged from previous build (existing 88 tests still pass).
5. Dashboard shows a "Claude: ●" indicator updating within ~5s of session-file changes.

## 10. Open Questions / Deferred

- 60-second activity window is hard-coded. If too aggressive (treats brief 50s pauses as "still working"), could be made configurable. Not in v1.
- Claude Code hook integration (`Stop`/`UserPromptSubmit` POST to local listener) — instant signal, but adds setup friction. Deferred unless polling proves laggy.
- Per-project slug verification — if Claude Code changes its slug derivation in a future version, ours diverges. Currently no fallback to scan all slugs for path-substring match. YAGNI for v1.
