-- Tracing (.plan/observability.md §2): every gateway request writes its span tree to trace_spans in the same transaction
-- as its request row, and loses it with that row (ON DELETE CASCADE — request-log retention removes both). The request
-- row carries what Astra infers about the agent from the traffic itself (§3): the conversation (session_key), the call
-- chain (chain_key), the turn it belongs to, the call it continues and how that link was found. trace_id is shared by
-- the calls of one turn; parent_span_id is the agent's own span when it sent a traceparent.
-- request_attempts (0001) stays unused: attempts are upstream.attempt spans.
-- Every new column is nullable or defaulted: rows logged before this migration have no trace. Nothing is back-filled.
-- Plain DDL (NOT idempotent by design — 0001 is the only idempotent script).

ALTER TABLE requests ADD COLUMN trace_id          TEXT;
ALTER TABLE requests ADD COLUMN parent_span_id    TEXT;
ALTER TABLE requests ADD COLUMN session_key       TEXT;
ALTER TABLE requests ADD COLUMN chain_key         TEXT;
ALTER TABLE requests ADD COLUMN turn_id           TEXT;
ALTER TABLE requests ADD COLUMN parent_request_id TEXT;
ALTER TABLE requests ADD COLUMN step              INTEGER;
ALTER TABLE requests ADD COLUMN call_kind         TEXT;
ALTER TABLE requests ADD COLUMN link_source       TEXT;
ALTER TABLE requests ADD COLUMN finish_reason     TEXT;
ALTER TABLE requests ADD COLUMN tool_calls        INTEGER NOT NULL DEFAULT 0;

CREATE INDEX idx_requests_session_started ON requests(session_key, started_at_utc);
CREATE INDEX idx_requests_turn            ON requests(turn_id);

-- One span per row; start_us / duration_us are microseconds from the moment the request arrived (monotonic clock).
-- attributes_json: flat object of astra.* / gen_ai.* keys; events_json: [{name, offset_us, attributes}].
CREATE TABLE trace_spans (
    request_id      TEXT    NOT NULL REFERENCES requests(id) ON DELETE CASCADE,
    span_id         TEXT    NOT NULL,
    parent_span_id  TEXT,
    name            TEXT    NOT NULL,
    kind            TEXT    NOT NULL DEFAULT 'internal',  -- server | internal | client
    start_us        INTEGER NOT NULL,
    duration_us     INTEGER NOT NULL,
    status          TEXT    NOT NULL DEFAULT 'unset',     -- ok | error | unset
    status_message  TEXT,
    attributes_json TEXT,
    events_json     TEXT,
    PRIMARY KEY (request_id, span_id)
);
