---
module: cli
sources: ["cli/**"]
commit: 6324e30
updated: 2026-10-14
---
# cli (npm `astragate`, bin `astra`)

The terminal front-end for the local gateway: it resolves and launches the `astra-server` binary,
starts/stops/reuses it, learns its real address from `runtime.json`, drives the admin HTTP API for
clients/providers/tokens/plugins, manages autostart, installs/uninstalls the desktop app, and
self-updates. It ships as one tsup bundle (`dist/index.js`) and owns `~/.astra/install.json` plus the
CLI-side view of the `~/.astra` layout.
**Not responsible for:** anything the server does (subcommand bodies, `/api/*` handlers,
`runtime.json`/`config.json` writers, client config rewrite) — that is `astra-server` /
`astra-clients`; the update algorithm itself is `update-core`; the desktop app's own resolver is
`desktop-app` (`desktop/src/shared/resolveServerBinary.ts`); packaging/version sync is
`packaging-release`.

## Entry points
| Symbol | File | Role |
|---|---|---|
| `program` (commander root) | `cli/src/index.ts:55` | Declares the whole command surface; `astra` with no subcommand behaves like `astra start` (`:63`). |
| top-level error handler | `cli/src/index.ts:273` | `ApiError` → `error: … (HTTP n)`, `AstraError` → message + hint, else stack behind `ASTRA_DEBUG`; always sets `process.exitCode = 1`. |
| `clientKindArgument` | `cli/src/index.ts:9` | `<kind>` arg parser: built-in kind or `plugin-<agent id>`. |
| `runStart` / `runStop` / `runRestart` / `runStatus` / `runLogs` / `runOpen` | `cli/src/commands/manage.ts:17`,`:34`,`:43`,`:52`,`:87`,`:105` | Service lifecycle commands. |
| `runAutostart` | `cli/src/commands/autostart.ts:35` | Dispatch `enable/disable/status` per platform. |
| `runInstall` / `runUninstallClient` / `runUpdate` | `cli/src/commands/desktop.ts:30`,`:52`,`:74` | Desktop-app install/removal and the CLI+server+desktop update path. |
| `runUninstall` | `cli/src/commands/uninstall.ts:29` | Full uninstall: restore configs → autostart off → desktop removal → stop → optional `--purge`. |
| `runClaude` | `cli/src/commands/claude.ts:140` | Runs the official `claude` CLI with a subscription token obtained from the server. |
| `runRestoreAll` | `cli/src/commands/restore-all.ts:8` | Spawns the binary's offline `restore-all` subcommand. |
| `runSetPassword` | `cli/src/commands/config.ts:9` | Prompts twice, pipes the password into `astra-server set-password` stdin. |

## Key types and functions
| Symbol | File | Role |
|---|---|---|
| `resolveServerBinary` | `cli/src/lib/binary.ts:60` | The resolution order: `ASTRA_SERVER_BIN` > `install.json` `serverPath` > repo dev build > `@aidotnet/server-*` package; returns `{command, args, serverPath, source}`. |
| `findPackagedServerBinary` | `cli/src/lib/binary.ts:107` | Step 4 alone: reads `bin['astra-server']` from `@aidotnet/server-<platform>-<arch>/package.json`. |
| `platformInfo` | `cli/src/lib/platform.ts:22` | darwin/linux/win32 × x64/arm64 → `rid` (`osx-arm64`), `serverPackage`, `desktopPackage`, `binaryName`; throws for anything else. |
| `startServer` | `cli/src/lib/server-lifecycle.ts:135` | Reuse-or-spawn: persist port/host, probe, write `install.json` (path + probed version), spawn `serve --port --host --started-by cli`, detached with logs to `logs/server-stdout.log`. |
| `waitForStartedServer` | `cli/src/lib/server-lifecycle.ts:111` | Polls `runtime.json` + `/api/health`; the real port may differ from the requested one. |
| `stopServer` | `cli/src/lib/server-lifecycle.ts:248` | `POST /api/admin/shutdown` with the runtime token, then SIGTERM → SIGKILL; clears a stale `runtime.json`. |
| `serverStatus` / `probeRunning` / `ensureServerRunning` | `cli/src/lib/server-lifecycle.ts:284`,`:57`,`:307` | Liveness = runtime.json + live pid + `/api/health`; `ensureServerRunning` is what every admin command calls first. |
| `readRuntimeInfo` / `parseRuntimeJson` | `cli/src/lib/runtime.ts:46`,`:18` | Validates and reads `runtime.json` (pid/port/host/version/apiVersion/startedBy/runtimeToken). |
| `apiRequest` / `adminHeaders` | `cli/src/lib/http.ts:45`,`:28` | The only admin HTTP path: adds `X-Astra-Admin: 1` on non-GET and `X-Astra-Runtime-Token`; throws `ApiError` carrying the server's `{ error }` text. |
| `instanceInstallJson helpers` | `cli/src/lib/install-json.ts:13`,`:25`,`:37`,`:49` | Read/write and merge `install.json` without losing the other side's fields. |
| `ensureDesktopServer` | `cli/src/lib/desktop-server.ts:63` | Makes `install.json` point at a durable server for the desktop app; copies out of an npx/dlx cache into `~/.astra/server/astra-server-<version>` with `wwwroot` + native companions. |
| `npmInstallGlobal` | `cli/src/lib/desktop.ts:20` | CLI self-update: `npm install -g <PACKAGE_NAME>@latest` via update-core's `locateNpm`/`npmChildEnv`. |
| `autostartArgv` + `renderLaunchdPlist`/`renderSystemdUnit`/`schtasksCreateArgs` | `cli/src/lib/autostart.ts:99`,`:25`,`:59`,`:86` | Writes the per-platform autostart entry as `<binary> serve --started-by autostart`. |
| `findNewestLog` / `followFile` | `cli/src/lib/logs.ts:7`,`:41` | Newest `logs/astra-YYYYMMDD.log` else `logs/server-stdout.log`; 1 s polling tail. |
| `AstraError` | `cli/src/errors.ts:2` | Expected failure with an optional `hint` line. |
| `VERSION` / `PACKAGE_NAME` | `cli/src/version.ts:3`,`:10` | Both inlined from `cli/package.json` by tsup — `PACKAGE_NAME` is the name self-update targets. |

## Dependencies
- Uses: `update-core` via import (devDependency, bundled from source by tsup) — `checkForServerUpdate` (`cli/src/commands/manage.ts:2`), `fetchManifest`/`applyServerUpdate`/`installDesktopClient`/`removeLauncher`/`majorOf` (`cli/src/commands/desktop.ts:3`), `locateNpm`/`npmChildEnv` (`cli/src/lib/desktop.ts:2`), managed-binary install + companions (`cli/src/lib/desktop-server.ts:2`). It never imports a compiled copy.
- Uses: `astra-server` via subprocess + HTTP — spawns `serve`/`restore-all`/`set-password`/`version`/`healthcheck` (`cli/src/lib/server-lifecycle.ts:176`,`cli/src/commands/restore-all.ts:11`,`cli/src/commands/config.ts:16`,`cli/src/lib/server-lifecycle.ts:72`) and calls `/api/health`, `/api/admin/shutdown`, `/api/clients*`, `/api/providers*`, `/api/plugins*` (`cli/src/lib/http.ts:113`,`:129`, `cli/src/commands/admin.ts:107`, `cli/src/commands/plugin.ts:87`).
- Uses: `astra-server` via files — `runtime.json` (`cli/src/lib/runtime.ts:46`), `config.json` patch (`cli/src/lib/config-file.ts:19`), `logs/` (`cli/src/lib/logs.ts:7`).
- Uses: `packaging-release` via npm — resolves `@aidotnet/server-<platform>` as an optionalDependency (`cli/package.json:33`, `cli/src/lib/binary.ts:117`).
- Used by: `desktop-app` via file — reads `install.json` ("shape matches the CLI's InstallJson exactly", `packages/update-core/src/install-info.ts:5`; parser `desktop/src/shared/install.ts:7`).
- Used by: `packaging-release` via npm publish — `release.yml` packs `astragate`, then renames `cli/package.json` and rebuilds the same CLI as `@aidotnet/astra-gate` (`.github/workflows/release.yml:499`–`:505`).
- Used by: `docs-site` via docs only — the CLI command map is documented in `docs/content/docs/cli/index.mdx:15`.

## State it owns
- `~/.astra/install.json` — the CLI is its primary writer (`cli/src/lib/install-json.ts:17`); fields `serverPath`, `serverVersion`, `desktopPath`, `desktopVersion`, `updatedAt` (`cli/src/lib/install-json.ts:5`). `update-core` writes the identical shape (`packages/update-core/src/install-info.ts:10`) and clears `serverPath` on a rollback of a standalone installer (`clearServerInfo`, `:52`) — the two must stay compatible.
- `~/.astra/config.json` — patched for `port`/`host` only, and only when a flag was passed or the key was missing (`cli/src/lib/server-lifecycle.ts:141`, `cli/src/lib/config-file.ts:19`). It is **authoritative for nothing at runtime**; the server may drift the port.
- `~/.astra/server/astra-server-<version>[.exe]`, `~/.astra/updates/`, `~/.astra/backups/`, `~/.astra/update-state.json` — owned by `update-core` (`packages/update-core/src/paths.ts:19`), reached through the CLI only via import.
- `~/.astra/desktop/` — the installed desktop payload (`cli/src/lib/paths.ts:38`), removed by `runUninstallClient` (`cli/src/commands/desktop.ts:63`).
- Autostart entries outside `~/.astra`: `~/Library/LaunchAgents/io.astra.server.plist`, `~/.config/systemd/user/astra.service`, the schtasks task `Astra Server` (`cli/src/lib/autostart.ts:21`,`:51`,`:10`).
- Read-only for the CLI: `runtime.json` (`cli/src/lib/runtime.ts:6` — written by the server), `logs/*` (`cli/src/lib/logs.ts`).

## Contracts it exposes
- Command surface (`cli/src/index.ts`): `start` (default) / `stop` / `restart` / `status` / `logs [-f]` / `open`, each of start/restart taking `--port --host --foreground --open` (`:47`); `autostart enable|disable|status` (`:94`); `install [--desktop|--client]` (`:108`); `uninstall [--desktop|--client] [--purge]` (`:117`); `update [--desktop|--client] [--check] [--retry]` (`:128`); `client list|status|enable --provider <id|name> [--model] [--token]|disable` (`:139`); `token list` (`:169`); `provider list|add --template --key [--variant --name]|import [--from --yes --only]|remove <id>` (`:175`); `plugin`/`plugins` `list [--json]|add <spec>|remove|on|off|update [name]|options <name> [json]|use <name> <provider>|market [query]` (`:203`); `config set-password` (`:248`); `claude [--account] [--status]` (`:255`); `restore-all [--purge]` (`:262`).
- Env vars the CLI itself reads: `ASTRA_HOME` (`cli/src/lib/paths.ts:22`), `ASTRA_SERVER_BIN` (`cli/src/lib/binary.ts:67`), `ASTRA_DEBUG` (`cli/src/index.ts:286`), `NO_COLOR`/`TERM` (`cli/src/ui.ts:4`), `CLAUDE_CONFIG_DIR` (`cli/src/commands/claude.ts:211`).
- `install.json` fields (above) — the cross-process anchor for `desktop-app` and `update-core`.
- The CLI's HTTP footprint: every call goes through `apiRequest` with `Accept: application/json`, `X-Astra-Admin: 1` on non-GET, and `X-Astra-Runtime-Token` when `runtime.json` carries one (`cli/src/lib/http.ts:28`).
- Admin endpoints used, by command group: `/api/clients`, `/api/clients/{kind}/enable|disable`, `/api/tokens`, `/api/providers` (+`/{id}` DELETE), `/api/provider-templates`, `/api/providers/import[/sources]` (`cli/src/commands/admin.ts`); `/api/plugins` (GET/POST/PATCH/DELETE `?spec=`), `/api/plugins/jobs/{id}`, `/api/plugins/update`, `/api/plugins/prefer`, `/api/plugins/search`, `/api/plugins/market` (`cli/src/commands/plugin.ts`); `/api/clients/claude-code/direct/prepare` (`cli/src/commands/claude.ts:25`); `/api/admin/shutdown`, `/api/health` (`cli/src/lib/http.ts:113`,`:129`); `/api/version` during an update probe (`cli/src/commands/desktop.ts:185`).
- Bundle contract: single ESM file with a `#!/usr/bin/env node` banner, `format: ['esm']`, `target: node18`, `splitting: false` (`cli/tsup.config.ts`).

## How to extend
- To add a command: declare it in `cli/src/index.ts` and put the work in `cli/src/commands/<area>.ts` returning `Promise<void>`; throw `AstraError` with a hint for expected failures so the root handler prints `error: …` + hint (`cli/src/index.ts:273`) — a plain `Error` loses the hint and demands `ASTRA_DEBUG`.
- To add an API-backed command: copy the `api()` helper shape of the group you are extending (`cli/src/commands/admin.ts:53` for a 10 s timeout, `cli/src/commands/plugin.ts:55` for the 60 s one) — it does `ensureServerRunning()`, passes `runtime.runtimeToken`, and announces an auto-start. Calling `fetch` directly skips `X-Astra-Admin` and gets a 403 on any mutation.
- To change binary resolution order: `cli/src/lib/binary.ts:60` **and** its stated mirror `desktop/src/shared/resolveServerBinary.ts` (see the order comment at `cli/src/lib/binary.ts:52`); changing only the CLI makes the desktop app and the CLI disagree about which server runs.
- To move a `~/.astra` path: `cli/src/lib/paths.ts:26`, `src/Astra.Core/AstraPaths.cs` and `desktop/src/shared/paths.ts` together; a path that exists in only one of the three makes the other side read a file nobody writes.
- To add an autostart mechanism: extend the three renderers plus the `dispatch` switch (`cli/src/commands/autostart.ts:22`) — a missing branch throws `Autostart is not supported on …`.

## Invariants and traps
- Never take the port from `config.json`; the server may have moved to the next free port, so the real address is `runtime.json` (`cli/src/lib/server-lifecycle.ts:107`, `cli/src/lib/runtime.ts:6`). `startServer` passes `--port` but returns `rt.port` (`cli/src/lib/server-lifecycle.ts:231`).
- The update manifest is keyed by the .NET RID, not `process.platform` — `platformKey: plat.rid` (`cli/src/commands/desktop.ts:199`, comment at `:199`). The wrong key silently drops the sha256 check; commit 628e692 fixed exactly this.
- The CLI has **no session/cookie support**: `adminHeaders` only ever sends `X-Astra-Admin`/`X-Astra-Runtime-Token` (`cli/src/lib/http.ts:28`). With a password set and the server listening off-loopback, `SecurityMiddleware.SessionRequired` turns every `/api` call into 401 (`src/Astra.Server/Security/Security.cs:141`) — that is why `runSetPassword` parses the binary's stdin instead of posting the password.
- `ASTRA_SERVER_BIN` that points at a missing file is a hard error, not a fall-through to the next source (`cli/src/lib/binary.ts:69`).
- `install.json` that is corrupt or unparsable reads as `null`, silently demoting resolution to the repo build or the platform package (`cli/src/lib/json-file.ts:15`, `cli/src/lib/binary.ts:76`).
- The repo dev build is found by walking up from `process.cwd()` for `src/Astra.Server` (`cli/src/lib/binary.ts:37`,`:82`) — running `astra` from inside the repo runs `bin/Debug/net10.0`, not the installed server, and `dotnet <dll>` is used when only the dll exists (`:88`).
- npx / pnpm-dlx paths are throwaway caches and never a durable server path (`cli/src/lib/desktop-server.ts:24`); `astra install --desktop` copies the binary plus `wwwroot` and the native companion libraries into the managed dir because the server loads both from its own executable's directory (`cli/src/lib/desktop-server.ts:53`).
- A recorded `install.json` server older than the running CLI is replaced, and an ephemeral path is rejected (`hasUsableServer`, `cli/src/lib/desktop-server.ts:29`).
- Self-update targets `PACKAGE_NAME`, which tsup inlines from `package.json` at build time; `release.yml` rebuilds the package after renaming it to `@aidotnet/astra-gate` for that reason (`cli/src/version.ts:5`, `.github/workflows/release.yml:501`). Hard-coding `astragate` would update the wrong package for scoped-name installs.
- Provider import never passes an API key through the CLI: it posts `{source, ref}` and the server re-reads the source (`cli/src/commands/admin.ts:327`); the listing shows only a mask.
- Plugin name lookup accepts spec, package name or short name in that order (`cli/src/commands/plugin.ts:61`) — an ambiguous short name picks the first match silently.
- Long-running plugin jobs are polled from `/api/plugins/jobs/{id}` every 800 ms, printing only newly appended log lines (`cli/src/commands/plugin.ts:71`).
- `stopServer` treats a failed shutdown request as non-fatal and escalates SIGTERM → SIGKILL (`cli/src/lib/server-lifecycle.ts:257`), so a stopped server always clears `runtime.json` (`:271`).
- `runLogs -f` never resolves — it parks on a forever promise and relies on the SIGINT handler (`cli/src/commands/manage.ts:95`).
- `astra update` updates the CLI by shelling out to npm, and only mentions the failure: the server is already updated by then (`cli/src/commands/desktop.ts:97`).

## Tests
`cli/test/**` (vitest, `environment: 'node'`, `include: ['test/**/*.test.ts']` — `cli/vitest.config.ts:5`), one `<area>.test.ts` per lib/command group: `binary`, `server-lifecycle`, `runtime`, `http`, `install-json`, `paths`, `platform`, `autostart`, `desktop-server`, `admin`, `plugin`, `claude` (45–631 lines each).
- Setup pattern: pure functions are called directly, and command tests `vi.mock('../src/lib/server-lifecycle.js')` + `vi.mock('../src/lib/http.js')` to capture calls instead of touching a real server (`cli/test/admin.test.ts:6`). Imports inside tests use extensionless specifiers (`../src/lib/http`).
- Run one file: `pnpm --filter astragate test test/binary.test.ts` (all: `pnpm --filter astragate test`; typecheck `pnpm --filter astragate typecheck`).

## Related flows
- [server-startup](../flows/server-startup.md)
- [admin-api-request](../flows/admin-api-request.md)
- [client-config-takeover](../flows/client-config-takeover.md)
- [plugin-install-and-run](../flows/plugin-install-and-run.md)
- [release-and-update](../flows/release-and-update.md)
