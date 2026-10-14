---
module: update-core
sources: ["packages/update-core/**"]
commit: 6324e30
updated: 2026-10-14
---
# update-core (`@aidotnet/update-core`)

The shared, dependency-free update engine: it fetches the `latest.json` release manifest, stages an
`npm` install of the platform server package into a throwaway prefix, verifies its sha256, swaps the
binary (plus its native companions and `wwwroot`) to a version-suffixed path, repoints
`install.json`, restarts the server and rolls the whole thing back on failure — persisting every
phase to `~/.astra/update-state.json`. It also installs the npm-track desktop app and its launcher,
and owns the npm invocation plumbing (PATH repair for GUI-launched processes) both consumers need.
**Not responsible for:** producing or serving the manifest (`packaging-release`, `docs-site`), the
server-side feed check and cache (`src/Astra.Server/Hosting/UpdateCheckService.cs`,
`Api/UpdateEndpoints.cs`), deciding *when* to update or rendering progress (`cli`, `desktop-app`,
`web-ui`), binary resolution at launch (`cli/src/lib/binary.ts`,
`desktop/src/shared/resolveServerBinary.ts`), and the CLI's own `install.json` read/write helpers
(`cli/src/lib/install-json.ts`).

## Entry points
| Symbol | File | Role |
|---|---|---|
| public barrel (every `export` below) | `packages/update-core/src/index.ts:1` | The only surface consumers see; `main`/`types`/`exports` all point here. |
| `applyServerUpdate` | `packages/update-core/src/apply.ts:94` | The orchestrator: staged download → swap → restart → commit, with the rollback leg. |
| `checkForServerUpdate` | `packages/update-core/src/check.ts:20` | Version-only check; prefers the running server's `/api/update/status`, falls back to the feed. |
| `fetchManifest` | `packages/update-core/src/manifest.ts:46` | Fetches `<feed>/<channel>/latest.json` and validates that a version exists. |
| `stageServerBinary` | `packages/update-core/src/staging.ts:71` | `npm install --prefix <staging>` + sha256 verification before anything is touched. |
| `installManagedBinary` / `installManagedWebRoot` | `packages/update-core/src/swap.ts:93`,`:175` | The two managed-artifact installers, both with the `.prev` rollback lifecycle. |
| `installDesktopClient` | `packages/update-core/src/desktop-install.ts:201` | npm-track desktop install under `~/.astra/desktop` plus the per-OS launcher. |

## Key types and functions
| Symbol | File | Role |
|---|---|---|
| `UpdateManifest` / `UpdatePlatformInfo` | `packages/update-core/src/manifest.ts:16`,`:6` | The feed document; `platforms` is keyed by .NET RID. |
| `DEFAULT_FEED_URL` / `DEFAULT_CHANNEL` / `UpdateFeedError` | `packages/update-core/src/manifest.ts:26`,`:28`,`:39` | `https://astra-gate.si/api/client-releases`, `stable`, and the feed-failure error type. |
| `ApplyServerUpdateOptions` / `ServerControl` / `UpdateError` | `packages/update-core/src/apply.ts:51`,`:43`,`:32` | The executor's inputs and the host-process control surface it drives. |
| `UpdateState` / `UpdatePhase` / `MAX_CONSECUTIVE_FAILURES` | `packages/update-core/src/state.ts:14`,`:5`,`:30` | Persisted phase machine; 2 consecutive failures lock out until `force`. |
| `readUpdateState` / `writeUpdateState` | `packages/update-core/src/state.ts:47`,`:73` | Tolerant read (corrupt ⇒ `null`), atomic tmp+rename write. |
| `updatePaths` / `managedServerBinaryPath` | `packages/update-core/src/paths.ts:19`,`:29` | The updater's own `~/.astra` layout, and `astra-server-<version>[.exe]`. |
| `InstallInfo` / `applyServerInfo` / `clearServerInfo` | `packages/update-core/src/install-info.ts:10`,`:61`,`:52` | The cross-process `install.json` anchor; `clearServerInfo` is the standalone-installer rollback. |
| `NATIVE_LIBRARY` / `findNativeCompanions` / `sha256File` | `packages/update-core/src/staging.ts:45`,`:52`,`:7` | Which siblings are native companions, and the checksum used to verify the staged binary. |
| `backupCurrentBinary` / `pruneBackups` / `pruneManagedBinaries` | `packages/update-core/src/swap.ts:18`,`:49`,`:150` | Rolling last-resort backup and both prune policies. |
| `commitNativeCompanions` / `restoreNativeCompanions` / `commitManagedWebRoot` / `restoreManagedWebRoot` | `packages/update-core/src/swap.ts:127`,`:138`,`:191`,`:196` | The commit/rollback halves of the `.prev` lifecycle. |
| `locateNpm` / `npmChildEnv` / `withNodeOnPath` / `npmInstallIntoPrefix` / `npmView` | `packages/update-core/src/npm-runner.ts:21`,`:67`,`:197`,`:221`,`:235` | System-npm discovery, parent-npm env scrubbing, GUI PATH repair, and the install/view primitives. |
| `parseVersion` / `isNewerVersion` / `majorOf` / `isApiVersionCompatible` | `packages/update-core/src/version.ts:6`,`:19`,`:32`,`:43` | Semver-core comparison (suffixes ignored) and the API-major compatibility gate. |
| `findDesktopEntry` / `createLauncher` / `removeLauncher` | `packages/update-core/src/desktop-install.ts:57`,`:133`,`:163` | Locates the packaged app and installs the symlink / `.lnk` / `.desktop` launcher. |

## Dependencies
- Uses: **no workspace package at all** — imports are `node:fs`, `node:path`, `node:crypto`,
  `node:os`, `node:child_process` only (`packages/update-core/src/npm-runner.ts:1`,
  `packages/update-core/src/staging.ts:1`); `package.json` declares only devDependencies
  (`packages/update-core/package.json:18`), so it is safe to bundle into any host.
- Uses: `docs-site` via HTTP — `${feed}/<channel>/latest.json` (`packages/update-core/src/manifest.ts:49`),
  default origin `https://astra-gate.si/api/client-releases` (`manifest.ts:26`), served by
  `docs/app/api/client-releases/[channel]/[...path]/route.ts:80`.
- Uses: `astra-server` via HTTP — `/api/update/status` as the cheap check path
  (`packages/update-core/src/check.ts:33`).
- Uses: `packaging-release` via npm + HTTP — `npm install --prefix <staging> <pkg>@<version>` where
  the package comes from `manifest.platforms[rid].server` (`packages/update-core/src/apply.ts:139`,
  `packages/update-core/src/npm-runner.ts:221`); the sha256 it checks is the one
  `scripts/gen-manifest.mjs:54` wrote.
- Used by: `cli` via import (devDependency, bundled from source by tsup) —
  `applyServerUpdate` (`cli/src/commands/desktop.ts:196`), `checkForServerUpdate`
  (`cli/src/commands/manage.ts:2`), `fetchManifest`/`installDesktopClient`/`removeLauncher`/`majorOf`
  (`cli/src/commands/desktop.ts:3`), `updatePaths`/`installManagedBinary`/`installManagedWebRoot`/
  `commit*` (`cli/src/lib/desktop-server.ts:2`), `locateNpm`/`npmChildEnv` (`cli/src/lib/desktop.ts:2`).
- Used by: `desktop-app` via import (bundled from source by esbuild) — the whole apply path
  (`desktop/src/update.ts:4`) and `isNewerVersion` in the resolver
  (`desktop/src/shared/resolveServerBinary.ts:2`).
- Shared contract with: `astra-server` via JSON over HTTP — the .NET `UpdateManifest`
  (`src/Astra.Server/Hosting/UpdateCheckService.cs:15`) mirrors `manifest.ts` for the same
  `latest.json`; nothing on either side imports the other.
- Used by: `web-ui` via HTTP, **indirectly** — the admin UI reads the *server's* `/api/update/status`
  (`web/src/api/hooks.ts:238`); it never touches this package or `update-state.json`.

## State it owns
- `~/.astra/update-state.json` (`updatePaths().stateFile`, `packages/update-core/src/paths.ts:24`) —
  the only phase source of truth, written by `applyServerUpdate` via `writeUpdateState`
  (`packages/update-core/src/apply.ts:116`); it lives outside SQLite because the executor may run
  while the server is stopped (`paths.ts:3`).
- `~/.astra/server/astra-server-<version>[.exe]` + the native companions + `wwwroot` beside it
  (`paths.ts:29`) — the managed install; `install.json` is what makes a resolver pick it.
- `~/.astra/updates/staging` (`paths.ts:21`) — disposable npm prefix, `rm -rf`'d at the start of
  every stage (`staging.ts:73`) and after a successful swap (`apply.ts:194`).
- `~/.astra/backups/server/astra-server-<version>[-libs]` (`paths.ts:22`, `swap.ts:32`) — at most
  `MAX_BACKUPS = 3` units, pruned as a pair (`swap.ts:49`).
- `~/.astra/install.json` — **co-owned**: the CLI is its primary writer (`cli/src/lib/install-json.ts:17`)
  and this module writes the identical shape (`install-info.ts:5`); it is the only place that clears
  the server pointer on rollback (`install-info.ts:52`).
- `~/.astra/desktop/` npm prefix and the launcher outside the data dir (`desktop-install.ts:202`,
  `:133`).
- Not owned / never a cache of truth: the manifest (the server caches its own copy under settings key
  `update.state`, `src/Astra.Server/Hosting/UpdateCheckService.cs:48`) and the transient `*.tmp` /
  `*.prev` siblings (`swap.ts:96`,`:180`), which are mid-flight artifacts rather than live files.

## Contracts it exposes
- **Manifest** (`UpdateManifest`, `packages/update-core/src/manifest.ts:16`): `{ version, apiVersion?,
  releasedAt?, notes?, minUpdateable?, platforms?: { '<rid>': { server?, desktop?, serverSha256?,
  serverSize? } } }`, fetched from `${feedUrl}/${channel}/latest.json` with URL/trailing-slash
  normalisation (`manifest.ts:49`). Only `version` is validated (`manifest.ts:74`); `desktop` is
  declared in TS but never written by `scripts/gen-manifest.mjs:73` — desktop installs pass their own
  `desktopPackage` instead (`packages/update-core/src/desktop-install.ts:194`).
- **Feed URL**: `DEFAULT_FEED_URL` (`manifest.ts:26`), overridable per call; the server-side
  `updateFeedUrl` setting (empty string = disabled, `UpdateCheckService.cs:53`) reaches callers only
  as `ServerUpdateStatus.feedConfigured` (`check.ts:12`).
- **`~/.astra/update-state.json`** `{ phase, targetVersion?, previousVersion?, step?, error?,
  failedCount, updatedAt }` (`state.ts:14`), phases `idle|checking|downloading|swapping|restarting|
  rolling-back|failed` (`state.ts:5`), lock at 2 failures (`state.ts:30`).
- **`~/.astra/install.json`** `{ serverPath?, serverVersion?, desktopPath?, desktopVersion?,
  updatedAt? }` (`install-info.ts:10`) — parser keeps only non-empty strings and drops anything else
  (`install-info.ts:30`); the shape must match `cli/src/lib/install-json.ts:5`.
- **`ServerControl`** (`apply.ts:43`) — `isRunning`/`stop`/`start`/`probeVersion`, implemented by each
  host (`cli/src/commands/desktop.ts:173`, `desktop/src/update.ts:79`).
- **npm execution contract**: the child env is `npmChildEnv` minus `npm_config_(allow[-_]scripts|call|package)`
  (`npm-runner.ts:64`) with `withNodeOnPath` applied (`npm-runner.ts:197`), and every entry point takes
  the `NpmRunnerOptions` test seams (`npm-runner.ts:120`).
- The barrel `src/index.ts:1` is the entire API; nothing else in the package is importable by path.

## How to extend
- To add a manifest field: declare it on `UpdateManifest` (`packages/update-core/src/manifest.ts:16`),
  consume it in `apply.ts`/`check.ts`, then register it on the .NET mirror
  (`src/Astra.Server/Hosting/UpdateCheckService.cs:15`) and in `scripts/gen-manifest.mjs:83`.
  Missing the .NET mirror makes the field invisible to server-side checks while the TS side still
  reads it — the two diverge silently.
- To add a phase: add it to `UpdatePhase` **and** `KNOWN_PHASES` (`packages/update-core/src/state.ts:5`,
  `:32`), then set it in `applyServerUpdate`. A phase missing from `KNOWN_PHASES` makes
  `readUpdateState` treat the whole file as corrupt and return `null` (`state.ts:57`), which resets
  `failedCount` and drops the failure lock.
- To carry a new managed artifact (a second library, a data directory): pick it up in
  `findNativeCompanions` via `NATIVE_LIBRARY` (`staging.ts:45`) or write a sibling of
  `installManagedWebRoot` with the same `dest.tmp` → `dest.prev` sequence (`swap.ts:175`), then call
  the commit in the success path (`apply.ts:195`–`:198`) and the restore in `rollback`
  (`apply.ts:241`–`:244`). Missing the restore half leaves a rolled-back binary with libraries built
  for the other version; missing the commit half leaves `.prev` files forever.
- To add a platform: nothing here — the caller passes `platformKey` (RID) and `serverPackage`
  (`apply.ts:54`); the CLI derives both from `cli/src/lib/platform.ts:38` and the desktop app from its
  own prefix mapping (`desktop/src/update.ts:337`), so both must produce the same .NET RID.
- To add a new update subject: model it on `installDesktopClient` (`desktop-install.ts:201`) plus the
  `kind: 'desktop'` branch of `backupCurrentBinary` (`swap.ts:27`) and the matching `install.json`
  fields (`install-info.ts:61`).

## Invariants and traps
- **`platformKey` is a .NET RID, not `process.platform`** (`osx-arm64`, not `darwin-arm64`). A wrong
  key makes `manifest.platforms[key]` `undefined`, which does **not** fail the update — it silently
  skips the sha256 check (`apply.ts:139`, `staging.ts:84`). Fixed in 628e692 for the CLI
  (`cli/src/commands/desktop.ts:199`); the desktop app builds the same key inline
  (`desktop/src/update.ts:337`).
- Nothing running is ever replaced in place: a fresh version-suffixed file is installed and
  `install.json` repoints at it, so Windows file locks are irrelevant (`swap.ts:83`);
  `pruneManagedBinaries` never deletes the current target (`swap.ts:150`).
- Companions share one filename across versions, so they use copy → rename with a `<name>.prev`
  snapshot (`swap.ts:104`), while `wwwroot` uses directory `.tmp` → `.prev` (`swap.ts:175`). Both
  commit and restore are decided purely by the presence of `.prev` — a `.prev` left by an unrelated
  run is committed/restored by the next one.
- `NATIVE_LIBRARY` (`staging.ts:45`) must stay in sync with `scripts/pack-platform.mjs`; changing one
  side only makes the pack step write companions the updater will not carry (or vice versa).
- A Native AOT binary without its companion library dies on the first database access, so the
  rollback path restores companions *before* restarting the old binary (`apply.ts:244`) and
  `backupCurrentBinary` snapshots them into `<name>-libs` (`swap.ts:36`).
- `sha256File` reads the whole file into memory (`staging.ts:9`) — ~90 MB per verification, and only
  the executable is hashed; the companions have no integrity anchor (AGENTS.md "Known gap").
- Two consecutive failures lock out further automatic attempts until `force`/`--retry`
  (`state.ts:30`, `apply.ts:100`); `force` also clears the counter on success (`apply.ts:199`).
- Rolling back an install that had **no** managed binary (standalone installer, `currentServerPath:
  null`) clears the `install.json` server pointer instead of restoring a path — otherwise the newer
  failed binary keeps winning over the bundled server (`install-info.ts:52`, `apply.ts:233`); the
  file is deleted when nothing else was recorded (`install-info.ts:57`).
- GUI-launched processes (Finder/Dock Electron, or a `.app`-spawned CLI) start with launchd's minimal
  PATH, where npm's `#!/usr/bin/env node` shebang cannot find node; `withNodeOnPath` appends the
  located npm's own dir, then `/opt/homebrew/bin` + `/usr/local/bin`, then the login shell's PATH, and
  does nothing on Windows (`npm-runner.ts:188`). The login-shell probe is memoised in a module-level
  promise (`npm-runner.ts:148`), so a shell that changes PATH later in the process's life is not
  re-read.
- Staging is only wiped on **success** (`apply.ts:194`); a failed run leaves the ~90 MB prefix behind,
  but the next `stageServerBinary` removes it first (`staging.ts:73`).
- The `wwwroot` swap is best-effort and swallows its error after the binary is already committed
  (`apply.ts:180`) — a failure leaves the new server serving the old UI, and `webRootSwapped` then
  keeps rollback from touching it.
- `checkForServerUpdate` swallows every failure of `/api/update/status` and falls through to the feed
  (`check.ts:31`–`:47`); on the `server` path it returns **no** manifest, so a caller that needs
  `serverSha256` must re-fetch (which `desktop/src/update.ts:305` does).
- Unparsable versions are never "newer" (`version.ts:19`), so `isNewerVersion('0.1.0-rc.1', '0.1.0')`
  is `false`: a prerelease-suffixed *current* version can never be moved to the plain release, and a
  garbage `version` in the feed reads as "up to date" rather than erroring.
- The `majorOf`/`isApiVersionCompatible` gate refuses a cross-major update before anything is
  downloaded (`apply.ts:109`) — "a half-updated pair is worse than an old pair" (comment on
  `applyServerUpdate`).
- `UpdateState` is written by this module and, as of this snapshot, read by **no one in-tree**: grep
  for `update-state|updateState|UpdateState` outside the package finds only docs/README mentions
  (`web/src/**` and `desktop/src/**` have no reader). It is an exported contract for a future UI/tray
  surface, not a live one.
- Source-only package: `main`/`types`/`exports` all point at `src/index.ts` (`package.json:8`), there
  is no `build` script and it is `"private": true` (`package.json:4`), so it is never published and
  its own `version` (0.1.0) is deliberately outside `scripts/sync-versions.mjs`'s npm/ + desktop
  target list (`scripts/sync-versions.mjs:38`). Consumers bundle it from source (tsup for the CLI,
  esbuild for desktop), so its `typecheck`/`lint`/`test` run against `src` directly
  (`tsconfig.json:7`) — and a type error surfaces when a consumer bundles, not when it is published.

## Tests
`packages/update-core/test/*.test.ts`, vitest with `environment: 'node'` and
`include: ['test/**/*.test.ts']` (`packages/update-core/vitest.config.ts:3`): `apply.test.ts` (the
whole state machine: success, non-newer/API-major refusal, checksum mismatch, wrong reported version,
rollback of binary/wwwroot/companions, standalone-installer pointer clearing, failure lock),
`staging.test.ts`, `state.test.ts`, `version.test.ts`, `npm-runner.test.ts`, `desktop-install.test.ts`.
Run one file: `pnpm --filter @aidotnet/update-core test test/apply.test.ts` (all:
`pnpm --filter @aidotnet/update-core test`; typecheck `pnpm --filter @aidotnet/update-core typecheck`).
The apply tests never touch npm or a real server: `installIntoPrefix` is a fake installer that lays
out `node_modules/<pkg>/bin/astra-server` (`test/apply.test.ts:38`) and `ServerControl` is a
`FakeControl` (`test/apply.test.ts:15`); `npm-runner.test.ts` proves the PATH repair without a shell
by injecting `loginShellPath`/`wellKnownBinDirs` (`test/npm-runner.test.ts:58`).

## Related flows
- [release-and-update](../flows/release-and-update.md)
