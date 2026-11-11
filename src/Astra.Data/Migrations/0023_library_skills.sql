-- Resource library (Skills). library_skills describes what lives in ~/.astra/library/skills/<name>; library_placements is both the
-- desired state and the ownership ledger: Astra only ever removes a placement recorded here and still its own (a link into the library,
-- or a copy whose hash has not changed). Plain DDL (NOT idempotent by design — 0001 is the only idempotent script).

CREATE TABLE library_skills (
    name         TEXT NOT NULL PRIMARY KEY,         -- the directory name, which SKILL.md's frontmatter name must equal
    description  TEXT NOT NULL DEFAULT '',
    source_kind  TEXT NOT NULL,                     -- migrated | local | git
    source_json  TEXT,                              -- original path, or { url, ref, commit }
    hash         TEXT NOT NULL,                     -- content hash when installed or migrated
    created_at   TEXT NOT NULL,
    updated_at   TEXT NOT NULL
);

CREATE TABLE library_placements (
    path        TEXT NOT NULL PRIMARY KEY,          -- absolute path of the link or copy
    skill_name  TEXT NOT NULL REFERENCES library_skills(name) ON DELETE CASCADE,
    target      TEXT NOT NULL,                      -- 'shared' (~/.agents/skills) or a client kind
    how         TEXT NOT NULL,                      -- symlink | junction | copy
    copy_hash   TEXT,                               -- copies only: the hash written
    origin      TEXT NOT NULL,                      -- migrated | applied
    placed_at   TEXT NOT NULL
);
CREATE INDEX idx_library_placements_skill ON library_placements(skill_name);
