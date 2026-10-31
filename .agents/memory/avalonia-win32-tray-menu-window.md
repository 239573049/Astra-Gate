---
name: avalonia-win32-tray-menu-window
description: Avalonia draws the Windows tray right-click menu as its own managed window (TrayIconImpl.TrayPopupRoot) — without a theme for Avalonia's menu controls it is 0 x 0 and the right click shows nothing (issue #18); headless tests can build that private window
type: project
updated: 2026-10-30
source: session
evidence: ["Avalonia 12.1.4 src/Windows/Avalonia.Win32/TrayIconImpl.cs:OnRightClicked", "src/Astra.Desktop/Theme/KitStyles.axaml (menu ControlThemes, 8fe5ee4)", "src/Astra.Desktop/Platform/Tray/TrayIcons.cs:TrayMenuWatch", "tests/Astra.Desktop.UiTests/TrayMenuWindowTests.cs", "https://github.com/239573049/Astra-Gate/issues/18"]
---
On Windows, Avalonia's `TrayIcon` menu is not a Win32 menu: a right click (`WM_RBUTTONUP`) makes `TrayIconImpl.OnRightClicked`
show a `TrayPopupRoot` (a `Window`, SizeToContent) whose content is a `MenuFlyoutPresenter` over the `NativeMenu` items
(`NativeMenuItemPresenter` = `MenuItem`, `Separator`). Arc themes none of Avalonia's menu controls, so up to 0.13.1 the window
measured 0 x 0: right click did nothing and the "more" menu in the panel drew nothing. 0.13.2 (8fe5ee4) added the themes to
KitStyles. Issue #18 was filed 2026-10-30 02:20 UTC, 13 h after 0.13.2 shipped; whether the reporter ran 0.13.2 was not known
when this was written, and nobody had checked the fixed menu on a real Windows machine.

**Why:** the window only has a size if those controls lay out, and a Linux/macOS run never shows it (macOS uses
`MacStatusItem`; Linux exports the menu over D-Bus).
**How to apply:** after touching Arc, the KitStyles menu themes or the Avalonia pin, run `TrayMenuWindowTests`. It builds
Avalonia.Win32's private `TrayPopupRoot` / `TrayIconMenuFlyoutPresenter` by reflection under the headless platform, as the right click
does, so it runs on any OS. At run time `TrayMenuWatch` closes an empty menu window and opens the tray panel (Open, Quit) in its place.
