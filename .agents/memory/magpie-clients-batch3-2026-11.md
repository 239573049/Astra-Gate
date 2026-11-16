---
name: magpie-clients-batch3-2026-11
description: Batch 3 of the magpie client adapters (file-backed CLI and desktop agents): FileClientAdapter's "give back what was recorded" disable, keyless Empryo / Ante / Craft Agents, NewMax left out
type: project
updated: 2026-11-16
source: session
evidence: ["src/Astra.Clients/Adapters/FileClientAdapter.cs", "packages/client-config/src/adapters/file-adapter.ts", "tests/Astra.Clients.Tests/MagpieBatch3Tests.cs"]
---
Fifteen clients were added from magpie's wiring (checked against magpie's code only, no live client): reasonix, morph, jcode, vibe,
empryo, nanobot, miniq, paimon, cline (CLI only), grok-app, atomcode, snow, ante, craft-agents, tuanjie-cowork.

- **`FileClientAdapter` is the family base** (C# and TS port `adapters/file-adapter.ts`, TS group `group-9.ts`). An adapter only writes;
  `PlanDisable` is every key the state store recorded (non-table keys first, then tables Astra created), so a key Astra *removed* at enable
  time (nanobot's `modelPreset`) is also given back. Subclasses override `IsTableKeyPath` for TOML tables / array selectors.
- **NewMax is not an adapter.** Its settings are the `app` row of a SQLite table the running app writes back whole; `ClientConfigApplier`
  edits text files, and a row editor would need a SQLite dependency in C# *and* the TS port (no `node:sqlite` on Node 18). Not worth it for
  one app that must be quit during the edit.
- **Keyless on purpose:** Empryo (no `envVar`), Ante (no `auth` block) and Craft Agents (`authType: none`) cannot hold a literal key, so
  the status card warns that "require gateway key" must be off (they then count as the default token). magpie names Craft by an
  `/agent/craft-agents` URL path, which Astra's gateway does not have.
- **Not written (no clock / no data):** Cline's `updatedAt` and VS Code extension state, Craft's `createdAt`, effort levels (Reasonix
  Planner and effort, Grok App, AtomCode), miniQ's live-daemon RPC push (restart needed instead). Snow copies only the `snowcfg` key of
  the active profile.
- Craft Agents refuses (EditorException) until Craft has made a `config.json` with `workspaces`; Tuanjie Cowork gets one `customModels`
  element per model (`astra-<id>`), not magpie's single `magpie` model.
- Homepages for the Manual installs (Reasonix, Mister Morph, Empryo, jcode, ...) are best guesses; install methods other than Cline /
  AtomCode / Snow (npm) are Manual.
