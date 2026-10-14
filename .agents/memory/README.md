# How repository memory works

This directory records context that reading the code does not reveal. Rules belong in the root AGENTS.md;
descriptions of the code belong in `.agents/codemap/`. Memory holds the rest: why things are the way they
are, and what happened.

## Types
- `project` — decisions and their reasons, rejected approaches, incidents, in-flight work (absolute dates).
- `reference` — an external resource and what it is for.
- `feedback` — a correction or a confirmed non-obvious approach that applies to everyone working here.
- `user` — personal preferences. Never here: they go in `.next-cowork/memory/`, which is not committed.

## Entry format
One fact per file, named after its `name`. Frontmatter: `name`, `description`, `type`, `updated`,
`source` (`init` or `session`), `evidence`. Body: the fact, then **Why:** and **How to apply:**.
Link related entries with `[[name]]`. Add one line per entry to MEMORY.md under its type.

## Maintenance
- Before adding an entry, look for one that covers the same thing and update it instead.
- Convert relative dates to absolute dates. Cite evidence: commit, `path:Symbol`, document or URL.
- Delete an entry as soon as it turns out to be wrong, and remove its index line.
- An entry describes the time it was written. Verify paths, symbols and commands before relying on it.
- When a `feedback` entry has been confirmed again and applies to everyone, propose turning it into an
  AGENTS.md rule; do not edit AGENTS.md rules on your own.
- Memory changes show up in the diff like any other change. Do not commit them separately unless asked.
