CREATE TABLE IF NOT EXISTS ticket_time (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    ticket_key        TEXT NOT NULL,
    cycle_started     TEXT NOT NULL,
    minutes_active    INTEGER NOT NULL DEFAULT 0,
    last_seen_status  TEXT,
    last_polled       TEXT,
    submitted_at      TEXT,
    worklog_id        TEXT,
    submitted_minutes INTEGER,
    UNIQUE(ticket_key, cycle_started)
);

CREATE INDEX IF NOT EXISTS idx_ticket_unsubmitted
    ON ticket_time(ticket_key) WHERE submitted_at IS NULL;

CREATE TABLE IF NOT EXISTS remember_entry (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    ticket_key          TEXT NOT NULL,
    timestamp_local     TEXT NOT NULL,
    entry_date          TEXT NOT NULL,
    body                TEXT NOT NULL,
    source_file         TEXT NOT NULL,
    consumed_in_worklog INTEGER NULL,
    UNIQUE(source_file, entry_date, timestamp_local, ticket_key)
);

CREATE TABLE IF NOT EXISTS minute_sample (
    sampled_at      TEXT PRIMARY KEY,
    ticket_key      TEXT,
    is_idle         INTEGER NOT NULL,
    git_branch      TEXT,
    claude_running  INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS oauth_state (
    id                  INTEGER PRIMARY KEY CHECK(id = 1),
    cloud_id            TEXT NOT NULL,
    access_token_dpapi  BLOB NOT NULL,
    refresh_token_dpapi BLOB NOT NULL,
    access_expires_at   TEXT NOT NULL,
    scope               TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS oauth_app_config (
    id                  INTEGER PRIMARY KEY CHECK(id = 1),
    client_id_dpapi     BLOB NOT NULL,
    client_secret_dpapi BLOB NOT NULL,
    redirect_uri        TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS config (
    id                          INTEGER PRIMARY KEY CHECK(id = 1),
    idle_threshold_seconds      INTEGER NOT NULL DEFAULT 600,
    jira_poll_interval_seconds  INTEGER NOT NULL DEFAULT 90,
    repo_path                   TEXT NOT NULL,
    remember_path               TEXT NOT NULL,
    in_progress_status_name     TEXT NOT NULL DEFAULT 'In Progress',
    transition_to_status_name   TEXT NOT NULL DEFAULT 'Review'
);

CREATE TABLE IF NOT EXISTS tracked_repo (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    path       TEXT NOT NULL UNIQUE COLLATE NOCASE,
    sort_order INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS slack_credential (
    id          INTEGER PRIMARY KEY CHECK(id = 1),
    token_dpapi BLOB NOT NULL
);

CREATE TABLE IF NOT EXISTS slack_channel (
    project_key      TEXT PRIMARY KEY,
    channel_id       TEXT NOT NULL,
    channel_name     TEXT NOT NULL,
    message_template TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS jira_site (
    id       INTEGER PRIMARY KEY CHECK(id = 1),
    cloud_id TEXT NOT NULL,
    site_url TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS pr_announcement (
    ticket_key   TEXT PRIMARY KEY,
    issue_id     TEXT,
    summary      TEXT,
    from_status  TEXT NOT NULL,
    to_status    TEXT NOT NULL,
    minutes      INTEGER NOT NULL DEFAULT 0,
    occurred_at  TEXT NOT NULL,
    queued_at    TEXT NOT NULL,
    attempts     INTEGER NOT NULL DEFAULT 0,
    announced_at TEXT,
    pr_url       TEXT,
    warned_at    TEXT
);

-- Basic-auth credential for Jira's internal dev-status API, which does not accept the OAuth
-- token: email plus an Atlassian API token, encrypted with the same DPAPI protector.
CREATE TABLE IF NOT EXISTS jira_api_token (
    id          INTEGER PRIMARY KEY CHECK(id = 1),
    email       TEXT NOT NULL,
    token_dpapi BLOB NOT NULL
);

-- The Bitbucket REST API needs its own token: the Jira one is scopeless and Bitbucket rejects it.
CREATE TABLE IF NOT EXISTS bitbucket_api_token (
    id          INTEGER PRIMARY KEY CHECK(id = 1),
    email       TEXT NOT NULL,
    token_dpapi BLOB NOT NULL
);

-- Which Jira project's board covers a tracked repo. A separate table rather than a column on
-- tracked_repo: DatabaseInitializer only runs CREATE TABLE IF NOT EXISTS, and SQLite has no
-- ADD COLUMN IF NOT EXISTS, so a new table stays idempotent with no migration machinery.
-- auto_matched records whether the mapping was guessed or set by hand, so a user correction
-- is never silently re-guessed.
CREATE TABLE IF NOT EXISTS repo_project (
    repo_path    TEXT PRIMARY KEY COLLATE NOCASE,
    project_key  TEXT NOT NULL,
    auto_matched INTEGER NOT NULL DEFAULT 1
);

-- Optional Claude Code OAuth token from `claude setup-token`, encrypted with the same DPAPI
-- protector as every other credential. Absent is the normal case: the estimator then inherits
-- the machine's own Claude Code login.
CREATE TABLE IF NOT EXISTS claude_auth (
    id          INTEGER PRIMARY KEY CHECK(id = 1),
    token_dpapi BLOB NOT NULL
);

-- One row per ticket ever considered for estimation. Durable rather than in-memory because the
-- 5-minute sweep would otherwise re-estimate everything after a restart, and because the
-- attempt cap and the notify-once flag both have to survive one.
CREATE TABLE IF NOT EXISTS ticket_estimate (
    ticket_key     TEXT PRIMARY KEY,
    repo_path      TEXT NOT NULL,
    status         TEXT NOT NULL,
    attempts       INTEGER NOT NULL DEFAULT 0,
    impl_minutes   INTEGER,
    test_minutes   INTEGER,
    review_minutes INTEGER,
    confidence     TEXT,
    rationale      TEXT,
    raw_output     TEXT,
    error          TEXT,
    failed_gate    TEXT,
    estimated_at   TEXT,
    warned_at      TEXT
);
