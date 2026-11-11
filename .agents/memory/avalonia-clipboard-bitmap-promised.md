---
name: avalonia-clipboard-bitmap-promised
description: Avalonia 12's SetBitmapAsync only promises the image on macOS (encoded at paste time) — a disposed bitmap or an ended process leaves an empty clipboard; the desktop writes images through MacPasteboard instead
type: project
updated: 2026-11-11
source: session
evidence: ["src/Astra.Desktop/Platform/MacPasteboard.cs", "src/Astra.Desktop/Components/Overview/DailyBillSheet.cs:WindowBillImageTarget.CopyAsync", "tests/Astra.Desktop.UiTests/DailyBillSheetTests.cs:A_copied_bill_is_on_the_mac_pasteboard_at_once_as_png_and_tiff", "Avalonia native/Avalonia.Native/src/OSX/clipboard.mm (NSPasteboardWritingPromised for every type but plain text)"]
---
On 2026-11-11 the daily bill's 「复制图片」 toasted success but pasting gave nothing (desktop, macOS). Avalonia.Native writes every
clipboard type except plain text as `NSPasteboardWritingPromised`: the PNG is encoded from the `Bitmap` only when another app
pastes (`DataTransferItemToAvnClipboardDataItemWrapper.GetValue` → `Bitmap.Save`). The sheet disposed its `RenderTargetBitmap`
right after `SetBitmapAsync` returned, so the paste found a dead bitmap. Avalonia's `DataTransfer`/`DataTransferItem.Dispose`
dispose nothing, so a bitmap handed to the clipboard must simply not be disposed by the caller. Even a live bitmap is lost
when the process ends — and the desktop ends itself 15 s after its window closes ([[desktop-tray-relaunch]]). Windows
(`FlushAsync` renders the formats at once) and X11 (the selection owner serves pastes) are lazy too.

**Why:** a copy that only works while the app keeps running and the bitmap stays alive looks fine in a quick test and fails
for a user who closes the window and pastes in a chat app later.

**How to apply:** to copy an image on the desktop, encode it to PNG first; on macOS call `MacPasteboard.SetImage` (writes
`public.png` + `public.tiff` now), elsewhere `SetBitmapAsync(new Bitmap(pngStream))` without disposing it, then `FlushAsync()`.
Text copies (`SetTextAsync`) are not affected: plain strings are written eagerly.
