---
name: avalonia-window-units
description: Avalonia screen coordinates are points on macOS (scaling 1) but a window renders at 2, so window bounds must be saved and restored in the screen's units
type: project
updated: 2026-10-26
source: session
evidence: ["src/Astra.Desktop/Platform/WindowBounds.cs:ToScreenUnits", "src/Astra.Desktop/Platform/WindowChrome.cs:Save", "tests/Astra.Desktop.UiTests/WindowBoundsTests.cs"]
---
On a Retina Mac `Screen.WorkingArea` / `Window.Position` are in points and `Screen.Scaling` is 1, while
`Window.RenderScaling` is 2. The desktop client used to save `ClientSize * RenderScaling`, which the next
launch read as a 2560x1640 window on a 1470x847 display, so it opened full screen (the "resized window after
the first launch" report; the saved `desktop-prefs.json` showed double the 1280x820 default).

**Why:** units of `Position`, work areas and the saved size must be the same space; convert with the screen's
scaling (`WindowBounds.ToScreenUnits` / `FromScreenUnits`), never with the window's render scaling.
**How to apply:** touch window-bounds code only through those helpers. A saved window larger than its display
is dropped (`FitsDisplay`), which also heals files written by the old build. The `sidebarCollapsed: true` that
two earlier agents saw in fresh prefs was never reproduced on real launches (three scratch-home runs, flag
stayed false); the only writers are the toggle command and the old visual harness that set a shared prefs
object, so scenes now use `DesktopPrefs.InMemory()`.
