---
name: desktop-tray-relaunch
description: why the Avalonia app restarts itself into the tray after its window was closed for 2 minutes (GPU memory a shown window leaves behind), and the hand-over rules
type: project
updated: 2026-10-28
source: session
evidence: src/Astra.Desktop/Platform/Tray/TrayRelaunch.cs, src/Astra.Desktop/Platform/Tray/TrayRuntime.cs:TryRelaunch, src/Astra.Desktop/Platform/MemoryRelief.cs, src/Astra.Desktop.Core/ServiceManager.cs:InheritedServerPid
---
Measured 2026-10-28 on the AOT build (macOS): once the main window has rendered, ~110–170 MB of GPU / IOSurface memory stays
with the process. Hiding, really closing the window (platform impl gone, zero windows), a compacting GC and capping Skia's GPU
cache (`SkiaOptions.MaxGpuResourceSizeBytes` = 8 MB) all left it in place. A start that never showed a window (`--hidden`) sits at
~105 MB; a window closed to the tray at 249–286 MB even after `MemoryRelief` (GC freed ~20 MB, `malloc_zone_pressure_relief`
nothing measurable). The user chose (2026-10-28) to restart into the tray: after 2 minutes closed, the app starts
`--hidden --after=<pid> --owned-server=<pid> --route=<page>` and exits; end-to-end the tray went 259 → 105 MB, the server kept its pid,
and the successor's Quit stopped it.

**Why:** the GPU memory is not reachable from the app; only a process that never rendered a window avoids it.
**How to apply:** keep the guards — no restart while a page holds unsaved edits (`PageViewModel.HoldsUnsavedWork`; ModelDetail,
SmartRouting and the provider settings tab report them, open sheets do not), an update runs, or the window / panel is on screen; the
exiting run must not stop the server it hands over (`AppServices.HandingOver`), and the successor only owns it when the pid matches.
Testing with a throwaway `ASTRA_HOME`: give it its own port in `config.json`, or the app adopts the developer's real server on 17321.
Related: [[desktop-page-lifetime]].
