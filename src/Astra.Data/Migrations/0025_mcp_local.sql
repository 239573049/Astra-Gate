-- MCP servers the user defines once (a local stdio command or a remote address) and has Astra write into clients' own MCP lists
-- (GitHub issue #34). Astra stores the definition and writes it into each chosen client; it never runs the command.
-- Plain DDL (NOT idempotent by design — 0001 is the only idempotent script).

CREATE TABLE mcp_local_servers (
    id          TEXT NOT NULL PRIMARY KEY,           -- ULID
    name        TEXT NOT NULL UNIQUE,                -- the entry name in the clients' files; lower-case, [a-z0-9_-]
    transport   TEXT NOT NULL DEFAULT 'stdio',       -- stdio | http
    command     TEXT NOT NULL DEFAULT '',            -- stdio only
    args_json   TEXT NOT NULL DEFAULT '[]',          -- JSON array of strings
    env_enc     TEXT,                                -- stdio: protected JSON object of environment variables (they often hold keys)
    url         TEXT NOT NULL DEFAULT '',            -- http only
    headers_enc TEXT,                                -- http: protected JSON object of request headers
    enabled     INTEGER NOT NULL DEFAULT 1,
    clients_json TEXT NOT NULL DEFAULT '[]',         -- the client kinds it should be written into (the desired state)
    created_at  TEXT NOT NULL,
    updated_at  TEXT NOT NULL
);
