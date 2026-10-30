---
name: avalonia-dispatchertimer-autostart
description: Avalonia's DispatcherTimer(interval, priority, callback) constructor starts the timer; a timer meant to start later must use the priority-only constructor
type: project
updated: 2026-10-30
source: session
evidence: Avalonia.Base 12.1.4 DispatcherTimer ctor ("This constructor immediately starts the timer"); src/Astra.Desktop/Platform/Tray/TrayRuntime.cs:_relaunch
---
`new DispatcherTimer(interval, priority, callback)` (and the overload with a `Dispatcher`) calls `Start()` itself, as WPF's does. On
2026-10-30 the tray-restart timer was first built that way: it was only stopped and restarted around a window close, so every
tray-only (`--hidden`) process, which never closes a window, restarted itself 15 s after starting, in a loop; a dev-build run caught
it before it shipped. `_probe`, `_updates` and `_relief` in `TrayRuntime` are built the same way and so run from construction;
that is harmless for them (`RelieveMemory` skips while anything is on screen) but not a pattern to copy.

**Why:** the overload reads like a configuration call; nothing at the call site says the timer is running.
**How to apply:** for a timer that starts on an event, use `new DispatcherTimer(priority) { Interval = … }` plus `Tick +=`, or
`DispatcherTimer.RunOnce`. Related: [[desktop-tray-relaunch]].
