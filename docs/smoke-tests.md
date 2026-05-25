# Manual smoke tests (run before each release)

For each item, check the box only after you observed the expected outcome.

## Prerequisites (one-time)

- [ ] Register an OAuth 2.0 (3LO) app at https://developer.atlassian.com/console/myapps/
  - Name: `TmTimeTracker (lefteris-personal)`
  - Callback URL: `http://localhost:53682/callback`
  - Permissions → Jira API: `read:jira-work`, `write:jira-work`, `offline_access`
- [ ] Copy Client ID and Secret into `secrets.json` at solution root
  (mirror the shape of `secrets.example.json`)

## 1. First-run consent
- [ ] Delete `%LOCALAPPDATA%\TmTimeTracker\state.db` if it already exists.
- [ ] Run `dotnet run --project src\TmTimeTracker -- --login`.
- [ ] Browser opens Atlassian consent → approve → page shows "TmTimeTracker connected".
- [ ] Console prints "Login complete. Tokens stored."
- [ ] `state.db` now has a row in `oauth_state` (verify via
  `sqlite3 %LOCALAPPDATA%\TmTimeTracker\state.db "SELECT cloud_id FROM oauth_state"`).

## 2. Ticket probe
- [ ] Run `dotnet run --project src\TmTimeTracker -- --probe-jira TM-29`.
- [ ] Console prints e.g. `TM-29: Review (indeterminate)`.

## 3. Live status transition (full daemon)
- [ ] On a TM-XX branch in `c:\projects\training-manager`, ensure the ticket is
      currently "In Progress" in Jira UI.
- [ ] Launch `dotnet run --project src\TmTimeTracker` (no args → full daemon).
- [ ] Tray icon appears. Right-click shows menu (Pending worklogs, Open log folder, Quit).
- [ ] Work normally for ~5 minutes (verify by querying
      `SELECT ticket_key, minutes_active FROM ticket_time WHERE submitted_at IS NULL`).
- [ ] Move the ticket "In Progress" → "Review" in Jira UI.
- [ ] Within 90 seconds, the daemon pops an edit form with the proposed minutes
      and a bullet list of `.remember/` entries.
- [ ] Click Submit. Form closes.
- [ ] Verify the worklog at `https://<your-site>.atlassian.net/browse/TM-XX?focusedWorklogId=<id>`.
- [ ] `submitted_at` is now set on the corresponding `ticket_time` row.

## 4. Lock/unlock
- [ ] Lock the workstation (Win+L) while the daemon is running.
- [ ] Wait 2 minutes.
- [ ] Unlock.
- [ ] Inspect `minute_sample` rows for the 2-minute window: `is_idle=1`.

## 5. Sleep/resume
- [ ] Put laptop to sleep for 10+ minutes.
- [ ] Wake.
- [ ] No spurious minute-tick increments during sleep window (`ticket_time.minutes_active`
      did not jump by 10).

## 6. Crash recovery
- [ ] Kill the daemon process mid-day (Task Manager → End task).
- [ ] Restart `dotnet run --project src\TmTimeTracker`.
- [ ] Open `ticket_time` rows still present and submittable; no duplicate cycles.

## 7. Single-instance + auto-start
- [ ] With the daemon running, launch a second instance — the second should exit silently.
- [ ] Verify auto-start: `reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v TmTimeTracker`
      → returns the exe path.
- [ ] Log out and back in — tray icon should reappear.
