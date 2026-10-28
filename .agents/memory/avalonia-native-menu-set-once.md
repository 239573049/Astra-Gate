---
name: avalonia-native-menu-set-once
description: On macOS a window's NativeMenu can be exported only once — a second NativeMenu.SetMenu with another instance throws "The menu being updated does not match" and kills the app; relabel the first menu in place
type: project
updated: 2026-10-28
source: session
evidence: ["src/Astra.Desktop/Platform/AppMenu.cs:Install", "src/Astra.Desktop/Platform/AppMenu.cs:Relabel", "tests/Astra.Desktop.UiTests/AppMenuTests.cs", "Avalonia.Native 12.1.4 AvaloniaNativeMenuExporter.SetMenu / __MicroComIAvnMenuProxy.Update (decompiled)"]
---
Avalonia.Native's menu exporter creates its native NSMenu proxy on the first `NativeMenu.SetMenu(window, …)`
and binds it to that `NativeMenu` instance; every later `SetMenu` calls `Update(menu)` on the same proxy,
which throws `ArgumentException("The menu being updated does not match.", "menu")` for any other instance.
The desktop client rebuilt the whole menu on `Loc.LanguageChanged`, so switching the language in Settings
on an installed build showed the fatal error box (reported 2026-10-28, 0.13.0-preview.1).

**Why:** the exporter observes item properties (Header, Gesture, IsChecked, …) and the item collections,
not the menu property itself; a new instance is a replacement it cannot do.
**How to apply:** build a window or application menu once and mutate it — `AppMenu.Relabel` copies the
headers of a freshly built menu onto the exported one; the app-menu Settings/About items already set
`Header` in place. The same applies to tray-icon and dock menus on macOS (the app's tray uses its own
NSStatusItem, so only the window menu was affected).
