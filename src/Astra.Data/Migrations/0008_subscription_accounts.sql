-- Subscription account switching (plan §5.4): per-account enable switch, the provider's current account,
-- failover order and rate-limit cooldown; plus which account served each request.
-- is_current: at most one 1 per provider (written in one statement by ProviderAccountStore.SetCurrentAsync);
-- none set = the first usable account by sort_order. cooldown_until_utc is only written by automatic failover.
-- Plain DDL (NOT idempotent by design — 0001 is the only idempotent script).

ALTER TABLE provider_accounts ADD COLUMN enabled INTEGER NOT NULL DEFAULT 1;
ALTER TABLE provider_accounts ADD COLUMN is_current INTEGER NOT NULL DEFAULT 0;
ALTER TABLE provider_accounts ADD COLUMN sort_order INTEGER NOT NULL DEFAULT 0;
ALTER TABLE provider_accounts ADD COLUMN cooldown_until_utc TEXT;
ALTER TABLE provider_accounts ADD COLUMN last_error TEXT;

-- Existing accounts keep their creation order as the failover order.
UPDATE provider_accounts SET sort_order = (
    SELECT COUNT(*) FROM provider_accounts p
    WHERE p.provider_id = provider_accounts.provider_id
      AND (p.created_at < provider_accounts.created_at
           OR (p.created_at = provider_accounts.created_at AND p.id < provider_accounts.id)));

ALTER TABLE requests ADD COLUMN account_id TEXT;
