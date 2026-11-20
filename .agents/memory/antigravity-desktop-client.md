---
name: antigravity-desktop-client
description: Antigravity desktop (kind `antigravity`) shares the CLI's engine; why it exports env vars in the rc and edits its settings.json, and what was never verified
type: project
updated: 2026-11-20
source: session
evidence: ["src/Astra.Clients/Adapters/AntigravityClientAdapter.cs", "packages/client-config/src/adapters/antigravity.ts", "tests/fixtures/client-config/antigravity", "tests/Astra.Clients.Tests/AntigravityClientTests.cs"]
---
The desktop agent app (`Antigravity.app`, bundle `com.google.antigravity`, checked on 2.33.0) is an Electron shell around the same
`language_server` as the CLI (`agy`), so it takes the CLI's inputs: `modelProvider: gemini` + `customModelsConfig` in `settings.json`, and
`GOOGLE_GEMINI_BASE_URL` / `GEMINI_API_KEY` from the environment only. Its app data dir is `~/.gemini/antigravity/` (the CLI's is
`antigravity-cli`; the engine's `--app_data_dir` is the app name lower-cased, `dist/paths.js`). Kind `antigravity`, separate from `antigravity-cli`.

- **Why an exporting rc block, not the CLI's `agy()` function:** the app starts the engine with the environment of the user's interactive
  login shell (`dist/languageServer.js`: `loginShellEnv`, read at launch and again on every engine restart), so a Dock launch sees what
  `~/.zshrc` exports and nothing else. A function would never reach it. Side effect: every shell sees `GEMINI_API_KEY` /
  `GOOGLE_GEMINI_BASE_URL` (Gemini CLI too).
- **Why the user's `settings.json` is edited (through the applier, originals recorded) instead of a `--settings` file:** the app has no such
  switch. Disable restores `model` and removes `modelProvider` / the `astra/*` custom models; the user's own custom models stay.
- **Not verified:** that a signed-in desktop actually honours `modelProvider: gemini` from `~/.gemini/antigravity/settings.json`. A headless
  `language_server` run only proved the data dir (it asks for a Google login before using any model). Test with a real account before
  promising it. `AGY_LLM_GATEWAY_URL` / `_API_KEY` / `_WIRE_PROTOCOL` / `_MODELS` exist in the engine and might avoid both side effects, but their
  semantics are unknown (names only).
- Windows reports not found (no shell profile). Skills / instructions / MCP places for the desktop are not added.

**How to apply:** if the real-app test shows the desktop reads a different file or needs different variables, change both adapters (C# + TS)
and re-run the golden cases (`ASTRA_UPDATE_FIXTURES=1 ASTRA_FIXTURE_FILTER=antigravity/`). Related: [[magpie-clients-batch1-2026-11]].
