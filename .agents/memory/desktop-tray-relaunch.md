---
name: desktop-tray-relaunch
description: why the Avalonia app restarts itself into the tray 15 s after its window was closed (GPU memory a shown window leaves behind), the measurements, and the hand-over rules
type: project
updated: 2026-10-30
source: session
evidence: src/Astra.Desktop/Platform/Tray/TrayRelaunch.cs, src/Astra.Desktop/Platform/Tray/TrayRuntime.cs:TryRelaunch, src/Astra.Desktop/Platform/MemoryRelief.cs, src/Astra.Desktop.Core/ServiceManager.cs:InheritedServerPid
---
Measured 2026-10-28 on the AOT build (macOS): once the main window has rendered, ~110–170 MB of GPU / IOSurface memory stays
with the process. Hiding, really closing the window (platform impl gone, zero windows), a compacting GC and capping Skia's GPU
cache (`SkiaOptions.MaxGpuResourceSizeBytes` = 8 MB) all left it in place. A start that never showed a window (`--hidden`) sits at
~105 MB; a window closed to the tray at 249–286 MB even after `MemoryRelief` (GC freed ~20 MB, `malloc_zone_pressure_relief`
nothing measurable). The user chose (2026-10-28) to restart into the tray: once the window has been closed a while, the app starts
`--hidden --after=<pid> --owned-server=<pid> --route=<page>` and exits; the server keeps its pid and the successor's Quit stops it.

2026-10-30 (0.13.0 app, Avalonia 12.1.4 on Metal, `footprint`): the user saw 250 MB in the tray during the then 2-minute wait and
asked for memory to drop as soon as possible after a close, since the window is rarely open. Window open 145–156 MB; closed + relief
139 MB on a light session (managed heap back to ~22 MB, but Metal resources ~31 MB + IOSurface ~13 MB + malloc ~29 MB stay);
after the restart 70–82 MB. A tray-only process shows its window within ~0.1 s, so the delay went to 15 s (`RelaunchAfter`), on a
repeating `_relaunch` timer that runs from the close until the window is shown again. Opening the tray panel costs ~25 MB (~10 MB of
it managed, given back by the relief that now follows a panel close); the panel alone never triggers a restart.

The tray-only process itself (2026-10-30, AOT in an Astra.app layout): 55–61 MB → 50–51 MB once it stopped building the main window,
its app menu and a 1024 px Dock icon render it never showed (`App.CreateWindow`, `TrayRuntime.MainWindow`; inside the bundle
`MacApp.SetDockIcon` discards the render, so it is now a callback). What is left is mostly floor: ~33 MB is the AOT image's `hydrated`
section (Native AOT unpacks its type data at start; it grows with what the binary contains, not with what runs), ~8.5 MB is ColorSync
tables Core Animation builds on the first layer commit with a colour (the status item alone triggers it), ~5 MB Skia, ~6 MB GC heap.
`DOTNET_gcConcurrent=0`, `DOTNET_GCgen0size` and `DOTNET_GCConserveMemory` changed nothing measurable. Going lower means a smaller
binary for the tray or a separate tray process without the UI.

**Why:** the GPU memory is not reachable from the app; only a process that never rendered a window avoids it.
**How to apply:** keep the guards — no restart while a page holds unsaved edits (`PageViewModel.HoldsUnsavedWork`; ModelDetail,
SmartRouting and the provider settings tab report them, open sheets do not), an update runs, or any window is visible
(`IClassicDesktopStyleApplicationLifetime.Windows`: the main window, the panel, a tray message box or notice); the exiting run must
not stop the server it hands over (`AppServices.HandingOver`), and the successor only owns it when the pid matches.
Testing with a throwaway `ASTRA_HOME`: give it its own port in `config.json`, or the app adopts the developer's real server on 17321.
A dev build (`bin/Debug/.../astra-desktop`) needs `ASTRA_SERVER_BIN`; System Events does not see it, press its close button through
`AXUIElementCreateApplication(pid)`. Related: [[desktop-page-lifetime]], [[avalonia-dispatchertimer-autostart]].
