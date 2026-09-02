# Slack Review Notifications — Design Spec

**Date:** 2026-09-02
**Status:** Approved — implementation pending
**Parent designs:**
- [2026-05-25-jira-time-tracker-daemon-design.md](2026-05-25-jira-time-tracker-daemon-design.md) (§ JiraPollService, `JiraStatusTransition`)
- [2026-05-25-tmtt-ui-design.md](2026-05-25-tmtt-ui-design.md) (Settings window structure)

---

## 1. Goal

When a tracked ticket moves into the configured review status, post a message to
a Slack channel chosen **per Jira project**, written by a **per-project editable
template**, sent **as the user** (not a bot).

The trigger already exists: `JiraPollService` publishes `JiraStatusTransition`
when a polled ticket's status becomes `config.transition_to_status_name`
(default `Review`) having previously been something else. This feature adds a
subscriber, a Slack client, and the settings to configure it.

## 2. Non-Goals

- **Watching Bitbucket pull requests.** Investigated and dropped: Bitbucket sits
  behind a separate authorization server (`bitbucket.org/site/oauth2`) from Jira
  (`auth.atlassian.com`), and the Atlassian developer console exposes no
  Bitbucket API to add to the existing OAuth app. It would require a second
  credential (an Atlassian API token, app passwords having been removed
  2026-07-28) plus a poller and a PR watermark. Deferred — see §10.
- **Causing the transition.** The user moves the ticket to Review by hand; this
  feature only reports it.
- **Posting as a bot.** Messages must carry the user's own name and avatar.
- **Notifying for untracked tickets.** `JiraPollService` polls only cycles with
  `submitted_at IS NULL`, so only tickets the user actually tracked time on can
  notify. This is intentional.
- Block Kit, threading, attachments, reactions, or editing posted messages.
- Notifying on any status transition other than the configured review status.

## 3. Confirmed Design Decisions

| Decision | Choice |
| -------- | ------ |
| Trigger | Existing `JiraStatusTransition` event; no new polling |
| De-duplication | **None required.** `ticket_time.last_seen_status` already makes the event edge-triggered — it fires only on `previous != Review && current == Review` |
| Slack auth | **User token** (`xoxp-`), pasted once into Settings, stored DPAPI-encrypted via the existing `ITokenProtector` |
| Why user token, not bot | Message must appear as the user; a user token also inherits the user's membership of private channels, so no `/invite` step is needed |
| Slack scopes | `chat:write`, `channels:read`, `groups:read` |
| `as_user` parameter | **Never sent.** It is legacy and returns `as_user_not_supported` on modern apps; authorship comes from the token type |
| Why not OAuth "click Allow" | Slack mandates an HTTPS `redirect_uri` and offers no PKCE/public-client flow, so a hosted redirect page would be required. A Slack app must be created either way; a one-time paste was chosen over standing up a redirect host |
| Channel mapping key | **Jira project key** (`SN`, `TM`), derived from the ticket key. The event carries no repo path, so project key is the only correct join |
| Template placeholders | `{NAME}` — uppercase, brace-delimited. Unknown placeholders render **literally** so typos are visible rather than silently blanking text |
| Missing configuration | Silent no-op at debug level. An unconfigured project is not an error |
| Issue URL | Requires the Jira site URL, which `OAuthCoordinator` currently fetches then discards. Persist it in a **new one-row `jira_site` table**, resolved lazily — see §6.6 |
| Why a new table, not a column | `DatabaseInitializer` executes `Schema.sql`, which is entirely `CREATE TABLE IF NOT EXISTS`. There is **no `ALTER TABLE` path**, so a new column on `oauth_state` would apply only to fresh databases and silently never appear on existing installs. New tables migrate correctly; new columns do not |
| Summary + minutes | Carried on the event. `JiraPollService` already holds both at publish time, so this costs zero extra API calls |

## 4. Architecture

```text
  JiraPollService  (existing, unchanged trigger logic)
        │
        │  publishes when previous != "Review" && current == "Review"
        ▼
  JiraStatusTransition(TicketKey, Summary, From, To, MinutesActive, AtUtc)
        │                          └── new fields ──┘
        │  IEventBus (existing)
        ▼
  ReviewSlackNotifier : BackgroundService          <- new
        │
        ├─ JiraProjectKey.From("SN-296") ─────────> "SN"
        │
        ├─ SlackChannelRepository.Find("SN")
        │     └─ null ──> debug log, stop (not an error)
        │
        ├─ MessageTemplateRenderer.Render(template, vars)
        │
        └─ SlackApiClient.PostMessageAsync(channelId, text)
                 │
                 └─ chat.postMessage, Bearer xoxp-...

  Settings -> Slack page                           <- new
        ├─ token field + [Test] ─> auth.test  ("Connected as @lefteris")
        ├─ channel dropdown     ─> conversations.list (public + private)
        ├─ per-project template editor + variables panel + live preview
        └─ [Test] per row ─> posts a rendered sample to that channel
```

### 4.1 Data model

Three new tables, appended to `Data/Schema.sql`. All are `CREATE TABLE IF NOT
EXISTS`, so they apply cleanly to both fresh and existing databases. **No
existing table is altered.**

```sql
CREATE TABLE IF NOT EXISTS slack_credential (
    id           INTEGER PRIMARY KEY CHECK(id = 1),
    token_dpapi  BLOB NOT NULL
);

CREATE TABLE IF NOT EXISTS slack_channel (
    project_key      TEXT PRIMARY KEY,
    channel_id       TEXT NOT NULL,
    channel_name     TEXT NOT NULL,
    message_template TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS jira_site (
    id        INTEGER PRIMARY KEY CHECK(id = 1),
    cloud_id  TEXT NOT NULL,
    site_url  TEXT NOT NULL
);
```

`channel_name` is stored alongside `channel_id` purely so Settings can render
`#sheeponline` without a Slack round-trip; `channel_id` is what is posted to.

## 5. Component Responsibilities

| Component | Layer | Responsibility |
| --------- | ----- | -------------- |
| `JiraProjectKey` | `Logic/` | Pure. `"SN-296"` -> `"SN"`. Splits on the **last** `-` so keys like `ABC_D-12` work |
| `MessageTemplateRenderer` | `Logic/` | Pure. `(template, IReadOnlyDictionary<string,string>)` -> rendered text. No I/O |
| `SlackVariables` | `Logic/` | Builds the variable dictionary from an event. Single source of truth for both rendering **and** the UI's variables panel, so the two cannot drift |
| `SlackApiClient` | `Slack/` | `AuthTestAsync`, `ListConversationsAsync` (cursor-paginated), `PostMessageAsync`. Mirrors `JiraApiClient`'s shape |
| `SlackCredentialRepository` | `Data/` | One-row token store, DPAPI-protected |
| `SlackChannelRepository` | `Data/` | CRUD over `slack_channel` |
| `JiraSiteRepository` | `Data/` | One-row `jira_site` cache, keyed by `cloud_id` (§6.6) |
| `ChannelSuggester` | `Logic/` | Pure. Jira project name + channel list -> best-guess channel, or none |
| `ReviewSlackNotifier` | `Services/` | `BackgroundService`; subscribes to `IEventBus`, orchestrates the pipeline above |
| `SlackPage` | `UI/SettingsPages/` | Token, connection test, per-project grid, variables panel, live preview |

## 6. Algorithm Details

### 6.1 Project-key derivation

```
"SN-296"    -> "SN"
"ABC_D-12"  -> "ABC_D"
"nokey"     -> null   (no '-', or non-numeric suffix)
```

Take the substring before the last `-`; the suffix must be all digits.

### 6.2 Template rendering

Scan for `\{[A-Z_]+\}`. Replace a match when the key exists in the dictionary;
otherwise leave the literal text untouched. Single pass — replacement output is
never re-scanned, so a summary containing `{TICKET}` cannot inject.

**`SlackVariables` omits keys whose value is unavailable** rather than mapping
them to an empty string. This is what makes §6.6's fallback work: an unresolved
site URL leaves `{URL}` *absent from the dictionary*, so it renders literally
and the reader can see something is misconfigured. Mapping it to `""` would
instead emit a silent blank line, which looks like a formatting bug rather than
a missing setting.

### 6.3 Available variables

| Variable | Source | Example |
| -------- | ------ | ------- |
| `{TICKET}` | event | `SN-296` |
| `{PROJECT}` | derived | `SN` |
| `{SUMMARY}` | issue `summary` field | `Fix Tolgee warning` |
| `{URL}` | `jira_site.site_url` + `/browse/` + key (§6.6) | `https://tcubeee.atlassian.net/browse/SN-296` |
| `{FROM}` | event | `In Progress` |
| `{TO}` | event | `Review` |
| `{MINUTES}` | `ticket_time.minutes_active` | `137` |
| `{HOURS}` | formatted from minutes | `2h 17m` |
| `{DATE}` | event timestamp, local | `2026-09-02 14:31` |

Default template:

```
Done ✅ {TICKET} — {SUMMARY}
{FROM} -> {TO} · {HOURS}
{URL}
```

### 6.4 Minimising setup

Setup is a hard requirement, not a nicety. The full user journey is:

1. Create a Slack app from the manifest shipped at `docs/slack-app-manifest.yml`
   (scopes pre-declared — nothing to pick), click **Install to Workspace**.
2. Copy the User OAuth Token, paste into Settings -> Slack, click **Test**.
3. Confirm the auto-suggested channel per project.

To make step 3 near-zero the app **bootstraps the grid itself**:

- Project rows are derived from `SELECT DISTINCT` project key over
  `ticket_time` — the user never adds a row by hand.
- Each row's channel is **pre-suggested** by fuzzy-matching the Jira project
  *name* against the channel list (`SheepOnline New` -> `#sheeponline`).
  Project names come from a new `JiraApiClient.ListProjectsAsync`
  (`GET /rest/api/3/project/search`, already covered by the granted
  `read:jira-work` scope), called once when the Settings page opens.
  Matching is on alphanumerics only, lowercased, ignoring spaces and hyphens,
  so `SheepOnline New` matches `sheeponline`. A suggestion is **never saved
  without the user confirming it** — it only pre-selects the dropdown.
- Templates are pre-filled with the default.

**The per-row [Send test message] button posts to the real channel**, where
teammates will see it. It must therefore render with obviously-sample values and
a visible test marker (`🧪 Test from TmTimeTracker — …`), never a plausible
`Done ✅ SN-296` that someone might act on.

### 6.5 Notify pipeline

```
on JiraStatusTransition e:
    if e.ToStatus != config.TransitionToStatusName:  return   # defensive
    project = JiraProjectKey.From(e.TicketKey)   ; if null: return
    row     = channels.Find(project)             ; if null: debug, return
    token   = credentials.Get()                  ; if null: debug, return
    text    = renderer.Render(row.Template, SlackVariables.From(e, siteUrl))
    await slack.PostMessageAsync(row.ChannelId, text)
```

### 6.6 Site-URL resolution

`OAuthCoordinator.CompleteFirstRunAsync` already calls `/accessible-resources`
and holds `resources[0].Url`, but discards it — and the refresh path
(`GetAccessTokenAsync` -> `SaveTokens`) never calls that endpoint at all. So
existing installs have a `cloud_id` and no site URL, and a re-auth alone would
not fix them.

Resolution is therefore **lazy and self-healing**:

```
GetSiteUrlAsync():
    row = jiraSite.Get()
    if row != null && row.CloudId == oauthState.CloudId:  return row.SiteUrl
    resources = await oauth.ListAccessibleAsync()     # already public
    match     = resources.FirstOrDefault(r => r.Id == oauthState.CloudId)
    if match == null: return null                     # {URL} renders literally
    jiraSite.Save(match.Id, match.SiteUrl)
    return match.SiteUrl
```

Keying the cache on `cloud_id` means `SwitchCloudId` invalidates it for free.
When the URL cannot be resolved the message still sends — `{URL}` simply stays
literal, consistent with the unknown-placeholder rule in §6.2.

## 7. Error Handling

| Failure | Behaviour |
| ------- | --------- |
| No token configured | Debug log, no-op. Not an error |
| No channel for project | Debug log, no-op. Not an error |
| `invalid_auth` / `token_revoked` | Warn, surface a "Slack disconnected — reconnect in Settings" state on the Settings page. Never retried in a loop |
| `channel_not_found` / `not_in_channel` | Warn naming the project and channel, so the user can fix the mapping |
| `ratelimited` (HTTP 429) | Honour `Retry-After`, retry **once**, then give up and log |
| Network failure | Warn and drop the notification. A missed Slack message must never stall the event loop or crash the host |
| `ok: false` generally | Every response is checked for the `ok` envelope before use; the `error` string is logged verbatim |

Notification failures are strictly non-fatal: this is a reporting side-channel,
and no failure in it may affect time tracking or worklog submission.

## 8. Testing

Unit tests only; nothing touches the network.

| Test | Covers |
| ---- | ------ |
| `JiraProjectKeyTests` | Normal keys, underscored keys, no-dash input, non-numeric suffix, null/empty |
| `MessageTemplateRendererTests` | Every variable substitutes; unknown placeholder preserved literally; no re-scan of substituted text; empty template; template with no placeholders |
| `SlackVariablesTests` | Dictionary keys match exactly the set advertised in the UI panel |
| `SlackApiClientTests` | `ok:false` surfaces the error; 429 honours `Retry-After`; `conversations.list` follows `next_cursor`; `as_user` is never sent |
| `SlackChannelRepositoryTests` | Upsert, find-by-project, delete, missing row -> null |
| `ChannelSuggesterTests` | `SheepOnline New` -> `#sheeponline`; `Training Manager` -> `#training-manager`; no plausible match -> none; ambiguous match -> none rather than a wrong guess |
| `JiraSiteRepositoryTests` | Save/get round-trip; a changed `cloud_id` invalidates the cached URL |
| `ReviewSlackNotifierTests` | Posts on a matching event; silent on unconfigured project; silent with no token; ignores non-review transitions; unresolvable site URL still sends with `{URL}` literal; a throwing Slack client does not propagate |

## 9. Acceptance Criteria

1. Moving a tracked SN ticket from In Progress to Review posts exactly **one**
   message to `#sheeponline`, authored by the user, within one poll interval.
2. The same transition on a TM ticket posts to `#training-manager`.
3. Editing a project's template changes the next message; the live preview in
   Settings matches what is posted.
4. A project with no channel configured produces no message and no error.
5. Revoking the Slack token surfaces a visible disconnected state in Settings
   and does not disturb time tracking or worklog submission.
6. `dotnet test` passes; no test performs network I/O.

## 10. Open Questions / Deferred

- **`{PR_URL}` variable — PINNED, blocked on a workspace admin.** Agreed
  approach: have the Bitbucket workspace admin connect Bitbucket to Jira (Jira →
  Settings → Apps → Bitbucket). That link is a product-to-product connection, not
  a credential this app holds, so afterwards TmTimeTracker can read PR links with
  the OAuth token it already has. Blocked because the user is not the workspace
  admin.

  Evidence gathered while scoping it, so it need not be re-derived:
  - Bitbucket **Cloud** does not expose `refs/pull-requests/*` over git —
    `git ls-remote` on `thecubeee/sheeponline-new` returns only `refs/heads`
    (210 refs). That namespace is Bitbucket **Server** only.
  - `development[pullrequests].all > 0` and `development[commits].all > 0` return
    zero across all 10 projects, and `SN-296` has no remote issue links —
    consistent with the integration being absent rather than merely unreadable.
  - The reader endpoint would be Jira's internal
    `/rest/dev-status/1.0/issue/detail?issueId={numericId}&applicationType=bitbucket&dataType=pullrequest`.
    It is undocumented, so **whether it authorises under `read:jira-work` via
    OAuth 2.0 (3LO) must be verified against a live instance** before relying on
    it. Verification is impossible until the integration is connected.

  Implementation shape when unblocked: an `IPullRequestSource` seam with a
  `JiraDevStatusPullRequestSource`, one extra entry in `SlackVariables.Catalog`,
  and nothing else. If the endpoint does not authorise, `{PR_URL}` is simply
  omitted from the dictionary and renders literally (§6.2) — so the failure mode
  is already designed for and no existing behaviour changes.

- **Bitbucket PR as the *trigger*** (§2), rather than just the link. Would remove
  the manual transition, at the cost of an Atlassian API token, a poller, and a
  `tracked_repo.last_seen_pr_id` watermark. Revisit only if the manual step chafes.
- **Per-project enable/disable toggle** — currently "no channel" doubles as
  "off". Adequate for now.
- **Settings shows no project cards when Slack is unreachable at page load.**
  `RefreshConnectionAsync` builds the cards only after a successful `auth.test`,
  so a transient outage makes the page look empty and could read as "my
  configuration was lost". Rendering saved mappings read-only in that case would
  be better, but it must not wire the save handlers — with an empty channel list
  a saved mapping would resolve to index 0 and the first interaction would delete
  it. Deliberately deferred rather than half-fixed.
- ~~**UI not yet smoke-tested.**~~ **Done 2026-09-02.** The first real run found
  exactly the predicted class of bug: `_connectionStatus` and `_projectsEmpty`
  had `AutoSize` but no `Dock`, so they sat at (0, 0) and painted over the
  "Slack connection" heading — hiding the only text that explained the token
  box. `FlatButton` also ate the ampersand in "Save & test" (it draws through
  `TextRenderer.DrawText` without `TextFormatFlags.NoPrefix`). Both fixed.
  End-to-end verified: token pasted, connection established, message posted.

  **Slack install gotcha, recorded so it is not re-diagnosed.** The app-creation
  wizard's "Create and Install" opens the consent page in a `window.open` popup.
  That page redirects `slack.com/oauth/v2/authorize` -> `<team>.slack.com/oauth`,
  a cross-subdomain hop carrying the Slack session cookie, which popups abort
  under strict third-party-cookie or popup-blocking settings. The symptom is a
  window that flashes and closes with only "Installation was not completed."
  Fix: open the same authorize URL as a normal top-level tab from
  **Install App -> Install to \<workspace\>**; it succeeds first time. Nothing to
  do with scopes, the manifest, or admin approval.
- **Notifying on other transitions** (e.g. -> Done). The pipeline is generic;
  only the guard in §6.5 restricts it. Widening is a config change, not a
  redesign.
