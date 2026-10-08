-- Tokens (令牌): gateway credentials shared by clients and usable directly. A client config holds
-- "<token>.<client kind>" so the gateway still knows which client called; a bare token is a direct
-- call routed to the token's own default provider (provider_id / account_id, both optional).
-- key_enc is the DataProtection-encrypted plaintext; NULL only for the default token this script
-- inserts — the server generates its key on startup (SQL cannot generate or encrypt a secret).
-- Plain DDL (NOT idempotent by design — 0001 is the only idempotent script).

CREATE TABLE tokens (
    id           TEXT    NOT NULL PRIMARY KEY,   -- ULID; the default token is always 'default'
    name         TEXT    NOT NULL,
    is_default   INTEGER NOT NULL DEFAULT 0,
    enabled      INTEGER NOT NULL DEFAULT 1,
    key_enc      TEXT,
    key_hash     TEXT,
    key_prefix   TEXT,
    provider_id  TEXT    REFERENCES providers(id) ON DELETE SET NULL,
    account_id   TEXT    REFERENCES provider_accounts(id) ON DELETE SET NULL,
    created_at   TEXT    NOT NULL,
    updated_at   TEXT    NOT NULL,
    last_used_at TEXT
);
CREATE INDEX idx_tokens_prefix ON tokens(key_prefix);

-- Lifetime usage per token, added to in the same transaction that writes the request rows, so
-- request-log retention never shrinks it. tps_* only count successful requests with a measured
-- generation time (weighted output speed = tps_output_tokens / tps_generation_ms * 1000).
CREATE TABLE token_usage_totals (
    token_id           TEXT    NOT NULL PRIMARY KEY REFERENCES tokens(id) ON DELETE CASCADE,
    requests           INTEGER NOT NULL DEFAULT 0,
    success_requests   INTEGER NOT NULL DEFAULT 0,
    cost_nanousd       INTEGER NOT NULL DEFAULT 0,
    input_tokens       INTEGER NOT NULL DEFAULT 0,
    output_tokens      INTEGER NOT NULL DEFAULT 0,
    cache_read_tokens  INTEGER NOT NULL DEFAULT 0,
    cache_write_tokens INTEGER NOT NULL DEFAULT 0,
    reasoning_tokens   INTEGER NOT NULL DEFAULT 0,
    tps_output_tokens  INTEGER NOT NULL DEFAULT 0,
    tps_generation_ms  INTEGER NOT NULL DEFAULT 0
);

-- clients.token_id: null = the default token. No foreign key: deleting a token moves its clients first (TokenService).
ALTER TABLE clients ADD COLUMN token_id TEXT;
-- No foreign key on requests: the log keeps the id (and the name snapshot) after a token is deleted.
ALTER TABLE requests ADD COLUMN token_id   TEXT;
ALTER TABLE requests ADD COLUMN token_name TEXT;
CREATE INDEX idx_requests_token_started ON requests(token_id, started_at_utc);

INSERT INTO tokens(id, name, is_default, enabled, created_at, updated_at)
VALUES ('default', 'Default', 1, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

-- Every client moves to the default token; the old per-client keys stop working right away.
UPDATE clients SET token_id = 'default', local_key_enc = NULL, local_key_hash = NULL, local_key_prefix = NULL;

-- History is attributed to the default token, and its lifetime totals start from that history.
UPDATE requests SET token_id = 'default';
INSERT INTO token_usage_totals(token_id, requests, success_requests, cost_nanousd, input_tokens, output_tokens,
                               cache_read_tokens, cache_write_tokens, reasoning_tokens, tps_output_tokens, tps_generation_ms)
SELECT 'default',
       COUNT(*),
       COALESCE(SUM(CASE WHEN status = 'success' THEN 1 ELSE 0 END), 0),
       COALESCE(SUM(cost_nanousd), 0),
       COALESCE(SUM(total_input_tokens), 0),
       COALESCE(SUM(total_output_tokens), 0),
       COALESCE(SUM(cache_read_tokens), 0),
       COALESCE(SUM(cache_write_tokens), 0),
       COALESCE(SUM(reasoning_tokens), 0),
       COALESCE(SUM(CASE WHEN status = 'success' AND generation_ms > 0 THEN total_output_tokens ELSE 0 END), 0),
       COALESCE(SUM(CASE WHEN status = 'success' AND generation_ms > 0 THEN generation_ms ELSE 0 END), 0)
FROM requests;

-- Enabled clients still hold their old key: the server rewrites them once on the next start.
INSERT INTO settings(key, value_json)
SELECT 'token_migration', '{"pending":true}'
WHERE EXISTS (SELECT 1 FROM clients WHERE enabled = 1);
