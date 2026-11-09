---
name: ci-ui-test-cleanup-flake
description: CI's UiTests fail one random scene with "Test Case Cleanup Failure ... calling thread cannot access" while it passes locally; an Avalonia headless session race, seen before and after Arc 1.0
type: project
updated: 2026-11-09
source: session
evidence: CI runs 41818894257 (0.14.2, traces__request-inspector dark) and 42573656498 (0.14.3100, clients__backups light); both 561/562, stack at Avalonia.Headless HeadlessUnitTestSession.EnsureIsolatedApplication -> DefaultRenderLoop.Add -> Dispatcher.VerifyAccess
---

On ubuntu CI, `dotnet test` can fail exactly one `SceneCaptureTests.Scene_renders` case after 1 ms with
`[Test Case Cleanup Failure] InvalidOperationException: The calling thread cannot access this object`. The scene differs from
run to run, and the full suite passed 562/562 on macOS with the same code. Likely cause, from Avalonia 12.1.4's source and not
reproduced: PerTest isolation resets the dispatcher between tests and `Dispatcher`'s constructor makes the first new one the UI
thread (`s_uiThread ??= this`), so a stray timer or task from an earlier test can take it. The candidate fix is
`AvaloniaTestIsolationLevel.PerAssembly` in `tests/Astra.Desktop.UiTests/TestApp.cs`; on 2026-11-09 it passed 562/562 on macOS
with all 488 scenes within thresholds of the PerTest run, but CI has not confirmed it.

**Why:** it makes `main`'s CI red without a real regression. It does not gate `release.yml`: 0.14.3100 published with this CI
red, while 0.14.2's Release failure was a separate problem (NU1903 on the rc.2 ILCompiler).

**How to apply:** when CI shows one such failure, check the message before chasing the scene. A rendering failure in a named
scene is not this flake.
