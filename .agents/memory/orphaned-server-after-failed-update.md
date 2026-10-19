---
name: orphaned-server-after-failed-update
description: A failed 0.9.3 update left a server on the configured port with no runtime.json entry, and the desktop bound its UI to it
type: project
updated: 2026-10-15
source: session
evidence: ["desktop/src/service.ts:ServiceManager.startServer", "desktop/src/service.ts:ServiceManager.stopService", "desktop/src/shared/listener.ts:findServerListenerPid", "packages/update-core/src/apply.ts:probeRestartedVersion", "src/Astra.Server/Hosting/AstraApp.cs (runtime.Write on ApplicationStarted)"]
---
On 2026-10-14 a desktop server update 0.9.2 → 0.9.3 failed with "The operation was aborted due to
timeout". The new server was healthy but slow to start, and the desktop's `probeVersion` threw on a
2 s `/api/version` timeout. The rollback's `isRunning()` health probe then missed the busy 0.9.3
server, so the rollback never stopped it. It started 0.9.2, which moved to 17322 and overwrote
runtime.json. 0.9.3 kept running on 17321 with no record anywhere: no pid or token on disk.
On 2026-10-15 desktop 0.9.4 tried to replace it, but the stop failed (no token, no pid) and it
started its own server anyway. That server moved to 17322, and the startup poll accepted the old
server's health reply on 17321, because runtime.json is only written at ApplicationStarted, seconds
after Kestrel listens. Result: the UI showed server 0.9.3 with "update available", while the updater
(which reads runtime.json) saw 0.9.4 and answered "already up to date".

**Why:** Two servers on one `~/.astra` split the UI, the update checks and the client traffic across
different processes. Every symptom looks like an update bug, but the cause is the server lifecycle.
**How to apply:** After a spawn, trust only the runtime.json entry the child wrote (`isRuntimeFrom`),
never the configured port. When a mismatching server cannot be stopped, adopt it rather than start a
second one. A server without a record is found by its port (`findServerListenerPid`). When "update
says up to date but the UI shows an old server" is reported, check
`lsof -nP -iTCP -sTCP:LISTEN | grep astra` for two servers first.

A second cause of the same symptom (2026-10-19, one server, versions matching): on the dmg track the
Settings download button answered "up to date" while electron-updater was still fetching the new app
(`~/Library/Caches/@aidotnetdesktop-updater/pending/temp-*.zip` still growing). Fixed with the
`downloading` outcome of `UpdateController.applyFromUi`; if the symptom comes back with one server,
look at that cache directory before suspecting the lifecycle.
