-- Resource library, part 2: instructions, sub-agents, slash commands, hooks and environment variables (library_items), what Astra
-- wrote for them (library_writes, the ownership ledger: Astra only ever removes what is recorded here and still exactly as written),
-- the project directories things can be placed into (library_projects), the skill router's clients, and the project of a Skill
-- placement / an own MCP server. Plain DDL (NOT idempotent by design).

CREATE TABLE library_projects (
    path      TEXT NOT NULL PRIMARY KEY,           -- absolute directory
    name      TEXT NOT NULL,
    added_at  TEXT NOT NULL
);

CREATE TABLE library_items (
    id           TEXT NOT NULL PRIMARY KEY,
    kind         TEXT NOT NULL,                    -- instruction | agent | command | hook | env
    name         TEXT NOT NULL,
    description  TEXT NOT NULL DEFAULT '',
    body         TEXT NOT NULL DEFAULT '',         -- the text / prompt / command / value
    extra_json   TEXT NOT NULL DEFAULT '{}',       -- hook event / matcher / timeout, agent model ...
    targets_json TEXT NOT NULL DEFAULT '[]',       -- [{ "client": "claude-code", "project": "" }]
    enabled      INTEGER NOT NULL DEFAULT 1,
    created_at   TEXT NOT NULL,
    updated_at   TEXT NOT NULL,
    UNIQUE (kind, name)
);

CREATE TABLE library_writes (
    id            TEXT NOT NULL PRIMARY KEY,
    item_id       TEXT REFERENCES library_items(id) ON DELETE CASCADE,   -- null for a skill router link
    client        TEXT NOT NULL,
    project       TEXT NOT NULL DEFAULT '',        -- '' = user-wide
    path          TEXT NOT NULL,                   -- the file written (or the link)
    mode          TEXT NOT NULL,                   -- file | block | hook | config | router
    locator       TEXT NOT NULL DEFAULT '',        -- block id / hook event / config key path
    content_hash  TEXT NOT NULL DEFAULT '',        -- hash of what was written (the block body, the file)
    entry         TEXT,                            -- hooks: the exact JSON entry added to the event's array
    written_at    TEXT NOT NULL,
    UNIQUE (item_id, client, project, path, locator)
);
CREATE INDEX idx_library_writes_item ON library_writes(item_id);

CREATE TABLE library_router (
    client      TEXT NOT NULL PRIMARY KEY,
    enabled_at  TEXT NOT NULL
);

ALTER TABLE library_placements ADD COLUMN project TEXT NOT NULL DEFAULT '';   -- '' = user-wide
ALTER TABLE mcp_local_servers ADD COLUMN projects_json TEXT NOT NULL DEFAULT '[]';
