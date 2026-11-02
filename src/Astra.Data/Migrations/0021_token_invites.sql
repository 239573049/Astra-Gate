-- One-time join links of gateway tokens (the desktop's "connect to a unified gateway"): an admin makes a short-lived code for
-- an employee's token, the link carries it, and the employee's Astra exchanges it once for the token's key. Only the SHA-256 of
-- the code is stored. used_at / used_ip record who redeemed it (the admin sees them); a revoked, used or expired code is dead.
-- Plain DDL (NOT idempotent by design — 0001 is the only idempotent script).

CREATE TABLE token_invites (
    id          TEXT NOT NULL PRIMARY KEY,                                   -- ULID
    token_id    TEXT NOT NULL REFERENCES tokens(id) ON DELETE CASCADE,
    code_hash   TEXT NOT NULL UNIQUE,
    created_at  TEXT NOT NULL,
    expires_at  TEXT NOT NULL,
    used_at     TEXT,
    used_ip     TEXT,
    revoked_at  TEXT
);
CREATE INDEX idx_token_invites_token ON token_invites(token_id);
