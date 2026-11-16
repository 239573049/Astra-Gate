-- Named routing groups (magpie parity): virtual models `group/<slug>` whose members are `provider/model` targets or other groups,
-- ordered per request by a mode (smart / order / rotate / usage / pace / weight). members_json is a JSON array of
-- { provider, model, group, weight }; the gateway reads these rows per request through a RowCache.
-- Plain DDL (NOT idempotent by design — 0001 is the only idempotent script).

CREATE TABLE routing_groups (
    id            TEXT NOT NULL PRIMARY KEY,           -- ULID
    slug          TEXT NOT NULL UNIQUE,                -- the id after `group/`
    name          TEXT NOT NULL,
    mode          TEXT NOT NULL DEFAULT 'smart',
    stays         TEXT NOT NULL DEFAULT 'auto',
    members_json  TEXT NOT NULL DEFAULT '[]',
    enabled       INTEGER NOT NULL DEFAULT 1,
    listed        INTEGER NOT NULL DEFAULT 1,
    created_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL
);
