-- MCP gateway (GitHub issue #29, .plan/mcp-gateway.md): upstream MCP servers aggregated behind one /mcp endpoint, the OAuth
-- tokens Astra holds for some of them, a log of tool calls, and which servers a gateway token may use.
-- Plain DDL (NOT idempotent by design — 0001 is the only idempotent script).

CREATE TABLE mcp_servers (
    id              TEXT NOT NULL PRIMARY KEY,                       -- ULID
    slug            TEXT NOT NULL UNIQUE,                            -- prefixes the server's tool names; lower-case, no underscore
    name            TEXT NOT NULL,
    transport       TEXT NOT NULL,                                   -- http | sse
    url             TEXT NOT NULL,
    auth_type       TEXT NOT NULL DEFAULT 'none',                    -- none | header | oauth
    headers_enc     TEXT,                                            -- protected JSON object of static request headers
    enabled         INTEGER NOT NULL DEFAULT 1,
    timeout_seconds INTEGER NOT NULL DEFAULT 60,
    status          TEXT NOT NULL DEFAULT 'unknown',                 -- cache of the last contact: ok | needs_auth | auth_error | unreachable | version_unsupported
    status_message  TEXT,
    protocol_version TEXT,                                           -- what the last connection settled on
    checked_at      TEXT,
    created_at      TEXT NOT NULL,
    updated_at      TEXT NOT NULL
);

-- The OAuth state of an auth_type = 'oauth' server: the SDK's token container (access / refresh token, client registration, the
-- authorization server that issued them), protected as one value. A server has one identity, shared by every token bound to it.
CREATE TABLE mcp_oauth (
    server_id   TEXT NOT NULL PRIMARY KEY REFERENCES mcp_servers(id) ON DELETE CASCADE,
    tokens_enc  TEXT NOT NULL,
    issuer      TEXT,
    scope       TEXT,
    updated_at  TEXT NOT NULL
);

-- One row per tool call: who, which tool, how long, how it ended. Never the arguments or the result. server_id is not a foreign
-- key: the log outlives a deleted server.
CREATE TABLE mcp_calls (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    at              TEXT NOT NULL,
    token_id        TEXT NOT NULL,
    server_id       TEXT NOT NULL,
    server_slug     TEXT NOT NULL,
    tool            TEXT NOT NULL,
    duration_ms     INTEGER NOT NULL,
    ok              INTEGER NOT NULL,
    error_category  TEXT,
    front_era       TEXT
);
CREATE INDEX idx_mcp_calls_at ON mcp_calls(at);
CREATE INDEX idx_mcp_calls_token ON mcp_calls(token_id, at);

-- The MCP servers a token may use (JSON array of mcp_servers.id); NULL = none. Ids are not foreign keys: a deleted server is skipped.
ALTER TABLE tokens ADD COLUMN mcp_json TEXT;
