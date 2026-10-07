-- Astra full schema (plan §3, §5.4, §6.6, §6.7) as a single migration: new databases are created
-- directly at the current shape. Idempotent so it can be re-applied to an existing database.

CREATE TABLE IF NOT EXISTS schema_version (
    version    INTEGER NOT NULL PRIMARY KEY,
    applied_at TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS settings (
    key        TEXT NOT NULL PRIMARY KEY,
    value_json TEXT NOT NULL
);

-- System models. pricing_json is the official (vendor) default price; per-provider prices live in model_prices.
CREATE TABLE IF NOT EXISTS models (
    id                        TEXT    NOT NULL PRIMARY KEY,
    display_name              TEXT    NOT NULL,
    vendor                    TEXT    NOT NULL DEFAULT '',
    family                    TEXT,
    aliases_json              TEXT    NOT NULL DEFAULT '[]',
    context_window            INTEGER,
    max_output_tokens         INTEGER,
    capabilities_json         TEXT    NOT NULL DEFAULT '{}',
    pricing_json              TEXT,
    source                    TEXT    NOT NULL DEFAULT 'user',  -- seed | sync | user
    user_modified_fields_json TEXT    NOT NULL DEFAULT '[]',
    seed_version              INTEGER,
    enabled                   INTEGER NOT NULL DEFAULT 1,
    created_at                TEXT    NOT NULL,
    updated_at                TEXT    NOT NULL
);

-- System price of a model at one AI provider (price_key = provider type = template id, e.g. "siliconflow").
CREATE TABLE IF NOT EXISTS model_prices (
    id                INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    model_id          TEXT    NOT NULL REFERENCES models(id) ON DELETE CASCADE,
    price_key         TEXT    NOT NULL,
    upstream_model_id TEXT,
    pricing_json      TEXT    NOT NULL,
    source            TEXT    NOT NULL DEFAULT 'user',  -- seed | sync | user
    user_modified     INTEGER NOT NULL DEFAULT 0,
    updated_at        TEXT    NOT NULL,
    UNIQUE (model_id, price_key)
);

CREATE TABLE IF NOT EXISTS providers (
    id                                TEXT    NOT NULL PRIMARY KEY,
    template_id                       TEXT,
    price_key                         TEXT,                       -- defaults to template_id
    template_version                  INTEGER,
    template_snapshot_json            TEXT,
    name                              TEXT    NOT NULL,
    icon                              TEXT,
    category                          TEXT    NOT NULL DEFAULT 'custom',
    endpoints_json                    TEXT    NOT NULL DEFAULT '[]',
    preferred_upstream_protocols_json TEXT    NOT NULL DEFAULT '[]',
    auth_scheme                       TEXT    NOT NULL DEFAULT 'bearer',
    api_key_enc                       TEXT,
    extra_headers_json                TEXT    NOT NULL DEFAULT '{}',
    http_proxy                        TEXT,
    price_multiplier                  TEXT    NOT NULL DEFAULT '1',
    adapter_id                        TEXT,
    settings_json                     TEXT    NOT NULL DEFAULT '{}',
    enabled                           INTEGER NOT NULL DEFAULT 1,
    sort_order                        INTEGER NOT NULL DEFAULT 0,
    notes                             TEXT,
    website                           TEXT,
    created_at                        TEXT    NOT NULL,
    updated_at                        TEXT    NOT NULL
);

-- Subscription accounts (plan §5.4): OAuth logins behind a provider whose
-- auth_scheme = 'oauth-subscription'. Tokens are DataProtection-encrypted.
CREATE TABLE IF NOT EXISTS provider_accounts (
    id                  TEXT    NOT NULL PRIMARY KEY,
    provider_id         TEXT    NOT NULL REFERENCES providers(id) ON DELETE CASCADE,
    display_name        TEXT    NOT NULL DEFAULT '',
    account_email       TEXT,
    plan                TEXT,
    access_token_enc    TEXT,
    refresh_token_enc   TEXT,
    expires_at_utc      TEXT,
    status              TEXT    NOT NULL DEFAULT 'active',  -- active | expired | revoked
    last_refresh_at_utc TEXT,
    extra_json          TEXT    NOT NULL DEFAULT '{}',
    created_at          TEXT    NOT NULL,
    updated_at          TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS provider_models (
    id              INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    provider_id     TEXT    NOT NULL REFERENCES providers(id) ON DELETE CASCADE,
    model_id        TEXT    NOT NULL,          -- upstream model id
    system_model_id TEXT,                       -- auto-matched or manually linked system model
    overrides_json  TEXT    NOT NULL DEFAULT '{}',
    enabled         INTEGER NOT NULL DEFAULT 1,
    sort_order      INTEGER NOT NULL DEFAULT 0,
    UNIQUE (provider_id, model_id)
);

-- Rows are created lazily: by default no client is configured.
CREATE TABLE IF NOT EXISTS clients (
    kind             TEXT    NOT NULL PRIMARY KEY,
    enabled          INTEGER NOT NULL DEFAULT 0,
    local_key_enc    TEXT,
    local_key_hash   TEXT,
    local_key_prefix TEXT,
    selected_model   TEXT,
    extra_json       TEXT,
    applied_at       TEXT
);

-- First version only ever uses priority = 0; the list shape is reserved for failover.
CREATE TABLE IF NOT EXISTS client_bindings (
    client_kind TEXT    NOT NULL REFERENCES clients(kind) ON DELETE CASCADE,
    provider_id TEXT    NOT NULL REFERENCES providers(id) ON DELETE CASCADE,
    account_id  TEXT    REFERENCES provider_accounts(id) ON DELETE SET NULL,  -- pinned subscription account; null = the provider's default account
    priority    INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (client_kind, priority)
);

-- One row per key Astra wrote into a client config file (original value or absent marker).
CREATE TABLE IF NOT EXISTS client_config_state (
    id                  INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    client_kind         TEXT    NOT NULL,
    file_path           TEXT    NOT NULL,
    key_path            TEXT    NOT NULL,
    original_absent     INTEGER NOT NULL DEFAULT 0,
    original_value_json TEXT,
    applied_value_json  TEXT,
    applied_at          TEXT    NOT NULL,
    UNIQUE (client_kind, file_path, key_path)
);

-- response_model is the model ID declared by the upstream's raw response itself (e.g. a
-- snapshot-suffixed id); null when the response declares no ID. It is never a fallback of
-- requested_model / upstream_model.
-- cache_read_tokens / cache_write_tokens (5m + 1h) / reasoning_tokens split out
-- total_input_tokens and total_output_tokens for the request log and stats.
-- privacy_json is the per-request guard outcome (plan §6.7): hits, actions, dry-run.
-- Originals of redacted values are NEVER stored here.
CREATE TABLE IF NOT EXISTS requests (
    id                    TEXT    NOT NULL PRIMARY KEY,      -- ULID
    started_at_utc        TEXT    NOT NULL,
    client_kind           TEXT,
    provider_id           TEXT,
    provider_name         TEXT,
    inbound_protocol      TEXT    NOT NULL DEFAULT '',
    upstream_protocol     TEXT,
    passthrough           INTEGER NOT NULL DEFAULT 0,
    requested_model       TEXT,
    upstream_model        TEXT,
    response_model        TEXT,
    system_model_id       TEXT,
    stream                INTEGER NOT NULL DEFAULT 0,
    service_tier          TEXT,
    status                TEXT    NOT NULL DEFAULT 'success',
    http_status           INTEGER,
    error_type            TEXT,
    error_message         TEXT,
    upstream_request_id   TEXT,
    ttfb_ms               INTEGER,
    ttft_ms               INTEGER,
    total_ms              INTEGER,
    generation_ms         INTEGER,
    output_tps            REAL,
    total_input_tokens    INTEGER NOT NULL DEFAULT 0,
    total_output_tokens   INTEGER NOT NULL DEFAULT 0,
    cache_read_tokens     INTEGER NOT NULL DEFAULT 0,
    cache_write_tokens    INTEGER NOT NULL DEFAULT 0,
    reasoning_tokens      INTEGER NOT NULL DEFAULT 0,
    cost_nanousd          INTEGER NOT NULL DEFAULT 0,
    usage_source          TEXT    NOT NULL DEFAULT 'missing',  -- reported | missing
    usage_raw_json        TEXT,
    pricing_snapshot_json TEXT,
    pricing_source        TEXT,                                    -- system_default | provider_price | provider_override | none
    price_key             TEXT,
    billing_trace_json    TEXT,
    billing_description   TEXT,
    privacy_json          TEXT,
    body_ref              TEXT,
    user_agent            TEXT
);

CREATE TABLE IF NOT EXISTS request_usage_items (
    id               INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    request_id       TEXT    NOT NULL REFERENCES requests(id) ON DELETE CASCADE,
    token_type       TEXT    NOT NULL,
    tokens           INTEGER NOT NULL DEFAULT 0,
    is_per_call      INTEGER NOT NULL DEFAULT 0,
    unit_price       TEXT    NOT NULL DEFAULT '0',   -- USD per 1M tokens (per call), invariant decimal string
    base_unit_price  TEXT,
    tier_applied     TEXT    NOT NULL DEFAULT 'base',
    priced_as        TEXT,
    multipliers_json TEXT,
    cost_nanousd     INTEGER NOT NULL DEFAULT 0,
    note             TEXT
);

-- Failover attempts (plan §6.6): the gateway writes rows from v1.1 on.
CREATE TABLE IF NOT EXISTS request_attempts (
    id                INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    request_id        TEXT    NOT NULL REFERENCES requests(id) ON DELETE CASCADE,
    attempt           INTEGER NOT NULL,
    provider_id       TEXT,
    provider_name     TEXT,
    upstream_protocol TEXT,
    status            TEXT    NOT NULL,                 -- success | upstream_error | gateway_error
    http_status       INTEGER,
    error_type        TEXT,
    error_message     TEXT,
    ttfb_ms           INTEGER,
    total_ms          INTEGER,
    started_at_utc    TEXT    NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_requests_started_at           ON requests(started_at_utc);
CREATE INDEX IF NOT EXISTS idx_requests_provider_started     ON requests(provider_id, started_at_utc);
CREATE INDEX IF NOT EXISTS idx_requests_client_started       ON requests(client_kind, started_at_utc);
CREATE INDEX IF NOT EXISTS idx_requests_model_started        ON requests(system_model_id, started_at_utc);
CREATE INDEX IF NOT EXISTS idx_request_usage_items_request   ON request_usage_items(request_id);
CREATE INDEX IF NOT EXISTS idx_request_attempts_request      ON request_attempts(request_id);
CREATE INDEX IF NOT EXISTS idx_provider_models_provider      ON provider_models(provider_id, sort_order);
CREATE INDEX IF NOT EXISTS idx_client_bindings_provider      ON client_bindings(provider_id);
CREATE INDEX IF NOT EXISTS idx_model_prices_key              ON model_prices(price_key);
CREATE INDEX IF NOT EXISTS idx_provider_accounts_provider    ON provider_accounts(provider_id);
CREATE INDEX IF NOT EXISTS idx_provider_accounts_expires     ON provider_accounts(status, expires_at_utc);
