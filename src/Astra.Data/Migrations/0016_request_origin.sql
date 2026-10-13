-- Where a gateway request came from (remote access: .plan/remote-access.md): remote = 1 when it reached Astra through
-- Cloudflare (a tunnel), as the security middleware decided it (Astra.Gateway RequestOrigin); client_ip is the caller's
-- address when it isn't this machine (Cloudflare's Cf-Connecting-IP for tunnel requests), NULL for local callers and
-- for rows logged before this was tracked. Both follow the request-log retention like every other column.
-- Plain DDL (NOT idempotent by design — 0001 is the only idempotent script).

ALTER TABLE requests ADD COLUMN remote INTEGER NOT NULL DEFAULT 0;
ALTER TABLE requests ADD COLUMN client_ip TEXT;
