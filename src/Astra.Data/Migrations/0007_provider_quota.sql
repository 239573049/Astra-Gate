-- Balance / quota snapshot of an API-key provider (the provider's "usage query"). Same idea as the
-- subscription snapshot in provider_accounts.extra_json.quota, but it belongs to the provider itself.
-- quota_json holds the last successful snapshot plus the latest failure (error, errorCode, failures),
-- so list views can show both without calling the upstream. Credentials, request headers and raw
-- upstream responses are never stored here. quota_checked_at_utc is the last attempt (success or
-- failure) and drives the background refresh interval; it is not part of updated_at on purpose.
-- Plain DDL (NOT idempotent by design — 0001 is the only idempotent script).

ALTER TABLE providers ADD COLUMN quota_json TEXT;
ALTER TABLE providers ADD COLUMN quota_checked_at_utc TEXT;
