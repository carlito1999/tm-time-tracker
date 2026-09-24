# Jev for ticket time estimation — research + plan

## Context

**What Jev is.** TypeSafe AI's "System One" model (`jev-1.13`). Not a chat model: you send a
`state` (text or JSON) plus named typed questions, you get typed answers back — never prose.

| Primitive | Returns | Use here |
|---|---|---|
| `noul` | P(yes) 0–1 | presence features ("repro given?", "UI change?") |
| `choice` | pick + full distribution + confidence (≤255 options) | unordered routing |
| `score` | probability-weighted level index + per-level probs + confidence (2–10 levels) | ordinal features ("scope", "clarity") |

Two ways to call it, **same request body** `{ model, state, questions }`, same `answers` shape:

| Provider | Endpoint | Model id | Auth |
|---|---|---|---|
| **OpenRouter** (user's choice) | `POST https://openrouter.ai/api/v1/systemone` | `typesafe/jev-1.13` | `Bearer <OpenRouter key>` |
| TypeSafe direct | `POST https://api.typesafe.ai/v1/systemone` | `jev-1.13.0` | `Bearer <TypeSafe key>` |

OpenRouter adds `id`, `provider`, `usage.cost` (USD) to the response. Input $0.042/1M tokens,
output free; 32k-token state. **Text only** (no images/PDFs). No .NET SDK → plain `HttpClient`.
Known weaknesses (docs' jaggedness page): numeric calibration, counting, dates-as-text, and
accuracy drops as irrelevant state grows. The docs say outright it is *not* a replacement for the
LLM behind Claude Code — it is a decision primitive inside an app.

**The baseline that frames everything** (read-only against live `state.db`; 40 Claude-estimated
tickets, all cycles submitted, ≥15 tracked min):

| Predictor | Mean abs. error |
|---|---|
| Current Claude estimator (impl+test+review) | **66 min** |
| Leave-one-out global median (~75 min) | **62 min** |
| Leave-one-out per-project median | 62 min |

Log-correlation estimate↔actual 0.25; Claude over-estimates (median 102 vs 78); its self-reported
`confidence` does not predict its error. Today's estimator is statistically indistinguishable from
guessing the median. Two caveats: tracked minutes miss off-machine time (SN-346 = 3 min), and the
prompt asks for *Claude-session* minutes while `minutes_active` is human + Claude streams combined
(`TimeAggregator.cs:8-18`) — a target mismatch in the current design.

**So the question is "what's the cheapest thing that beats the median?"** Jev's docs-endorsed
shape for that is the autoresearch-feature-discovery cookbook: Jev answers a few ordinal/presence
questions about the text → numeric columns → a small regression fitted on labelled history. We
have **193 tickets with submitted worklogs** — the training set needs no Claude estimate per row.

## Plan

### Step 1 — Jev connection setting (OpenRouter key), always built

The user-requested setting, following the existing credential idiom exactly.

- `Data/Schema.sql`: new table (CREATE-only, no ALTER — same reasoning as `Schema.sql:131-133`):
  ```sql
  CREATE TABLE IF NOT EXISTS jev_credential (
      id          INTEGER PRIMARY KEY CHECK(id = 1),
      provider    TEXT NOT NULL,          -- 'openrouter' | 'typesafe'
      token_dpapi BLOB NOT NULL
  );
  ```
  `provider` is stored now because adding a column later means a new table.
- `Data/JevCredentialRepository.cs`: `Save(provider, token)` / `Get()` / `Clear()`, modelled on
  `ClaudeAuthRepository.cs` with the extra column; DPAPI via the shared `ITokenProtector`.
- `Jev/JevProvider.cs`: the two rows of the provider table above (base URL + pinned model id).
  The model id is **pinned, not `jev-latest`** — Step 4's fitted weights are only valid for the
  model version that produced the features.
- `Jev/IJevClient.cs` + `Jev/JevApiClient.cs`: one `AskAsync(state, questions)` → typed
  `JevAnswers` (noul / choice / score records, `usage.cost` nullable). Reads the credential per
  call, so a key saved in Settings works without a restart. Named `HttpClient` `"jev-api"`,
  registered next to `"gitlab-api"` in `HostingExtensions.cs:195-216`. 429/529 → one backoff
  retry; 401 → typed exception the callers turn into an urgent notify-once toast.
- `UI/SettingsPages/ApiTokensPage.cs`: a fourth section, "Jev (OpenRouter) — ticket estimation",
  reusing `TokenSection` plus a provider ComboBox (OpenRouter default). "Get a token" opens
  `https://openrouter.ai/settings/keys`. **Save and test** sends a one-noul probe through
  `JevApiClient` (verifies key + Jev routing + parsing in one call, ~$0.00003) and shows the cost
  from `usage.cost`; stores only on success, via the existing `Stored(...)` tail-only display.
- Tests: `JevApiClientTests` (WireMock, as `GitLabApiClientTests`: request body shape, bearer
  header, per-provider base URL, all three answer types, OpenRouter extra fields, 401/429),
  `JevCredentialRepositoryTests` (`SharedSqlite.NewInMemory()`), and add `IJevClient` to
  `ClaudeServiceWiringTests`.

### Step 2 — Phase 0 offline experiment (scratchpad Python, no daemon changes)

Decides whether Step 4 is worth building.

1. **Key**: the user sets `OPENROUTER_API_KEY` in their own user environment (not via `!`, so the
   key never enters the transcript). Plain `httpx` POSTs to the OpenRouter endpoint — no SDK, so
   the script doubles as a spec for `JevApiClient`.
2. **Label set**: `state.db` opened `mode=ro`; tickets whose cycles are all submitted and total
   `minutes_active` ≥ 15. Target = `log(minutes)`.
3. **State per ticket** via `twg jira`: summary, description flattened to text, issue type,
   project, has-attachments / has-GitLab-link flags. Deliberately minimal; no code.
4. **Rubric** (user contribution — see below): 5–8 `score`/`noul` questions. Encoding per the
   cookbook: `score` → mean + spread columns, `noul` → one column; plus `log(Claude minutes)` on
   rows that have one.
5. **Fit**: ridge regression on log-minutes, leave-one-out CV. Print MAE (minutes) for: LOO median,
   Claude alone, Jev alone, Jev + Claude.
6. **Go/no-go**: build Step 4 only if Jev (or Jev + Claude) beats the LOO median by ≥15% MAE.
   Output artifact on "go": `jev-estimator.json` = `{ model, questions, intercept, coefficients }`.

Cost: ~190 tickets × ~1.5k tokens × a few rubric iterations ≈ <1M tokens ≈ **<$0.05**.

### Step 3 — Readiness gate before the Claude session (needs no labelled data)

In `TicketEstimationWorker` just before `PrepareAsync` (`TicketEstimationWorker.cs:164`): one Jev
call with `noul` questions — is the requested change stated? is this a question/discussion rather
than work? P(actionable) low with confidence ≥ 0.6 (docs' "do not act" threshold) → new status
`skipped_unclear` (terminal, in `EstimateStatus.cs`), notify once via `warned_at`. **Tickets with
image/PDF attachments or a GitLab link always pass** — Jev cannot see them (TM-50 lived entirely in
a screenshot). No Jev key saved → gate is skipped, behaviour identical to today.

### Step 4 — Calibrated estimate (only if Step 2 says go)

- `jev-estimator.json` from Step 2 ships as an embedded resource — questions and weights in one
  file so they cannot drift apart.
- `Logic/JevEstimateCalibrator.cs` (pure): Jev answers (+ Claude minutes if Step 2 showed they
  add signal) → encoded columns → weights → minutes. Inserted between gate 3 and
  `EstimateRounding` (`TicketEstimationWorker.cs:~302`); rounding, gate 4 and the Jira write stay
  exactly as they are.
- New sibling table `ticket_estimate_jev (ticket_key PK, model, answers_json, predicted_minutes,
  created_at)` — raw answers stored so every future ticket becomes training data for a refit.
- If Step 2 shows Claude's minutes add nothing, a later change can skip the worktree session
  entirely (needs a typed seam above `EstimateResult`, since `IClaudeEstimator` returns CLI stdout
  — out of scope here).

## Decisions left to their step (not blocking)

- **Rubric** (Step 2) — the user writes the 5–8 questions in the script's `QUESTIONS` dict: which
  ticket traits really drive their time. Domain knowledge no model has.
- **What Original Estimate means** (Step 4) — the fitted model predicts *tracked worklog minutes*,
  which is what Jira compares the estimate against. Recommend switching the meaning to that;
  today's prompt asks for Claude-session minutes.

## Other places Jev fits (secondary, not in this plan)

- `Logic/RepoProjectMatcher.cs:22-60` — the no-rule cases (`payload-site` → "New site"): a
  `choice` over project names; suggest only, never auto-save below threshold.
- `Logic/ChannelSuggester.cs:11-27` — Slack channel suggestion, same shape.

## Critical files

New: `Data/JevCredentialRepository.cs`, `Jev/JevProvider.cs`, `Jev/IJevClient.cs`,
`Jev/JevApiClient.cs`, (Step 4) `Logic/JevEstimateCalibrator.cs`.
Modified: `Data/Schema.sql`, `HostingExtensions.cs`, `UI/SettingsPages/ApiTokensPage.cs`,
`Services/TicketEstimationWorker.cs`, `Data/EstimateStatus.cs`.
Tests: `tests/TmTimeTracker.Tests/Jev/JevApiClientTests.cs`, `Data/JevCredentialRepositoryTests.cs`,
`Services/TicketEstimationWorkerTests.cs` (gate cases, fake `IJevClient`),
`Services/ClaudeServiceWiringTests.cs`.

## Verification

- Step 1: `dotnet test` green; publish per the project's publish command (`--self-contained`,
  `IncludeNativeLibrariesForSelfExtract=true`), run the Desktop copy, paste the OpenRouter key,
  press Save and test → status shows accepted + cost; `jev_credential` row exists with a DPAPI
  blob, not plaintext.
- Step 2: script prints the MAE table; go/no-go is read straight off it.
- Step 3: worker tests for pass-through (attachments, no key) and skip; one live sweep on a
  deliberately vague To Do ticket, reading back `ticket_estimate.status`.
- Step 4: calibrator unit tests against a fixed weights JSON; live sweep, then gate 4 read-back of
  the Jira field.
