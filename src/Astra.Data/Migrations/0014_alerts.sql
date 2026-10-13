-- Alerts and webhooks (plan: .plan/alerts-webhooks-token-limits.md §3).
-- webhooks: delivery targets only. secret_enc / headers_enc are ISecretProtector-encrypted (headers may carry
-- credentials); the API reports whether a secret is set and the header names, never the values.
-- alert_rules: event + condition + subjects (NULL = every subject of the event's type) → alert_rule_webhooks.
-- alert_states: one row per rule × subject, so a restart does not notify again what already fired. Subjects are not
-- foreign keys (providers, accounts and tokens live in different tables); rows of deleted subjects are dropped lazily.
-- webhook_deliveries: every call (pending until sent, retried with backoff), pruned after 7 days / 1000 per webhook.
-- Plain DDL (NOT idempotent by design — 0001 is the only idempotent script).

CREATE TABLE webhooks (
    id          TEXT    NOT NULL PRIMARY KEY,   -- ULID
    name        TEXT    NOT NULL,
    url         TEXT    NOT NULL,
    secret_enc  TEXT,
    headers_enc TEXT,
    enabled     INTEGER NOT NULL DEFAULT 1,
    timeout_sec INTEGER NOT NULL DEFAULT 10,
    use_proxy   INTEGER NOT NULL DEFAULT 1,
    created_at  TEXT    NOT NULL,
    updated_at  TEXT    NOT NULL
);

CREATE TABLE alert_rules (
    id               TEXT    NOT NULL PRIMARY KEY,   -- ULID
    name             TEXT    NOT NULL,
    enabled          INTEGER NOT NULL DEFAULT 1,
    event            TEXT    NOT NULL,
    subjects_json    TEXT,
    condition_json   TEXT,
    notify_resolved  INTEGER NOT NULL DEFAULT 0,
    repeat_minutes   INTEGER,
    cooldown_minutes INTEGER NOT NULL DEFAULT 60,
    created_at       TEXT    NOT NULL,
    updated_at       TEXT    NOT NULL
);

CREATE TABLE alert_rule_webhooks (
    rule_id    TEXT NOT NULL REFERENCES alert_rules(id) ON DELETE CASCADE,
    webhook_id TEXT NOT NULL REFERENCES webhooks(id)    ON DELETE CASCADE,
    PRIMARY KEY (rule_id, webhook_id)
);

CREATE TABLE alert_states (
    rule_id          TEXT NOT NULL REFERENCES alert_rules(id) ON DELETE CASCADE,
    subject_id       TEXT NOT NULL,
    subject_name     TEXT NOT NULL,
    state            TEXT NOT NULL,   -- 'firing' | 'ok'
    value_json       TEXT,
    changed_at       TEXT NOT NULL,
    last_notified_at TEXT,
    PRIMARY KEY (rule_id, subject_id)
);

CREATE TABLE webhook_deliveries (
    id               TEXT    NOT NULL PRIMARY KEY,   -- ULID, also X-Astra-Delivery
    webhook_id       TEXT    NOT NULL REFERENCES webhooks(id) ON DELETE CASCADE,
    rule_id          TEXT,                           -- NULL = test delivery
    event            TEXT    NOT NULL,
    status           TEXT    NOT NULL,               -- 'pending' | 'success' | 'failed'
    attempts         INTEGER NOT NULL DEFAULT 0,
    next_attempt_at  TEXT,
    http_status      INTEGER,
    error            TEXT,
    response_snippet TEXT,
    payload_json     TEXT    NOT NULL,
    created_at       TEXT    NOT NULL,
    finished_at      TEXT
);
CREATE INDEX idx_webhook_deliveries_due  ON webhook_deliveries(status, next_attempt_at);
CREATE INDEX idx_webhook_deliveries_hook ON webhook_deliveries(webhook_id, created_at);
