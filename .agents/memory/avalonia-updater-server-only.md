---
name: avalonia-updater-server-only
description: the Avalonia app's in-app update used to replace only the server on every track, so dmg/installer apps fell behind their server; fixed 2026-10-30 by the track rule + app self-update (option B), still unverified in a real release
type: project
updated: 2026-10-30
source: session
evidence: src/Astra.Desktop.Core/Update/UpdateCoordinator.cs, src/Astra.Desktop.Core/Update/DesktopReleaseFeed.cs, src/Astra.Desktop.Core/Update/MacAppUpdater.cs, src/Astra.Desktop/Platform/SelfUpdate.cs, desktop/src/shared/updateTrack.ts
---
Seen 2026-10-30 on the maintainer's Mac: `/Applications/Astra.app` (dmg) at 0.13.0-preview.1 ran
`~/.astra/server/astra-server-0.13.3`, with 0.13.1 and 0.13.2 beside it — three in-app updates had
replaced the server and never the app. Causes: `IDesktopUpdater` had no implementation, the port of
`update.ts` dropped the Electron track rule (dmg / installer apps never update the server alone), and the
check compared only the server's version.

Decision the same day: the user chose "B 完整自动更新" over a download-page-only fix, letting app and server
version apart (API-compat negotiation), and an npm-package swap for every platform. Built: `UpdateTrack` +
`UpdateTracks.Resolve`; `DesktopReleaseFeed` (reads `releases.json` by the upload step's platform/kind tags);
`MacAppUpdater` (zip → ditto → bundle id + same signing team → `/bin/sh` swap after exit, rollback on a failed
rename), `WindowsInstallerUpdater` (PowerShell waits for the pid, then `setup.exe /S --updated --force-run`),
`AppImageUpdater` (rename over `$APPIMAGE`, relaunch after exit); a read-only copy gets the download page;
Settings flags an app/server version mismatch. The npm track still updates only the server and tells the user
to run `astra update --desktop`.

**Why:** a release ships app and server at one version (`sync-versions.mjs`); only a one-sided update makes
them differ, and a packaged app must update as a whole.
**How to apply:** no release has exercised the self-update yet. The first one that can (an installed app with
this code updating to a newer release) should be a preview, checked on each OS: the swap log is
`~/.astra/logs/desktop-update.log`. When "desktop and server versions differ" comes back, check the update
track in that log first, then the server lifecycle ([[orphaned-server-after-failed-update]]). Never let a
packaged app swap the server alone, and never downgrade a managed server to the bundled one (a newer
server may already have migrated the database).
