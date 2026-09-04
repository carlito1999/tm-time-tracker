# Read pull requests from Bitbucket, not Jira's mirror

## Why

On 2026-09-04 SN-291 was announced in Slack linking pull request **353** when the live pull
request was **367**. The evidence, from the daemon log and the `pr_announcement` row:

| Time (UTC)        | Event                                                      |
| ----------------- | ---------------------------------------------------------- |
| `05:04:17.554`    | PR 367 created (its `lastUpdate` in Jira)                   |
| `05:04:32.737`    | SN-291 moved to Review; announcement row queued             |
| `05:04:32.759`    | `GET /rest/dev-status/1.0/issue/detail?issueId=28611`       |
| `05:04:34.000`    | Slack message posted, `pr_url = .../pull-requests/353`      |

The daemon asked **15.2 seconds** after the pull request was created, and posted **1.3 seconds**
after the transition. Jira's dev-status is a webhook-fed *mirror* of Bitbucket, and it had not
ingested 367 yet.

This is provable rather than inferred. Re-fetching the same endpoint afterwards returns 367, 353
and 352 all `OPEN`; under the current rule (prefer `OPEN`, then newest `lastUpdate`) a response
containing 367 must return 367. It returned 353, so 367 was absent.

Two further defects surfaced in the same payload:

1. **Cross-ticket contamination.** dev-status returns PR 297 (branch `SN-246-273-notifs-&-email`)
   and PR 358 (branch `SN-295-369-Code-clean-up`) under SN-291. PR 297's `lastUpdate` is now the
   newest of the whole set, so SN-291 announced today would link *SN-246's* pull request.
2. **`OPEN` carries no signal in this workflow.** Branches are re-PR'd against each new
   `main-DD-MM-YYYY` snapshot, so 352, 353 and 367 are all `OPEN` simultaneously. The filter meant
   to skip stale merged/declined pull requests skips nothing.

The fix for all three is the same: ask Bitbucket, which has no ingestion lag and can be queried
per branch. The plumbing for this already exists and was never connected — `BitbucketRemote.Parse`
(referenced nowhere outside its own file) turns a git remote into workspace/slug, and
`BitbucketApiTokenRepository` holds a `read:pullrequest:bitbucket` token that no read path uses.

## Ranking rule

Freshest = **highest numeric pull request id**, not newest `lastUpdate`. Decided with the user.
Bitbucket ids are monotonic per repository, so 367 > 353 > 352 permanently, whereas `lastUpdate`
lets a comment on a stale pull request win it back - which is exactly how 353 was chosen.

## Phases

### Phase 1 - Bitbucket client

- `IBitbucketPullRequestClient` + `BitbucketApiClient` using `BitbucketApiTokenRepository`
  (basic auth: email + scoped token, the same shape as `BasicAuthDevStatusClient`).
- `GET /2.0/repositories/{workspace}/{slug}/pullrequests?q=source.branch.name~"SN-291"&state=OPEN`
  Paging ignored: a single branch never has enough pull requests to page.
- Every failure returns empty, never throws - matching `BasicAuthDevStatusClient`'s contract, so a
  missing token or a Bitbucket outage degrades to today's behaviour instead of breaking the worker.
- **Verified live on 2026-09-04** via `--probe-devstatus 28611 SN-291`: Bitbucket answered 200 to
  `q=source.branch.name ~ "SN-291"` with the repeated `state` params and the `fields` projection,
  and the chain returned pull request **367** where the old dev-status path returned 353.

### Phase 2 - Resolve ticket to repository

- Add `GetRemoteUrl(repoPath)` to `IGitBranchProbe` (or a sibling `IGitRemoteProbe`).
- `JiraProjectKey.From("SN-291")` -> `SN` -> `RepoProjectRepository.GetAll()` filtered to `SN` ->
  repo paths -> `BitbucketRemote.Parse(remote)` -> `BitbucketRepo(workspace, slug)`.
- Several repos may map to one project; query each and merge candidates.

### Phase 3 - Selector

- `PullRequestSelector.Best(candidates, ticketKey)`.
- Filter to candidates whose source branch or title matches the ticket key on a boundary:
  `(?<![A-Za-z0-9])SN-291(?![0-9])`, case-insensitive, so `SN-29` never matches `SN-291`.
- Then prefer `OPEN`, then order by numeric id descending, `lastUpdate` only as a tiebreak for
  non-numeric ids.
- Add `SourceBranch` to `PullRequestCandidate` and to `DevStatusPullRequest` (dev-status carries
  `source.branch` and the DTO currently drops it), so the same filter works on the fallback path.
- **When nothing references the ticket, report no pull request** rather than falling back to the
  unfiltered list. Falling back is what would re-admit PR 297. The whole complaint being fixed is
  "it posted the wrong pull request", so silence plus the Phase 4 toast beats a confident wrong
  link.

### Phase 4 - Staleness gate, capped at 10 minutes

Belt and braces for the case where Bitbucket itself is briefly behind, and the reason the whole
bug was reported.

- Refuse to announce a pull request that predates the announcement row's `queued_at` by more than
  **4 hours**; return `Waiting` so the poll loop retries. A judgement call, not a measurement - the
  observed miss was 15h56m stale, so "more than a day" (the first instinct) would not have caught
  it. Four hours still catches it, while tolerating the ordinary case of opening a pull request in
  the morning and moving the ticket after lunch.
- **The wait is capped at 10 minutes.** Waiting forever is its own failure: the user hears nothing
  and the announcement silently never happens. At 10 minutes past `queued_at` with still no fresh
  pull request, fire the urgent Windows notification and hand the job to the user.
- **After the hand-off the ticket is not auto-announced.** Posting the stale link is the reported
  bug, and posting a late correct link after the user has already announced by hand would duplicate
  the message - which the toast-notifier work exists to prevent. Silence plus an urgent toast is
  the safer failure. `ForgetAsync` still clears the row when the ticket leaves review, so a ticket
  that comes back to review announces again normally.
- **Hazard:** the `Waiting` path today calls `WarnIfOverdue`, which anchors on `LastCommitUtc`.
  SN-291's branch `lastCommit` is from 09-03, so a staleness-`Waiting` reusing that branch fires
  the toast instantly. Staleness has its own deadline measured from `queued_at`, and the
  under-deadline case must record the attempt without warning.

### Phase 5 - Wire up and verify

- `BitbucketPullRequestSource` becomes the registered `IPullRequestSource`; Jira dev-status stays
  as the fallback when no Bitbucket token is configured, and as the source of `LastCommitUtc` for
  the overdue warning.
- Delete the disproven comment in `PrAnnouncementWorker.ListenAsync` ("usually Jira already knows,
  and waiting 30s would be silly") and record the 15-second evidence in its place.
- Full test run, then deploy and confirm against a real transition.

## Tests to write first

- Selector: a foreign-ticket pull request with a newer `lastUpdate` loses to the ticket's own.
- Selector: 367 beats 353 beats 352 by id, all `OPEN`, ignoring `lastUpdate` order.
- Selector: `SN-29` does not match branch `SN-291-350-individual-breeders`.
- Client: a missing token yields empty, not an exception.
- Worker: a pull request older than the gate yields `Waiting` **without** firing the toast, and
  announces once a fresher one appears.
