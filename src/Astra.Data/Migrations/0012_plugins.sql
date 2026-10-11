-- Installed plugins (plan: .plan/plugins.md). magpie keeps this list as plugins.json; Astra keeps it here and
-- hands it to the plugin host in its init call, so plugins never read it as a file.
-- spec: how the plugin was added — an npm package (name, name@version), a git repository (github:o/r,
--       git+https://…) or an absolute path to a file or folder. Unique: the same spec is one plugin.
-- name: the package name (or file name for a path), what remove/off/options also find a plugin by.
-- sort_order: the user's order, which is the gateway middleware chain's order (plugins.json order in magpie).
-- options_json: handed to the plugin — a middleware's ctx.options, an OpenCode plugin's second argument.
-- source: market | npm | github | git | path | magpie-import.
-- plugin_provider_prefs is magpie's "prefer": the plugin picked for a provider id more than one plugin signs in to.
-- Plain DDL (NOT idempotent by design — 0001 is the only idempotent script).

CREATE TABLE plugins (
    spec              TEXT    NOT NULL PRIMARY KEY,
    name              TEXT    NOT NULL,
    enabled           INTEGER NOT NULL DEFAULT 1,
    sort_order        INTEGER NOT NULL DEFAULT 0,
    options_json      TEXT,
    source            TEXT,
    installed_version TEXT,
    added_at_utc      TEXT    NOT NULL,
    updated_at_utc    TEXT    NOT NULL
);

CREATE TABLE plugin_provider_prefs (
    provider_id TEXT NOT NULL PRIMARY KEY,
    spec        TEXT NOT NULL REFERENCES plugins(spec) ON DELETE CASCADE
);
