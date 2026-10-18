-- Lifetime usage per provider credential. An empty account_id represents the provider's own API key;
-- named accounts retain their id after removal, just like the request log. Log retention never shrinks totals.
CREATE TABLE provider_account_usage_totals (
    provider_id  TEXT    NOT NULL REFERENCES providers(id) ON DELETE CASCADE,
    account_id   TEXT    NOT NULL,
    requests     INTEGER NOT NULL DEFAULT 0,
    input_tokens INTEGER NOT NULL DEFAULT 0,
    output_tokens INTEGER NOT NULL DEFAULT 0,
    cost_nanousd INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (provider_id, account_id)
);

-- Recover the history that is still available on upgrade, keeping the original model pricing snapshots.
INSERT INTO provider_account_usage_totals(provider_id, account_id, requests, input_tokens, output_tokens, cost_nanousd)
SELECT r.provider_id, COALESCE(r.account_id, ''), COUNT(*),
       COALESCE(SUM(r.total_input_tokens), 0), COALESCE(SUM(r.total_output_tokens), 0), COALESCE(SUM(r.cost_nanousd), 0)
FROM requests r JOIN providers p ON p.id = r.provider_id
WHERE COALESCE(r.error_type, '') <> 'token_limit'
GROUP BY r.provider_id, COALESCE(r.account_id, '');
