-- Plugins v2 (design/plugin-v2): a v2 plugin's own data, keyed by its manifest id (acme.word-guard) so it outlives an
-- update or a reinstall. A plugin is v2 when its folder has astra.plugin.json — that is read from disk, not stored here.
-- plugin_kv:      astra.storage — one JSON value per key, quota enforced by the host.
-- plugin_secrets: astra.secrets and "secret": true settings — values protected by ISecretProtector, never returned by the API.
-- plugin_grants:  the permissions the user confirmed at install / upgrade / a runtime request.
-- plugin_activity: the sensitive calls a plugin made (who, when, which call, aimed at what, outcome) — never the content.
-- plugin_successor_dismissals: "don't remind me" for a v1 plugin's v2 successor, by the v1 plugin's spec and the v2 version.
-- Plain DDL (NOT idempotent by design — 0001 is the only idempotent script).

CREATE TABLE plugin_kv (
    plugin_id      TEXT NOT NULL,
    key            TEXT NOT NULL,
    value_json     TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (plugin_id, key)
);

CREATE TABLE plugin_secrets (
    plugin_id       TEXT NOT NULL,
    name            TEXT NOT NULL,
    value_protected TEXT NOT NULL,
    updated_at_utc  TEXT NOT NULL,
    PRIMARY KEY (plugin_id, name)
);

CREATE TABLE plugin_grants (
    plugin_id        TEXT NOT NULL PRIMARY KEY,
    permissions_json TEXT NOT NULL,
    updated_at_utc   TEXT NOT NULL
);

CREATE TABLE plugin_activity (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    plugin_id TEXT NOT NULL,
    at_utc    TEXT NOT NULL,
    call      TEXT NOT NULL,
    target    TEXT,
    outcome   TEXT NOT NULL
);

CREATE INDEX ix_plugin_activity_plugin ON plugin_activity(plugin_id, id);

CREATE TABLE plugin_successor_dismissals (
    spec    TEXT NOT NULL PRIMARY KEY,
    version TEXT NOT NULL
);
