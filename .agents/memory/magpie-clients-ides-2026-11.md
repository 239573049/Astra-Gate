---
name: magpie-clients-ides-2026-11
description: Batch 5 of the magpie client adapters (IDEs, editors and AI extensions): Cursor CLI / Cursor Private Inference / Kiro Crew left out, T3 Code without its Codex instance, Obsidian keyless with enableCors, escaped dots and nested YAML rows in the editors
type: project
updated: 2026-11-16
source: session
evidence: ["src/Astra.Clients/Adapters/ConfigFileClientAdapter.cs", "src/Astra.Clients/Editing/YamlEditor.NestedRows.cs", "src/Astra.Clients/Adapters/ObsidianClientAdapter.cs", "packages/client-config/src/adapters/group-8.ts"]
---
Twelve clients were added from magpie's wiring (checked against magpie's code only, no live client): zoo-code, jetbrains-air, air-ide,
copilot-jetbrains, codebuddy, qoder, qoder-cn, t3code, pencil, openpencil, raycast, obsidian. TS adapters are `adapters/group-8.ts`.

- **Not adapters.** Cursor CLI: magpie only picks among Cursor's own models in `cli-config.json`; the CLI has no endpoint to point at the gateway
  (a Cursor subscription goes through magpie's own bridge). Cursor Private Inference: its endpoint is `CURSOR_LOCAL_AGENT_BASE_URL` /
  `_API_KEY` in the user's environment (launchctl / registry / environment.d), not a file. Kiro Crew: it has no provider; it runs on a harness
  agent (Codex, Claude Code, OpenCode, Pi, Goose) that is already on the gateway, which is another client's state an adapter cannot see.
- **Editors grew two features.** `JsoncEditor.SplitKeyPath` reads `\.` as a literal dot (VS Code's flat key `zoo-code.autoImportSettingsPath`), and
  `YamlEditor` addresses a row of a block sequence under a top-level key, `providers.[id=astra]` (line scan of the key at column 0 and its dash
  lines; the row is rendered as block YAML; removing the last row removes the key). Both are mirrored in the TS editors with tests.
- **`ConfigFileClientAdapter`** (C#) / `config-file-adapter.ts` is the shared base of this batch: `SetKey`, `TrackedRemovals` (a removal for every key the
  client recorded; restore then does the rest) and the "owned parts" pattern — entries a client or the user edits in normal use (Qoder's provider,
  OpenPencil's row, T3's instance, Raycast's entry, Copilot's `isRegistered`) compare only the fields Astra writes through `ConfigChange.StillOurs`.
- **Obsidian** (Copilot for Obsidian 4+, one `data.json` per vault from `obsidian.json`) is keyless like magpie's: Copilot keeps keys in Obsidian's
  keychain. It differs from magpie in `enableCors: true` (Copilot then sends through Obsidian's own request function; the gateway's CORS policy is the
  desktop app's only) and in `addedAt` / `configuredAt` = 0 (no clock in adapters); both unverified. Model ids are UUID v5 of `astra/<id>` under a
  namespace derived from the URL namespace; the user's default model is replaced the first time and a later pick in Copilot is left alone.
- **T3 Code** writes only the `claudeAgent` instance `astra` (Anthropic protocol); magpie's second, Codex instance (`launchArgs` + a catalog file)
  is not written. Reasoning models get T3's effort / thinking picks with low / medium / high — Astra does not know each model's own levels.
- **Qoder** reasoning models get a `thinking` capability with the same fixed levels (unverified). Air runs `opencode acp` with the path found on
  PATH or in `~/.opencode/bin`; the WSL variant of the Air plugin is not written.
- A fresh Raycast file whose only content was `providers: []` comes back as an empty file after a restore (semantically the same).
- Install: CodeBuddy / Qoder / Qoder CN are npm (`@tencent-ai/codebuddy-code`, `@qoder-ai/qodercli`, `@qodercn-ai/qoderclicn`, checked against the
  registry); the rest are Manual — Zoo Code is a VS Code extension, but `ClientInstallService.Inspect` special-cases only Copilot Chat.
- Icons are monograms except Qoder (library mark) and Copilot for JetBrains (Copilot mark + monitor badge).
