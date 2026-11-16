---
name: skill-library-decisions
description: Skills library (issue #34) — data lives in ~/.astra/library/skills, the nav group is "Resources" (MCP + Skills), links not copies, a table not adapter methods
type: project
updated: 2026-11-16
source: session
evidence: ["https://github.com/239573049/Astra-Gate/issues/34", ".plan/resources-library.md", "src/Astra.Clients/Library/SkillPlaces.cs"]
---

Issue #34 (unified Skills + MCP management) was built on 2026-11-11 as the Skill library, following `.plan/resources-library.md`.

- The issue author's suggested `~/.astra-gate/Skills` was **not** used: Skills live in `~/.astra/library/skills` (`AstraPaths.LibrarySkillsDir`) so `ASTRA_HOME` and the three path mirrors keep working. User agreed.
- Nav: one "资源 / Resources" group holding MCP and Skills; Hooks / instructions / sub-agents get a menu item only when they exist.
- Clients get a link into the library (not a copy); a client reading `~/.agents/skills` gets it there, so switching it off asks first.
- The Avalonia page mirrors the web page (chips are clickable Arc badges, so no dashed "locked" border).
- Where each client reads Skills is the `SkillPlaces` table, not an adapter method — adapters and golden cases untouched.

**Why:** a link keeps one copy of truth and makes "off" = delete the link, which was the user's own design.
**How to apply:** see linked update 26 in AGENTS.md and `.agents/codemap/flows/skill-library.md`. The Windows junction/copy paths have never run on Windows. [[mcp-gateway-design]]

## The rest of the library (2026-11-16, magpie parity #7, linked update 37)

- Instructions / sub-agents / commands / hooks / env, project scope, Skill updates, market and router were built from magpie's `internal/library` (MIT). One Library page with tabs in the web and Avalonia (`nav.library`), not five nav items.
- **Router is additive, unlike magpie's**: it lists the Skills a client has *no link for* instead of replacing the links of ticked Skills (magpie's design needs a `Skill.Router` flag and rewrites every link). Switching a Skill's link off is how the user routes it.
- **Not built, by decision**: magpie's MCP router (runs stdio servers locally / proxies — linked update 25 forbids it, needs the user first).
- Skills market goes to skills.sh `GET /api/search` by default (a third-party service Astra does not run); the address is the `library_market` setting. A website-side registry is wanted to make this first-party.
- Unverified (taken from magpie's tables, not run against the clients): Skills dirs of Pi, oh-my-pi, MiniMax Code, Hermes Agent, DeepSeek Harness, Command Code, Crush, ZCode, Antigravity CLI (`~/.gemini/config`); instruction files of the same plus Goose (`.goosehints`), Droid, Grok Build, Copilot CLI; MCP shapes of Droid, Crush, Copilot CLI (`type local|http` + `tools`); project command folders (`.claude/commands`, `.opencode/commands`, `.gemini/commands`, `.qwen/commands`, `.factory/commands` — magpie writes commands user-wide only); Qwen Code's project `.qwen/settings.json`; Windows paths; nothing here has run on Windows.
- Trap: `MigrationRunner` applies only migrations above the highest recorded version, so a DB that already has 0033 never gets 0027-0032 added later (matters only for branch-built dev DBs); the migration tests' "undo" scripts must drop 0033's tables first.

