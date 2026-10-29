---
name: windows-hidden-title-bar-caption-hint
description: Hiding AtomUI's title bar on Windows leaves Avalonia's caption hit-test strip (hint -1 → ~31 DIP of HTCAPTION) over the content; the hint is set to the frame thickness and the shell draws its own caption buttons with decoration roles
type: project
updated: 2026-10-29
source: session
evidence: ["src/Astra.Desktop/Platform/WindowChrome.cs:ConfigureWindowsChrome", "src/Astra.Desktop/Platform/WindowChrome.cs:WithOverlay", "src/Astra.Desktop/Platform/CaptionButtons.cs", "Avalonia.Win32 12.1.4 WindowImpl.HitTestNCA / UpdateExtendMargins and AtomUI 6.2.8 Window.CalculateEffectiveContentFrameMargin (decompiled)"]
---
AtomUI's Windows window is Avalonia's extended client area with AtomUI's own title bar hosted in the
drawn-decorations layer. `IsTitleBarVisible = false` hides that bar and AtomUI hands the content the whole
client area, but Avalonia's Win32 hit test still answers HTCAPTION for the top
`ExtendClientAreaTitleBarHeightHint` DIPs, and the hint stays -1 (the default caption height, ~31 DIP)
because AtomUI only sets it from the title bar's measured size. Everything in that strip without a
`WindowDecorationsElementRole` would drag the window instead of taking the click.

**Why:** the Electron shell's Windows chrome is `titleBarStyle: hidden` + `titleBarOverlay` (52 px); the
Avalonia port has to reproduce it without AtomUI's buttons, which are hidden with its title bar.
**How to apply:** `WindowChrome.ConfigureWindowsChrome` sets the hint to `WindowsFrameBorder` (8 DIP, the
resize frame), so the band is the top resize border and no caption strip remains; the content root is marked
`WindowDecorationsElementRole.User` as a fallback. `CaptionButtons` (minimize / maximize / close, roles
MinimizeButton / MaximizeButton / CloseButton → HTMINBUTTON / HTMAXBUTTON / HTCLOSE, Snap Layouts on
maximize) is overlaid by `WindowChrome.WithOverlay`; drag regions stay `AttachDragRegion`. Derived from the
decompiled sources, not yet verified on a Windows machine (2026-10-28): check top-edge resize, clicks in
the top 31 DIP, the maximized state and Snap Layouts first when something looks off there. Linux keeps the
system title bar on purpose (Electron parity).

**Arc 0.3.2 (2026-10-29):** AtomUI is gone, so the window is Avalonia's own `Window` (`ExtendClientAreaToDecorationsHint` +
`WindowDecorations.Full` set in `WindowChrome`, no `IsTitleBarVisible`). The analysis above was written against AtomUI's
template; whether Arc's `WindowBaseTheme` draws its own caption buttons or title strip on Windows, and whether the macOS
traffic lights still sit at `TrafficLightX` (the AtomUI offset API is gone), is unverified — look at both first.
