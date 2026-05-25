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
