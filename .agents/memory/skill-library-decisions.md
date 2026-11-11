---
name: skill-library-decisions
description: Skills library (issue #34) — data lives in ~/.astra/library/skills, the nav group is "Resources" (MCP + Skills), links not copies, a table not adapter methods
type: project
updated: 2026-11-11
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
