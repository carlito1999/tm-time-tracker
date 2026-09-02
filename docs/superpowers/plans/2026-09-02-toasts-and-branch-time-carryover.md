# Windows toast notifications + branch-time carryover

Two independent changes, built in order. Phase 1 replaces the tray balloon with a real
Windows toast that can break through Do Not Disturb. Phase 2 stops discarding time spent
on non-ticket branches.

## Background a newcomer needs

`PrAnnouncementWorker` announces a ticket's pull request in Slack once the ticket reaches
the review status. When Jira still reports no pull request 10 minutes after the branch's
last commit, it notifies the user to announce manually — that notification is what Phase 1
rewrites.

Two facts that shape the work:

- `JiraPullRequestSource.GetSnapshotAsync` returns `DevInfoSnapshot.Empty` on **every**
  failure, including the current `401 scope does not match`. `WarnIfOverdue` returns early
  when `LastCommitUtc is null`, so the notification cannot fire at all until the
  `read:dev-info:jira` scope is granted. A `--test-toast` flag is therefore the only way to
  verify Phase 1 end to end.
- `TimeAggregator` records a minute only when a ticket is active. Time on `main` is
  discarded, not stored under another key — Phase 2 has to capture it before it can move it.

## Phase 1 — toast notifications

### Task 1.1 — TFM bump and package

- `src/TmTimeTracker/TmTimeTracker.csproj`: `net8.0-windows` -> `net8.0-windows10.0.17763.0`.
- Add `PackageReference Include="Microsoft.Toolkit.Uwp.Notifications" Version="7.1.3"`.
- `dotnet build` must be clean before touching anything else. The TFM change alters the
  WinRT projection set; treat a build break here as a blocker, not something to work around.

### Task 1.2 — the notifier

Replace `IBalloonNotifier`'s tray-balloon implementation with a toast.

- Signature becomes `Show(string title, string body, bool urgent = false)`. The
  `ToolTipIcon` parameter goes away, so `UseWindowsForms` can come back out of the test
  project.
- Build the toast XML directly rather than via `ToastContentBuilder`: the toolkit's
  `ToastScenario` enum has only `Default/Alarm/Reminder/IncomingCall`, so `urgent` has to be
  set as a raw attribute on the root `<toast>` element.
- Send through `ToastNotificationManagerCompat.CreateToastNotifier().Show(...)`. This is the
  API that works for unpackaged Win32 apps with no Start Menu shortcut; it self-registers an
  AUMID and COM entry under HKCU on first use.
- Wrap the send in try/catch and fall back to the existing tray balloon. Notifications can be
  disabled at OS or policy level, and a missed warning is worse than an ugly one.

### Task 1.3 — activation guard

`Program.cs` has no single-instance mutex. Clicking a toast COM-activates the exe, which
would start a second daemon. Guard at the very top of `Main`:
`if (ToastNotificationManagerCompat.WasCurrentProcessToastActivated()) return;`

### Task 1.4 — `--test-toast`

Alongside the existing `--probe-devstatus`, a flag that fires one urgent toast and exits.
This is the verification path for the whole phase, since the 401 blocks the real trigger.

### Task 1.5 — worker wiring

`WarnIfOverdue` passes `urgent: true`. Keep the once-per-ticket `HasWarned` gate: "notify me
anyway" means skip the de-dupe against manual Slack posts, not fire every 30 seconds.

### Verification

`dotnet test` green, then publish with the project's required command
(`--self-contained`, `-p:IncludeNativeLibrariesForSelfExtract=true`) and run `--test-toast`
**from the published exe**. A green build is not evidence the single-file bundle resolves the
WinRT types. Only then copy to the Desktop.

## Phase 2 — branch-time carryover

Decided with the user: only **unattributed** time carries, capped at **30 minutes**.

### Task 2.1 — buffer unattributed minutes

In `TimeAggregator`, when active but `_currentTicket is null`, increment an in-memory
unattributed counter instead of dropping the minute. Cap it at 30; once full, stop counting
rather than sliding the window, so the buffer always means "the last stretch of untracked
work, up to half an hour".

### Task 2.2 — attribute on switch

On `BranchChanged` to a branch that *has* a ticket key, add the buffered minutes to that
ticket's cycle and clear the buffer. Switching between two ticket branches must not move
anything — the buffer is empty in that case by construction, which is what makes this safe.

### Task 2.3 — reset rules

Clear the buffer on app restart (it is in-memory, so this is free) and when the user goes
idle long enough that the aggregator already stops counting.

### Tests

- Time on main then switching to a ticket branch credits the ticket.
- Time on TM-101 then switching to TM-202 credits neither retroactively.
- Buffer never exceeds 30 minutes.
- Buffer is consumed once, not re-applied on a later switch.
