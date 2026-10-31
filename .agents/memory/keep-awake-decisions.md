---
name: keep-awake-decisions
description: Issue #14's "prevent sleep while requests are in flight": opt-in, idle sleep only, server host only, blittable DllImport, the Linux sleep lock's side effect, and the shutdown order it relies on
type: project
updated: 2026-10-30
source: session
evidence: ["src/Astra.Server/Power/KeepAwakeService.cs:KeepAwakeService", "src/Astra.Server/Power/KeepAwakeMiddleware.cs:KeepAwakeMiddleware.IsGatewayWork", "src/Astra.Server/Power/SystemdSleepInhibitor.cs:SystemdSleepInhibitor.Arguments", "src/Astra.Server/Hosting/AstraApp.cs:AstraApp.Create", "tests/Astra.Server.IntegrationTests/KeepAwakeTests.cs"]
---
On 2026-10-30 GitHub issue #14 got `AppSettings.PreventSleep` (off by default): while gateway POSTs under `/v1` or
`/v1beta` are in flight (a stream until it ends), the server holds the OS's idle-sleep inhibitor, and lets go 30 s after
the last one, at once when the setting goes off, and on shutdown. The decisions, as promised on the issue:

- **Idle sleep only**: macOS `PreventUserIdleSystemSleep` (what `caffeinate -i` takes), Windows
  `PowerRequestSystemRequired` (handle-based, not the thread-bound `SetThreadExecutionState`). Never the display, never
  the lid.
- **Linux takes `--what=idle:sleep --mode=block`** through `systemd-inhibit … cat`. The `sleep` lock also makes logind
  refuse (or ask polkit about) a suspend an unprivileged user asks for while it is held; the lid is unaffected only
  because logind's default is `LidSwitchIgnoreInhibited=yes`. Not verified on a Linux desktop yet.
- **Server host only**: an ASP.NET middleware registered in `AstraApp.Create`, not a pipeline feature, so
  `PipelineContracts.cs` and the relay are untouched.
- **Blittable `DllImport`, not `LibraryImport`**: the generator needs `AllowUnsafeBlocks`, which no project sets; the
  desktop's MacInterop / MemoryRelief and the server's WindowsConsole set the precedent.
- **Shutdown order (observed with a real host, SIGTERM mid-stream)**: WebApplication registers the web server's hosted
  service last, so it stops first; the stream drained for ~10 s still holding the assertion, and `StopAsync` let go
  after. SIGKILL: powerd dropped the assertion at once.

**Why:** the reporter's streams broke when the machine idled into sleep; a broader lock (display, lid) would surprise
users who only wanted requests to finish.

**How to apply:** keep new inhibitor kinds idle-only; if Linux users report blocked manual suspends, `--what=idle` alone
is the narrower choice (whether GNOME's auto-suspend honours it is the open question). The real-assertion test
(`[MacOSFact]`) checks `pmset -g assertions`; Windows has no automated check.
