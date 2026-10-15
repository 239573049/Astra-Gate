-- Provider pools of gateway tokens (GitHub issue #2): pool_json is a TokenPool (strategy, sticky sessions, failover,
-- members with weight and tier) that a bare-token direct call is spread over. NULL = the token's single
-- provider_id / account_id, exactly as before. A pool always has at least two members (one member is stored as
-- provider_id / account_id), and provider_id / account_id keep its first member so an older binary, which ignores
-- this column, still routes direct calls. Member provider ids are not foreign keys: a deleted provider is skipped
-- when the pool is read and dropped the next time the token is saved.
-- Plain DDL (NOT idempotent by design — 0001 is the only idempotent script).

ALTER TABLE tokens ADD COLUMN pool_json TEXT;
