-- Token limits and client restrictions (plan: .plan/alerts-webhooks-token-limits.md §1).
-- limits_json: TokenLimits (budgets per metric × period, rpm); NULL = unlimited.
-- allowed_clients_json: client kinds allowed as the key suffix; NULL = any client, [] = direct calls only.
-- token_usage_daily: usage per token and local day of the usage time zone (AppSettings.UsageTimeZone, the process's
-- local zone by default), added to in the same transaction as the request rows — like token_usage_totals — so
-- request-log retention never shrinks it. It feeds the day / week / month budgets and the key's self-service view.
-- Requests refused by a token limit are logged but counted in neither table.
-- Plain DDL (NOT idempotent by design — 0001 is the only idempotent script).

ALTER TABLE tokens ADD COLUMN limits_json TEXT;
ALTER TABLE tokens ADD COLUMN allowed_clients_json TEXT;

CREATE TABLE token_usage_daily (
    token_id         TEXT    NOT NULL REFERENCES tokens(id) ON DELETE CASCADE,
    day              TEXT    NOT NULL,   -- 'YYYY-MM-DD'
    requests         INTEGER NOT NULL DEFAULT 0,
    success_requests INTEGER NOT NULL DEFAULT 0,
    cost_nanousd     INTEGER NOT NULL DEFAULT 0,
    input_tokens     INTEGER NOT NULL DEFAULT 0,
    output_tokens    INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (token_id, day)
);

-- Start from whatever the request log still holds (process local time = the default usage time zone).
INSERT INTO token_usage_daily(token_id, day, requests, success_requests, cost_nanousd, input_tokens, output_tokens)
SELECT token_id,
       date(started_at_utc, 'localtime'),
       COUNT(*),
       COALESCE(SUM(CASE WHEN status = 'success' THEN 1 ELSE 0 END), 0),
       COALESCE(SUM(cost_nanousd), 0),
       COALESCE(SUM(total_input_tokens), 0),
       COALESCE(SUM(total_output_tokens), 0)
FROM requests
WHERE token_id IN (SELECT id FROM tokens)
GROUP BY token_id, date(started_at_utc, 'localtime');
