---
name: magpie-clients-batch1-2026-11
description: Batch 1 of the magpie client adapters (OpenCode/Pi families, simple CLIs): keyless fx and Command Code, Claude Science and Devin left out, array-of-tables editing, shell-function clients
type: project
updated: 2026-11-16
source: session
evidence: ["src/Astra.Clients/Adapters/FxClientAdapter.cs", "src/Astra.Clients/Editing/TomlArrayEntries.cs", "src/Astra.Clients/Adapters/PiClientAdapter.cs:PiFlavour"]
---
Fourteen clients were added from magpie's wiring (checked against magpie's code only, no live client): openchamber, deveco-code,
stepcode, aside, omo, prime-agent, mu, mothx, penguin-harness, antigravity-cli, dim, command-code, fx, goose.

- **fx and Command Code cannot hold a key** (fx `auth` is `none` or a bearer read from an env var; Command Code refuses a written
  `apiKey`). Astra writes them keyless and the status card warns that "require gateway key" must be off for local requests; they
  then count as the default token, not the client.
- **Claude Science and Devin are not adapters.** Claude Science has no config: magpie restarts its daemon with
  `ANTHROPIC_BASE_URL` set. Devin has no endpoint to point at (magpie only picks among Devin's own models).
- **Dim and Antigravity CLI use a shell function** in the rc file (the Muse Code pattern, `ShellRc`); agy also gets
  `~/.gemini/antigravity-cli/astra-settings.json` handed over with `--settings`, so its `settings.json` stays the user's.
- **PenguinHarness `[[models]]`**: `TomlArrayEntries` owns the array-of-tables rows with `provider = "astra"` (a Table-kind change
  with the selector `models[provider=astra]`; line scan, validated by re-parsing). Its `default_model` is written in whichever form
  the file has (inline table or `[default_model]`).
- **OpenChamber** shares OpenCode's config file but writes its own provider id `astra-openchamber` (a shared `astra` block would be
  overwritten with the other client's key) and selects its model in its own profile (`preferences.json` + the `settings.json` copy).
  It leaves `updatedAt` alone (no clock in the adapters), unverified against a real OpenChamber.
- mu and StepCode are detected by their directory alone: `mu` and `step` are other programs' commands.
- Homepages for Aside, StepCode (stepfun.com) and Antigravity are best guesses.
