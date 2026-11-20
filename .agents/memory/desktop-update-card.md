---
name: desktop-update-card
description: 2026-11-17 user request — the Avalonia app's update reminder, check result, progress and outcome are one bottom-right card (main window's overlay, else a corner window), not centered message boxes; labels stay zh/en pairs in UpdateDialogLabels
type: project
updated: 2026-11-17
source: session
evidence: ["src/Astra.Desktop/Platform/Tray/UpdateNotice.cs", "src/Astra.Desktop/Components/Update/UpdateCardView.cs", "src/Astra.Desktop.Core/Tray/UpdateCard.cs", "src/Astra.Desktop.Core/Update/UpdateProgress.cs", "tests/Astra.Desktop.UiTests/UpdateNoticeTests.cs"]
---
On 2026-11-17 the user asked (「优化一下更新的弹窗，在右下角窗口增加更新进度弹窗和提醒的能力」) for update progress and reminders in the
bottom-right corner. Built: `UpdateCoordinator` reports every apply step (`UpdateCoordinatorOptions.Progress`: Preparing → Downloading with bytes →
Verifying / Installing / Restarting → Done | Relaunching | Failed; download ticks thinned to one per percent or 150 ms) whoever started the apply;
`AvaloniaDesktopHost.UpdateProgressChanged` carries it to `TrayRuntime`, which shows `UpdateNotice`. The reminder, the tray's "Check for Updates…"
result and the old "updated" / "update failed" boxes are now that card; `MessageBox` is left for the API-mismatch warning and tray errors. The
card sits in the main window's overlay layer (ZIndex `ToastPresenter.AboveModals`, [[avalonia-overlay-modal-zindex]]) while the window is visible,
otherwise in `UpdateCardWindow` (topmost, never activated, bottom right on every OS — unlike `NoticeWindow`, which is top right on macOS), and moves
when the window is shown / hidden. A tray-only app now gets the actionable card instead of the OS notification for the reminder; an unanswered offer
in the corner window closes after 60 s. Settings › About drops its own success / error toast when the host shows the card
(`IDesktopHost.ShowsUpdateProgress`, `UpdateLogic.ApplyShownByHost`). The stock Avalonia `ProgressBar` draws nothing under Arc's theme here, so the
card draws its own track + fill.

**Why:** the reminder used to be a centered modal box and an apply showed nothing until it ended (a 40–90 MB download looked like a hang).
**How to apply:** new update texts go into `UpdateDialogLabels` (zh/en pairs, outside the web catalog, like the old dialogs). Anything that applies an
update goes through `IDesktopHost.ApplyUpdateAsync` so the card follows it; do not add another progress surface. Only seen in headless tests and
headless renders so far — not yet in a running app on any OS.
