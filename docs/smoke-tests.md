# Manual smoke tests (run before each release)

For each item, check the box only after you observed the expected outcome.

## Prerequisites (one-time, external)

Atlassian still requires manual registration of the OAuth 2.0 (3LO) app —
the wizard cannot create that for you. The app's Client ID + Secret are then
entered into the setup wizard on first run (no `secrets.json` file needed).

- [ ] At <https://developer.atlassian.com/console/myapps/>, create an OAuth 2.0
      (3LO) integration named e.g. `TmTimeTracker (personal)`.
- [ ] Add callback URL: `http://localhost:53682/callback`.
- [ ] Add Jira API permissions: `read:jira-work`, `write:jira-work`,
      `offline_access`.
- [ ] Note the Client ID and Secret for use in the setup wizard.

## 1. First-run wizard

- [ ] Delete `%LOCALAPPDATA%\TmTimeTracker\state.db` if it already exists.
- [ ] Launch the exe (double-click `TmTimeTracker.exe` on Desktop, or
      `dotnet run --project src\TmTimeTracker`).
- [ ] Within ~1 second, the tray icon appears AND the Setup window opens
      on page 1 (OAuth app).
- [ ] Paste Client ID + Client Secret → Next.
- [ ] On page 2, click Sign in to Atlassian → browser opens → approve.
- [ ] Browser shows "TmTimeTracker connected"; the wizard advances to "Connected!"
- [ ] Click Next → keep default paths and timing → Finish.
- [ ] Dashboard opens automatically.
- [ ] `state.db` now has rows in `oauth_app_config`, `oauth_state`, and `config`
      (verify via `sqlite3 %LOCALAPPDATA%\TmTimeTracker\state.db ".tables"`).

## 2. Dashboard reflects live state

- [ ] With daemon + dashboard open, switch git branches in the configured repo.
- [ ] Within 10 seconds: Branch and Ticket labels at top update.
- [ ] Recent events shows the BranchChanged event.

## 3. Live status transition (full pipeline)

- [ ] On a TM-XX branch, work normally for ~5 minutes (verify the pending
      row's Minutes column increments).
- [ ] Move the ticket "In Progress" → "Review" in Jira UI.
- [ ] Within 90 seconds, the daemon pops the edit form (same as before; the
      dashboard doesn't suppress it).
- [ ] Click Submit. Form closes.
- [ ] Verify the worklog at
      `https://<your-site>.atlassian.net/browse/TM-XX?focusedWorklogId=<id>`.
- [ ] Row disappears from the dashboard's pending list.

## 4. Submit Now (dashboard manual submit)

- [ ] In the dashboard, select a pending row with non-zero minutes.
- [ ] Click "Submit now".
- [ ] Verify the worklog appears in Jira within ~5 seconds.
- [ ] Row disappears from the pending list.

## 5. Discard (audit row stays)

- [ ] Select a pending row → click Discard → confirm Yes.
- [ ] Row disappears from pending.
- [ ] No worklog appears in Jira.
- [ ] Verify with `SELECT worklog_id FROM ticket_time ORDER BY id DESC LIMIT 1` —
      the latest row has `worklog_id` starting with `discarded:`.

## 6. Settings (tabbed mode)

- [ ] Right-click tray → Settings…
- [ ] Wizard opens; navigate to Paths page; change idle threshold to 5 min;
      Finish.
- [ ] Lock workstation; wait 6 minutes; unlock; verify minute samples in the
      relevant window have `is_idle=1`.

## 7. Lock/unlock

- [ ] Lock the workstation (Win+L) while the daemon is running.
- [ ] Wait 2 minutes.
- [ ] Unlock.
- [ ] Inspect `minute_sample` rows for the 2-minute window: `is_idle=1`.

## 8. Sleep/resume

- [ ] Put laptop to sleep for 10+ minutes.
- [ ] Wake.
- [ ] No spurious minute-tick increments during sleep window
      (`ticket_time.minutes_active` did not jump by 10).

## 9. Crash recovery

- [ ] Kill the daemon process mid-day (Task Manager → End task).
- [ ] Restart the exe.
- [ ] Open `ticket_time` rows still present and submittable; no duplicate
      cycles created.

## 10. Single-instance + auto-start

- [ ] With the daemon running, launch a second instance — the second exits
      silently.
- [ ] Verify auto-start:
      `reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v TmTimeTracker`
      → returns the exe path.
- [ ] Log out and back in — tray icon should reappear.

## 11. Migration from secrets.json (only if upgrading)

- [ ] With existing `secrets.json` present beside the exe and the new build
      installed for the first time, launch the daemon.
- [ ] On startup, `secrets.json` is renamed to `secrets.json.migrated`.
- [ ] Setup wizard opens at page 2 (Connect) because Client ID/Secret are
      already populated. Complete sign-in. Done.

## 12. Weekly report export

- [ ] Let the daemon run while you work in a tracked repo for a few minutes, then
      check the ledger filled:
      `sqlite3 %LOCALAPPDATA%\TmTimeTracker\state.db "select * from hour_activity;"`
      → rows land in the correct **local** hour with the correct repo path.
- [ ] Tray → **Export report…** — the window opens with From set to Monday of
      this week, To set to today, hours 08–16 and Min. minutes 0.
- [ ] The preview lists one row per hour, with untracked hours blank and a blank
      spacer row between days.
- [ ] Change the From date, the To date and the hour spinners — the preview
      refreshes on each change.
- [ ] Press **Export**. Explorer opens with the file selected in
      `Documents\TmTimeTracker\`.
- [ ] **Open the file in real Excel** (not only LibreOffice). It must open with
      **no repair prompt**, and keep: bold filled header, tinted Date/Time/Repo
      columns, wrapped Ticket column with several tickets on separate lines.
- [ ] Move the From or To date → the suggested file name follows it. Type a name
      of your own, move a date again → your name is kept.
- [ ] Clear the file name box and export again → the file is named
      `Lefteris-<from>-<to>.xlsx`.
- [ ] Type a name without an extension → `.xlsx` is appended.
- [ ] Pick a range with no tracked time → the status line reads
      "Nothing in this range" or shows blank rows, and export still writes a
      readable file.
