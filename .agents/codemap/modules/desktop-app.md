---
module: desktop-app
sources: ["desktop/**"]
commit: 6324e30
updated: 2026-10-14
---
# desktop-app

The Electron shell `@aidotnet/desktop` (`desktop/`, private package, `main: dist/main.js`): it serves the
built web UI over the privileged `app://astra` scheme with a strict CSP, owns the lifecycle of the
`astra-server` child process (resolve → adopt or replace → spawn → probe → shut down), and drives the tray /
menu-bar icon, the tray-panel popover, native menus and the three update surfaces (electron-updater DMG,
macOS npm self-replace, server binary via `update-core`). The main process is the only place that may spawn
processes, read/write files or show native UI; the renderer reaches it solely through the preload bridge.
**Not responsible for:** the UI itself (`web-ui` builds `web/dist`, copied into `desktop/renderer` and loaded
here), the server binary and every admin endpoint it calls (`astra-server`), the update state machine,
manifest shape and npm/desktop installers used by the controller (`update-core`), the installer
definitions, `server-bundle` staging and npm platform packages (`packaging-release`), and the other
launcher of the same binary with a deliberately different resolution order (`cli`).

## Entry points
| Symbol | File | Role |
|---|---|---|
| `main()` + bottom-of-file bootstrap | `desktop/src/main.ts:87`, `desktop/src/main.ts:852` | Composition root: single-instance lock, `registerSchemesAsPrivileged` (`:52`), then wires `ServiceManager`, `UpdateController`, `TrayController`, `TrayPanel`, IPC and windows. Every module-level `let` is declared above `main()` on purpose (see the comment at `:850`). |
| `registerIpc()` | `desktop/src/main.ts:659` | Declares every `ipcMain.handle` / `ipcMain.on` channel; called before `app.whenReady()`. |
| `handleAppRequest()` | `desktop/src/appProtocol.ts:22` | `app://astra` request handler: host check, path resolution, CSP + `nosniff` headers, SPA fallback. Registered at `desktop/src/main.ts:187`. |
| `rendererWebPreferences()` | `desktop/src/main.ts:587` | Shared `WebPreferences` for the main window and the tray panel: `dist/preload.js`, `contextIsolation`, `sandbox: true`, and the four `--astra-*` switch arguments. |
| `preload.ts` bridge | `desktop/src/preload.ts:35`, `desktop/src/preload.ts:79` | `contextBridge.exposeInMainWorld('__ASTRA__', …)`; reads the switch args at `desktop/src/preload.ts:13`. |
| `ServiceManager.ensureRunning()` | `desktop/src/service.ts:148` | Probe → adopt a version-matching server, replace a mismatching one (adopt it after all when it cannot be stopped — never a second server), else spawn and wait ≤20 s for health. |
| `resolveServerBinary()` | `desktop/src/shared/resolveServerBinary.ts:55` | install.json > `ASTRA_SERVER_BIN` > bundled > dev, with the installer-upgrade staleness rule. |
| `UpdateController.check()` / `applyFromUi()` / `start()` | `desktop/src/update.ts:211`, `:364`, `:143` | Manual check, in-app apply, and the launch + 12 h background checks. |
| `TrayController` / `buildTrayMenuTemplate()` | `desktop/src/tray.ts:25`, `desktop/src/shared/trayMenu.ts:124` | Tray icon, tooltip/menu-bar title, and the pure native-menu model. |
| `TrayPanel` / `trayPanelBounds()` | `desktop/src/trayPanel.ts:21`, `desktop/src/shared/trayPanel.ts:66` | The frameless popover window and the pure placement math. |
| `desktop/scripts/build.mjs` / `copy-web.mjs` | `desktop/scripts/build.mjs:17`, `desktop/scripts/copy-web.mjs:39` | esbuild bundles `src/main.ts` → `dist/main.js`, `src/preload.ts` → `dist/preload.js` (CJS, `external: ['electron']`); copy-web stages `web/dist` → `desktop/renderer`. |

## Key types and functions
| Symbol | File | Role |
|---|---|---|
| `ProbeResult` / `RunningService` / `StopResult` / `StartupError` | `desktop/src/service.ts:31`, `:41`, `:49`, `:21` | Probe and startup results; `StartupError.logPath` is what the retry dialog prints. |
| `ServiceManager.probe()` | `desktop/src/service.ts:122` | Reads `runtime.json` (falling back to `config.json`'s port when the runtime is stale), health-checks, then `/api/version`. |
| `ServiceManager.stopService()` / `stopIfOurs()` | `desktop/src/service.ts:182`, `:221` | `POST /api/admin/shutdown` with a fresh runtime's token, 5 s HTTP grace, then SIGTERM/`taskkill` at the runtime pid, else the astra-server listening on the port (`findServerListenerPid`, `desktop/src/shared/listener.ts:61`: lsof / netstat + an executable-name check), else the app's own child; `stopIfOurs` is the quit-time guard. |
| `ServiceManager.computeMismatch()` | `desktop/src/service.ts:111` | `isApiVersionCompatible` against `EXPECTED_API_MAJOR`. |
| `shouldReplaceRunningServer()` | `desktop/src/shared/resolveServerBinary.ts:92` | Adopt/replace decision; both versions must be known before anything is killed. |
| `EXPECTED_API_MAJOR` / `isApiVersionCompatible()` | `desktop/src/shared/version.ts:6`, `:15` | The desktop↔server handshake floor; a different major shows the mismatch dialog (`desktop/src/main.ts:258`). |
| `resolveUpdateTrack()` | `desktop/src/shared/updateTrack.ts:17` | `dmg` / `npm` / `installer` / `disabled` from `isPackaged`, the real exe path and `~/.astra/desktop`. |
| `decideReminder()` / `recordReminder()` / `skipVersion()` | `desktop/src/shared/updateReminder.ts:40`, `:57`, `:66` | Reminder circuit breaker: cooldown, per-version cap, skip-version. |
| `UpdateController.runCheck()` | `desktop/src/update.ts:227` | The lock-free check body; `applyFromUi` must call this, never `check()`. |
| `TrayState` / `TrayCallbacks` / `trayStateKey()` | `desktop/src/shared/trayMenu.ts:15`, `:29`, `:120` | Menu input model, the callbacks `main.ts` wires, and the change-detection key. |
| `parsePrefs()` / `parseTrayPrefs()` | `desktop/src/shared/prefs.ts:75`, `:62` | Tolerant `desktop-prefs.json` parsing; also the validator for `astra:tray-prefs-set`. |
| `sanitizeContextMenuItems()` / `sanitizeTrayTitle()` / `isTrayPanelCommand()` | `desktop/src/shared/chrome.ts:100`, `desktop/src/shared/trayPanel.ts:52`, `:47` | Untrusted-renderer-input validation for the three renderer-driven native surfaces. |
| `api.ts` request helpers | `desktop/src/api.ts:35` | `checkHealth`, `fetchVersion`, `fetchUpdateAutoCheck`, `refreshUpdateCheck`, `fetchClients`, `fetchProviders`, `putBinding`, `requestShutdown` — all plain `fetch` against `apiBase(port)`. |
| `adminHeaders()` | `desktop/src/shared/requestHeaders.ts:6` | `X-Astra-Admin: 1` plus `X-Astra-Runtime-Token` for mutating `/api` calls. |
| `resolveRendererPath()` / `contentTypeFor()` | `desktop/src/shared/rendererPath.ts:55`, `:40` | Path traversal defense (`..`, NUL, root containment), SPA fallback and the MIME table. |
| `windowChromeOptions()` / `navHash()` / `menuLabels()` | `desktop/src/shared/chrome.ts:57`, `:14`, `:125` | Platform window chrome, hash routes, and the zh/en native-menu labels. |

## Dependencies
- Uses: `web-ui` via build artifact + HTTP — `desktop/scripts/copy-web.mjs:39` copies `web/dist` into
  `desktop/renderer`; `desktop/src/main.ts:185` serves it from `<appRoot>/renderer`.
- Uses: `astra-server` via child process + HTTP — spawn at `desktop/src/service.ts:224`
  (`serve --started-by desktop --port <config.port>`), all API calls in `desktop/src/api.ts:21`.
- Uses: `update-core` via import (bundled from source by esbuild, never published) —
  `desktop/src/update.ts:4` (`applyServerUpdate`, `checkForServerUpdate`, `fetchManifest`,
  `installDesktopClient`, `isApiVersionCompatible`), `desktop/src/shared/resolveServerBinary.ts:2`.
- Uses: `packaging-release` via electron-builder config — `desktop/electron-builder.yml:15` `extraResources`
  stages `server-bundle` → `resources/server`, which is what `bundledServerBinaryPath` looks for.
- Uses: `cli` via the files it leaves behind — `~/.astra/install.json` (`desktop/src/shared/install.ts:7`,
  "written by the CLI") and `installDesktopPath` = `~/.astra/desktop` (`desktop/src/main.ts:105`) which
  decides the `npm` update track.
- Used by: `web-ui` via IPC — `desktop/src/preload.ts:79` injects `window.__ASTRA__`; the renderer mirrors the
  contract in `web/src/shell/bridge.ts:90` and detects it at `web/src/shell/bridge.ts:97`.
- Used by: `packaging-release` via electron-builder — `desktop/package.json:10` `main: dist/main.js` and the
  `files:` list in `desktop/electron-builder.yml:6`.

## State it owns
- `desktop-prefs.json` in Electron's `userData` dir — **the only file this app writes**
  (`desktop/src/main.ts:536` `prefsPath`, `:551` `savePrefs`; shape `DesktopPrefs` at
  `desktop/src/shared/prefs.ts:22`): `closeHintShown`, `tray` (`TrayPrefs`:
  title mode + panel sections), and `updateReminder` (the circuit-breaker state). Cached in the module-level
  `prefs` (`desktop/src/main.ts:74`) — an external edit is invisible until restart.
- In-memory, main process only: `trayState` (`desktop/src/main.ts:75`, rebuilt every 10 s by `refreshTray`),
  `lastBoot` (`:85`, the apiBase passed to each window), `lastPanelStateKey` (`:314`, dedupes broadcasts),
  `mismatchDialogShown` (`:73`), `quitting` / `closingForUpdate` (`:70`–`:72`), `lastMainWindow` /
  `releaseTimer` (where a released main window was, and the pending release).
- `ServiceManager.weStarted` + the `child` handle (`desktop/src/service.ts:70`, `:72`) — process ownership for
  this app run; cleared with the process.
- `UpdateController` in-memory state (`desktop/src/update.ts:98`): `track`, the single-flight `busy`/`held`
  lock, `pendingManifest`, `desktopUpdatePending`, `dmgDownloadedVersion`, `dmgAvailableVersion`, `timer`.
- `TrayController` (`lastKey`, `menu`, `title`, `detail` — `desktop/src/tray.ts:26`) and `TrayPanel`
  (`win`, `anchor`, `contentHeight`, `hiddenAt`, `blurHolds` — `desktop/src/trayPanel.ts:22`).
- Read-only, owned by others: `~/.astra/config.json`, `runtime.json`, `install.json` and `logs/` — paths in
  `desktop/src/shared/paths.ts:25` (a fourth mirror of the data-dir layout, alongside
  `AstraPaths.cs` and `cli/src/lib/paths.ts`) and parsers in `desktop/src/shared/config.ts:15` /
  `runtime.ts:20` / `install.ts:7`. The desktop never writes `install.json` — only `update-core` and the CLI do.

## Contracts it exposes
- **`app://astra` scheme**: registered as `standard/secure/supportFetchAPI/corsEnabled` before app ready
  (`desktop/src/main.ts:52`), which is what makes the renderer a real secure origin for CORS and fetch;
  handler registration at `desktop/src/main.ts:187`. Host must be `astra` (`desktop/src/appProtocol.ts:7`) —
  the server-side equivalent is not this scheme (the app serves itself; `http://127.0.0.1:<port>` is only
  the API origin in `connect-src`).
- **CSP**: one `content-security-policy` header on every file response (`desktop/src/appProtocol.ts:51`),
  built by `buildCsp(apiOrigin)` (`desktop/src/shared/csp.ts:8`). Deliberate allowlist — `default-src`/
  `script-src 'self'`, `style-src 'self' 'unsafe-inline'`, `img-src 'self' data: blob: https:`,
  `font-src 'self' data:`, `connect-src 'self' <apiOrigin>`, `object-src 'none'`, `base-uri 'self'`,
  `form-action 'none'`, `frame-ancestors 'none'`.
- **Additional-argument switches** read by the preload (`desktop/src/preload.ts:13`, values set at
  `desktop/src/main.ts:594`): `--astra-api-base`, `--astra-api-mismatch`, `--astra-version`,
  `--astra-accent`. They exist because the renderer needs `apiBase` synchronously at load, inside the
  Chromium sandbox.
- **IPC channels** (all declared in `registerIpc`, `desktop/src/main.ts:713`, mirrored in
  `desktop/src/preload.ts:35` and typed in `desktop/src/bridge.d.ts:10`):
  renderer→main `invoke`: `astra:open-path`, `astra:reveal-logs`, `astra:start-service`, `astra:restart-service`, `astra:update-check`,
  `astra:update-apply`, `astra:login-item-get`, `astra:login-item-set`, `astra:get-accent`, `astra:set-theme`, `astra:context-menu`, `astra:tray-prefs-get`,
  `astra:tray-prefs-set`, `astra:tray-state-get`, `astra:tray-command`; renderer→main `send`:
  `astra:tray-title`, `astra:tray-panel-resize`; main→renderer `send` (via `broadcast`,
  `desktop/src/main.ts:756`): `astra:menu-command`, `astra:accent-changed`, `astra:tray-state-changed`, `astra:login-item-changed`,
  `astra:tray-prefs-changed`, `astra:tray-panel-visibility` (`desktop/src/trayPanel.ts:99`/`:107`).
- **Hash routes the main process drives**: `#/tray` (`desktop/src/shared/chrome.ts:31`),
  `#/settings/tray` (`:29`), `navHash()` targets (`:14`); `MenuCommand` (`:19`) is the payload of
  `astra:menu-command`.
- **`--started-by desktop`** — `STARTED_BY` at `desktop/src/shared/types.ts:38`, passed at
  `desktop/src/service.ts:224`; this is how the server knows the desktop owns it.
- **`install.json` fields it reads**: `serverPath`, `serverVersion` (+ `desktopPath`, `desktopVersion`,
  `updatedAt` ignored by the desktop) — `desktop/src/shared/types.ts:14`, consumed at
  `desktop/src/shared/resolveServerBinary.ts:63`.
- **HTTP surface it consumes**: `/api/health`, `/api/version`, `/api/update/status`, `POST /api/update/check`,
  `/api/clients`, `/api/providers`, `PUT /api/clients/{kind}/binding`, `POST /api/admin/shutdown`
  (`desktop/src/api.ts:35`–`:128`).

## How to extend
- To add a **preload IPC call**: add the method to the `bridge` object (`desktop/src/preload.ts:35`), mirror it
  in the `Window.__ASTRA__` declaration (`desktop/src/bridge.d.ts:10`) **and** in the renderer's own
  `DesktopBridge` mirror (`web/src/shell/bridge.ts:53`), add the `ipcMain.handle` in
  `registerIpc` (`desktop/src/main.ts:659`), then use it from a component. Missing the handler makes the
  promise reject; missing either declaration is a `tsc` error on one side only.
- To add a **CSP directive**: change `buildCsp` (`desktop/src/shared/csp.ts:8`) and pin it in
  `desktop/tests/csp.test.ts` — the `img-src … https:` addition is the precedent and the test asserts plain
  `http:` stays out (`desktop/tests/csp.test.ts:15`). Add the narrowest directive; do not widen
  `default-src` or drop `object-src 'none'`.
- To add a **desktop preference**: add the field to `DesktopPrefs` (`desktop/src/shared/prefs.ts:22`),
  parse/default it in `parsePrefs`/`parseTrayPrefs` (`:75`/`:62`), read it through `loadPrefs()`
  (`desktop/src/main.ts:539`) and persist with `savePrefs()` (`:551`). Parsers are tolerant on purpose, so a
  forgotten default silently yields the old shape.
- To add a **tray menu item**: extend `TrayCallbacks` and `buildTrayMenuTemplate`
  (`desktop/src/shared/trayMenu.ts:29`/`:124`) plus a label in `trayLabels` (`:55`), then wire the callback in
  `trayCallbacks()` (`desktop/src/main.ts:353`). A state field that changes must also be added to
  `TrayState`, or `trayStateKey` will not notice it and the menu will not rebuild (`desktop/src/tray.ts:63`).
- To add a **tray-panel command**: add it to `TRAY_PANEL_COMMANDS` (`desktop/src/shared/trayPanel.ts:34`) and
  to the `switch` in `runTrayPanelCommand` (`desktop/src/main.ts:425`). A command present in the constant but
  missing from the switch validates, resolves and does nothing.
- To add a **navigation target**: extend `NAV_ORDER` (`desktop/src/shared/chrome.ts:7`) and the renderer's
  `NavTarget` mirror (`web/src/shell/bridge.ts:5`); the ⌘1–⌘6 accelerators are generated from the order
  (`desktop/src/main.ts:795`).
- To add an **update reminder text**: add the key to `reminderLabels` (`desktop/src/shared/updateReminder.ts:85`,
  zh/en inline) and use it in `UpdateController.maybeRemind`'s surfaces (`desktop/src/update.ts:457`/`:467`).

## Invariants and traps
- **The renderer must keep working without `window.__ASTRA__`** — it is declared optional
  (`desktop/src/bridge.d.ts:10`, comment: "absent in a normal browser (shell = web)") and detected at
  `web/src/shell/bridge.ts:97`; every shell-only feature needs a web fallback, since `@aidotnet/web` is also
  served by the server as a plain SPA.
- **`connect-src` carries exactly one origin**, the apiBase captured at protocol-registration time
  (`desktop/src/main.ts:187` closes over `currentApiOrigin`, set after the service is up at `:194`). The
  origin must therefore be known *before the first window loads*; a renderer-side fetch to any other origin
  (the docs market, GitHub) needs a CSP change first.
- **The server's port can drift** — it moves when `config.port` is taken, so the desktop follows
  `runtime.json` after spawning (`desktop/src/service.ts:283`) and `probe()` treats a
  `runtime.json` whose pid is dead as stale (`desktop/src/shared/runtime.ts:41`). Never assume
  `config.json`'s port — and after a spawn, trust only the entry the child wrote (`isRuntimeFrom`,
  `desktop/src/shared/runtime.ts:51`): the server writes it at ApplicationStarted, seconds after it
  listens, and until then the configured port can answer for another server still holding it.
- **Never kill a server you cannot vouch for** — `shouldReplaceRunningServer` requires both the running
  version and a target version, so env/dev targets and unreadable versions adopt instead
  (`desktop/src/shared/resolveServerBinary.ts:85` comment). A packaged app always serves its own bits
  because the bundled version is known (`desktop/src/main.ts:95`).
- **Only stop the server on exit if this run started it** — `stopIfOurs` (`desktop/src/service.ts:195`)
  guards `before-quit` (`desktop/src/main.ts:147`).
- **`applyFromUi` must not call the public `check()`** — it already holds the single-flight lock, so `check()`
  returns "already running" and leaves `pendingManifest` null, which used to make "Update now" report
  "up to date" (the 0.5.2 fix; comment at `desktop/src/update.ts:368`–`:374`, regression test
  `desktop/tests/updateController.test.ts:184`). Use `runCheck()` (`desktop/src/update.ts:227`).
- **The lock covers the whole apply** (`desktop/src/update.ts:364`–`:390`), which is what stops a 12 h
  background check from restarting the service mid-swap; `backgroundCheck` waits for the lock rather than
  queueing behind it (`desktop/src/update.ts:163`). The lock is released *before* reminding so the dialog's
  own "Update Now" button can apply (`desktop/src/update.ts:166`).
- **A reminder is recorded before it is shown** (`desktop/src/update.ts:447`, rationale at
  `desktop/src/shared/updateReminder.ts:20`), so a crash mid-dialog still consumes the budget.
- **Main→renderer pushes are deduped**: `pushTrayPanelState` compares a JSON key
  (`desktop/src/main.ts:314`), `TrayController.update` a `trayStateKey` (`desktop/src/tray.ts:63`). This
  keeps the 10 s poll from churning native menus; the cost is that state not present in either key never
  reaches the UI.
- **A poll finishing mid-transition must not clear the activity flag** — `refreshTray` carries
  `trayState.activity` forward (`desktop/src/main.ts:296`) and `runServiceAction` refuses to start a second
  transition (`:325`).
- **Closing the window is not quitting** — `window-all-closed` is a no-op (`desktop/src/main.ts:146`) and
  `close` hides unless `quitting`/`closingForUpdate` (`:630`); macOS leaves full screen first to avoid an
  empty Space (`:576`). Squirrel's `quitAndInstall` closes windows *before* `before-quit`, hence the
  `before-quit-for-update` flag (`desktop/src/main.ts:158`).
- **Tray-only releases renderers**: `IDLE_RENDERER_TTL_MS` (60 s) after the window is closed to the tray (or the
  panel hides), `releaseIdleRenderers` destroys the hidden main window — remembering bounds, maximized state and
  page (`restorableBounds` / `restorableHash`, `desktop/src/shared/chrome.ts`) for the next `createWindow` — and,
  with tray title `none`, the hidden panel too. `showMainWindow` / `openTraySettings` cancel a pending release.
- **Tray-title ownership is exclusive**: the panel window owns it while it exists, otherwise the main window
  may send it (also while hidden in the tray). Before a release destroys the main window, `ensureTitleSource`
  creates the panel unless the title mode is `none` (`desktop/src/main.ts`, `astra:tray-title` handler).
- **Renderer input is untrusted** — `astra:context-menu` and `astra:tray-title` / `astra:tray-command`
  validate before touching native APIs (`desktop/src/shared/chrome.ts:100`,
  `desktop/src/shared/trayPanel.ts:52`/`:47`).
- **Login items need a packaged, stable exe** — `loginItemSupported()` requires `app.isPackaged`
  (`desktop/src/main.ts:484`), and the Windows *query* must pass the same `--hidden` args as the
  registration or it never matches (`desktop/src/main.ts:489`). Linux has no Electron login-item API: an XDG
  autostart entry (`desktop/src/shared/loginItem.ts`) starts the AppImage file (`$APPIMAGE`, not its temporary
  mount) and is rewritten at launch when the app moved (`refreshLinuxLoginItem`, `desktop/src/main.ts:517`).
  The tray menu and Settings share `changeOpenAtLogin` (`:529`), which broadcasts `astra:login-item-changed`.
  macOS 13+ may leave the item at `requires-approval`, which the dialog at `desktop/src/main.ts:556` handles.
- **Platform tray differences are load-bearing**: Linux AppIndicator may never deliver click events, so the
  menu carries "Show Tray Panel" (`desktop/src/shared/trayMenu.ts:143`) and macOS must not call
  `setContextMenu` (it would turn every click into the menu — `desktop/src/tray.ts:48`). A native menu opened
  from the panel steals focus, so the panel is kept open with `holdOpen`/`blurHolds`
  (`desktop/src/main.ts:717`, `desktop/src/trayPanel.ts:125`), and a click right after a blur-hide is swallowed
  by `REOPEN_GUARD_MS` (`desktop/src/trayPanel.ts:6`).
- **`app.getLocale()` is only valid after ready** — hence the locale getters in the controller options
  (`desktop/src/update.ts:59`).
- **Never edit `desktop/renderer/`, `desktop/dist/` or `desktop/server-bundle/`** — all build outputs.
- **The desktop's binary resolution is not the CLI's** — install.json can lose to the bundled server here
  (`desktop/src/shared/resolveServerBinary.ts:57`) and there is no platform-package fallback; see the
  asymmetry note in `AGENTS.md`.
- **Dev-only flags**: `ASTRA_RENDERER_URL` switches every window to the Vite dev server and opens detached
  DevTools (`desktop/src/main.ts:59`, `:640`); `copy-web.mjs --optional` exists only for that mode
  (`desktop/scripts/copy-web.mjs:11`).

## Tests
`desktop/tests/**/*.test.ts` (vitest, `environment: 'node'` — `desktop/vitest.config.ts:5`, `include`
`tests/**/*.test.ts`). They cover the pure helpers only: `csp`, `chrome`, `config`, `install`, `paths`, `pid`,
`port`, `rendererPath`, `requestHeaders`, `resolveServerBinary`, `runtime`, `tray` (menu model + panel math +
prefs), `updateController`, `updateReminder`, `updateTrack`, `version`. `main.ts`, `service.ts`, `tray.ts` and
`trayPanel.ts` have **no** direct test file — Electron-touching code is exercised by mocking the module
(`vi.mock('electron', …)` / `vi.mock('electron-updater', …)` at `desktop/tests/updateController.test.ts:8`).
Run all: `pnpm --filter @aidotnet/desktop test`; one file:
`pnpm --filter @aidotnet/desktop exec vitest run tests/csp.test.ts`.

## Related flows
- [server-startup](../flows/server-startup.md) — the desktop is one of the two launchers: it resolves the
  binary, spawns it, follows `runtime.json`, health-checks and shuts it down.
- [release-and-update](../flows/release-and-update.md) — `UpdateController` is the desktop end of the
  manifest feed: background check, reminder, `applyFromUi`, and the server swap through `update-core`.
- [admin-api-request](../flows/admin-api-request.md) — the main process makes authenticated admin calls with
  `adminHeaders()` (`/api/update/check`, `/api/admin/shutdown`, `PUT /api/clients/{kind}/binding`).
