# Claude ticket estimation — implementation plan

Spec: [2026-09-03-claude-ticket-estimation-design.md](../specs/2026-09-03-claude-ticket-estimation-design.md)

Built TDD, in dependency order. Each phase ends green.

## Phase 1 — Pure logic

- `Logic/RepoProjectMatcher.cs` — repo folder name to Jira project, normalised on display
  name first, key second. Returns null on no match or ambiguity; never guesses.
- `Logic/EstimateResult.cs` — parses the `--output-format json` envelope plus the inner
  schema'd payload. Implements gates 2 (schema) and 3 (sanity).

## Phase 2 — Persistence

- `Data/Schema.sql` — `repo_project`, `claude_auth`, `ticket_estimate`.
- `Data/RepoProjectRepository.cs`
- `Data/ClaudeAuthRepository.cs` — DPAPI, `id=1`, mirrors `AtlassianTokenRepository`.
- `Data/TicketEstimateRepository.cs` — status transitions, attempts, warn dedupe.

## Phase 3 — Jira

- `Jira/IJiraSearchSource.cs` — search seam.
- `JiraApiClient.SearchIssuesAsync` — `/rest/api/3/search/jql`, falling back to the legacy
  `/rest/api/3/search` on 404/410.
- `JiraApiClient.SetOriginalEstimateAsync` — `PUT /rest/api/3/issue/{key}`.
- Extend issue reads with `timetracking` so gate 4 can compare.

## Phase 4 — Process seams

- `Platform/IGitWorktreeManager.cs` + `GitWorktreeManager.cs` — fetch (with
  `GIT_TERMINAL_PROMPT=0`, `GCM_INTERACTIVE=never`), default-branch resolution, add/remove.
- `Platform/IClaudeEstimator.cs` + `ClaudeCliEstimator.cs` — spawn, timeout,
  `Kill(entireProcessTree: true)`, optional `CLAUDE_CODE_OAUTH_TOKEN`.

## Phase 5 — Worker

- `Services/TicketEstimationWorker.cs` — the four gates, targeted retry, two-attempt cap,
  deduped notifications. Tested entirely against fakes.

## Phase 6 — Wiring and UI

- `HostingExtensions.AddClaudeServices`
- OAuth tab: Claude token section + Test button.
- Repositories tab: project column.

## Verification

`dotnet test` green at every phase; full suite green at the end.
