# Weekly hour-by-hour report — design

**Date:** 2026-09-21
**Status:** implemented

## Goal

One tray action that produces an `.xlsx` laying a range of days out hour by hour: which repo was
worked in, and which ticket, with three months of history kept locally.

| Date | Time | Repo | Ticket |
|------|------|------|--------|
| `21-09-26` | `08:00–09:00` | `sheeponline-new` | `SN-291-350: isolated-member studbooks` |
| `21-09-26` | `09:00–10:00` | | |
| `21-09-26` | `10:00–11:00` | `sheeponline-new / training-manager` | two tickets, one per line |

## Why a new table was unavoidable

The database could not answer this question before, and no amount of querying would have made it:

- `ticket_time` is a counter per (ticket, cycle). It has **no time axis** and **no `repo_path`**,
  and a cycle opens on the first credited minute and closes only on submit or discard — so one row
  can span weeks and cannot be cut at an hour or a week boundary.
- `TimeAggregator` keeps the repo → ticket map in a `Dictionary` **in memory on purpose**
  (`TimeAggregator.cs:46-48`): a restart must not resurrect ambiguous time and attach it to
  whatever branch happens to be checked out next. Correct decision, fatal for reporting.
- `minute_sample` has a schema, a repository and a 30-day pruner, but nothing in `src/` ever calls
  `Insert`. It is a ghost table and was left alone.
- Jira is no better. `WorklogRequestFactory` stamps `started` with **submission** time, so a
  Monday–Thursday cycle submitted on Friday lands entirely on Friday.

So the attribution has to be written down as it happens. `hour_activity` is that ledger:
`(hour_start, repo_path, ticket_key) → minutes`, upserted once per minute per active repo.

A new table rather than new columns, because `CREATE TABLE IF NOT EXISTS` silently skips an
existing table — a new column would also need an entry in `DatabaseInitializer.AddedColumns`.

## Decisions worth keeping

**The ledger is written at the sample, not at the credit.** `TimeAggregator.TickAsync` credits the
bucket for every `RepoActivity` the source returns, before `Accrue` resolves a ticket. A minute on
a branch carrying no ticket is banked in memory and reaches `ticket_time` later under the *next*
ticket, but the hour it was actually spent in still belongs to that repo.

*Consequence, by design:* the spreadsheet and the Jira worklog disagree about banked minutes. The
sheet shows when the work happened; the worklog shows what it was eventually billed to. Both are
correct for their own purpose.

**`hour_start` is local time without an offset.** The report answers "what did Tuesday morning look
like", which is a local-calendar question. `remember_entry` stores local for the same reason. The
retention cutoff is therefore computed from `IClock.LocalNow`, not `UtcNow`.

**The 5-minute floor is asymmetric.** Applied to the repo's *total* for the hour but to each ticket
*individually*. `TimeAggregator` credits every active repo concurrently, so without a floor a
one-minute Claude write would put an extra repo in the cell; but thresholding both on the ticket
would blank an hour split 4 + 4 across two tickets of one repo, which looks like a bug.

**Empty hours keep their row.** Requested explicitly. The shape of the day survives and gaps stay
visible rather than silently closing up.

**Ticket names come from a cache, not the network.** `JiraPollService` already requests
`fields=status,summary` on every poll and discarded the summary. `ticket_summary` stores it, so
export needs no network at all. A ticket with no cached row renders as the bare key.

**The writer is hand-rolled OOXML.** `Logic/XlsxText.cs` already reads `.xlsx` as a `ZipArchive` +
`XDocument` with an explicit "this needs no third-party library" rationale. Taking ClosedXML or
EPPlus would break that and grow a self-contained single-file exe already around 158 MB. It also
means `XlsxText` can read the writer's own output back, which is the round-trip test.

Excel is far stricter than the reader and repairs a malformed workbook **silently**, dropping all
styling without reporting an error. Three traps are covered by structural tests because none of
them fail loudly: `<fills>` must open with the reserved `none` and `gray125` defaults; `<cols>`
must precede `<sheetData>`; every row and cell needs an explicit reference.

## Shape

| Piece | File |
|-------|------|
| Ledger table + repository | `Data/Schema.sql`, `Data/HourActivityRepository.cs` |
| Write site | `Services/TimeAggregator.cs` (`TickAsync`) |
| 90-day retention | `Services/MaintenanceService.cs` (`RunOnce`) |
| Summary cache | `Data/TicketSummaryRepository.cs`, written by `Services/JiraPollService.cs` |
| Grid rendering (pure) | `Logic/WeekGrid.cs` |
| Workbook writing | `Logic/XlsxWriter.cs` |
| Orchestration | `Services/WeeklyReportExporter.cs` |
| Window | `UI/ExportReportForm.cs`, opened via `UI/WindowsHost.ShowExport()` |

All registrations sit in `AddTmTimeTrackerCore`, not `AddTrayUI`: `TimeAggregator` and
`JiraPollService` write these stores in every mode, and only the window is tray-specific.

## Known limits

- The ledger only fills while the daemon runs. It cannot reconstruct hours recorded before this
  feature existed, so the first useful report is a week of uptime away.
- Daily totals can exceed the wall clock, because concurrent repos each earn a full minute
  (`TimeAggregator.cs:11-13`). That is deliberate and the sheet reflects it.
- Claude-stream minutes ignore idle entirely, so an overnight session earns ledger time. Mostly
  outside an 08:00–17:00 window, but visible if the window is widened.
- An export-time backfill of missing summaries via `IJiraSearchSource` was considered and dropped:
  ledger minutes come from active cycles, and active cycles are polled every 90 s, so the cache
  fills for effectively every ticket that can appear. The only gap is a ticket submitted before its
  first poll.
