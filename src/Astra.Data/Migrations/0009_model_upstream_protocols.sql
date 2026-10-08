-- Per-model upstream protocol constraints. Some upstreams expose a model over only one of their APIs:
-- GitHub Copilot serves its Claude models over Messages only, so a Responses request for them must be
-- translated instead of passed through (the Responses endpoint answers "model_not_supported").
-- upstream_protocols_json is a JSON array of ApiProtocol ids learned from the upstream model list
-- (Copilot's /models "supported_endpoints"); NULL/empty means "no constraint".
-- Plain DDL (NOT idempotent by design — 0001 is the only idempotent script).

ALTER TABLE provider_models ADD COLUMN upstream_protocols_json TEXT;
