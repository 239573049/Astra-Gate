-- Account kinds, import source and per-account proxy (plan §5.4).
-- kind: 'oauth' = a login with refreshable grant (every account before this migration);
--       'api_key' = a static key the subscription's coding endpoint accepts (Kimi / Z.AI / BigModel Coding Plan):
--       never refreshed, never expires; an upstream 401 marks it revoked.
-- source: how the account got here — login | local | token | batch; NULL for accounts created before this migration.
-- http_proxy: proxy for everything this account sends (requests, refresh, quota); NULL = the provider's proxy,
--             else the system proxy.
-- Plain DDL (NOT idempotent by design — 0001 is the only idempotent script).

ALTER TABLE provider_accounts ADD COLUMN kind TEXT NOT NULL DEFAULT 'oauth';
ALTER TABLE provider_accounts ADD COLUMN source TEXT;
ALTER TABLE provider_accounts ADD COLUMN http_proxy TEXT;
