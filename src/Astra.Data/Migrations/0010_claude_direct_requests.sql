-- Direct-connection telemetry of the Claude clients (Claude Desktop / Claude Code): usage the client
-- itself collected and reported, NOT gateway traffic — it is deliberately kept out of `requests`, which
-- only logs what went through this gateway. One row per reported request, id is the client's own event
-- id so a replayed upload deduplicates on the primary key (INSERT ... ON CONFLICT(id) DO NOTHING).
-- profile_id groups the rows of one client profile/session owner; account_uuid is the upstream account
-- the client used (often absent); token counts are reported by the client, estimated_cost_nanousd is the
-- client's own estimate (NULL when it reported none).
-- Plain DDL (NOT idempotent by design — 0001 is the only idempotent script).

CREATE TABLE claude_direct_requests (
    id                     TEXT    NOT NULL PRIMARY KEY,
    profile_id             TEXT    NOT NULL,
    session_id             TEXT    NOT NULL,
    account_uuid           TEXT,
    model                  TEXT    NOT NULL,
    occurred_at_utc        TEXT    NOT NULL,
    status                 TEXT    NOT NULL,
    input_tokens           INTEGER NOT NULL,
    output_tokens          INTEGER NOT NULL,
    cache_read_tokens      INTEGER NOT NULL,
    cache_creation_tokens  INTEGER NOT NULL,
    duration_ms            INTEGER,
    estimated_cost_nanousd INTEGER
);

CREATE INDEX idx_claude_direct_requests_profile_started ON claude_direct_requests(profile_id, occurred_at_utc);
