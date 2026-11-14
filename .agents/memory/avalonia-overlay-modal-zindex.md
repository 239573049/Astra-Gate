---
name: avalonia-overlay-modal-zindex
description: Arc's sheets and dialogs take a rising ZIndex on the window's overlay layer; anything else added there draws under an open sheet unless it sets a higher ZIndex
type: project
updated: 2026-11-14
source: session
evidence: TKCode.Arc.Core 1.0.0 ModalPresence (HostCore.ZIndex = ++s_zIndex); TKCode.Arc 1.0.0 ToastHost.CreateStack; src/Astra.Desktop/Components/Kit/ToastPresenter.cs:AboveModals; tests/Astra.Desktop.UiTests/ToastLayerTests.cs
---
Arc puts every opened modal (Sheet, Dialog, Drawer, BottomSheet) on the window's overlay layer and gives its host
`ZIndex = ++counter` (a static counter, 1, 2, 3, … per open). Arc's auto-created `ToastStack` and our own `ErrorToasts` column
are plain children of the same layer at ZIndex 0, so a toast shown while a sheet was open drew under the sheet's scrim (issue #41,
「备份成功」 in the provider backup sheet). Fixed 2026-11-14 by giving both `ToastPresenter.AboveModals` (`int.MaxValue`).

**Why:** the order is not visible in our code: it comes from Arc's presence, and insertion order alone looks right until a sheet opens
after the toast stack was created (which is always, since `ArcFeedback` attaches the stack when the window opens).

**How to apply:** anything new we add to the overlay layer that must show over sheets (a banner, a floating panel) sets a ZIndex
above Arc's counter — reuse `ToastPresenter.AboveModals` — and gets a headless test like `ToastLayerTests` that opens a sheet first.
