---
name: mac-aot-needs-xcode-26
description: Native AOT for the macOS release rows fails to link on Xcode 16, which is why release.yml pins macos-15 and selects Xcode 26
type: project
updated: 2026-10-14
source: init
evidence: ["82fa73b", ".github/workflows/release.yml"]
---
The macOS rows of `.github/workflows/release.yml` run on `macos-15` and, before `dotnet publish`, select
the newest installed Xcode 2x (`xcode-select -s` on `/Applications/Xcode_2*.app`). `macos-15` itself
defaults to Xcode 16.4.

**Why:** The 0.3.0 release (2026-10-09, fixed by `82fa73b`) failed both mac rows at the Native AOT link
with undefined `__swift_FORCE_LOAD_$_swift_*` symbols from `pal_swiftbindings.o` in the .NET 10 AOT
runtime pack, which is compiled with an Xcode 26 swiftc and auto-links shims only the Xcode 26 SDK
ships. Whether the link passed depended on which agent the job landed on. The `macos-14` image tops out
at Xcode 16.2 and cannot run Xcode 26 at all.
**How to apply:** Keep both lines when editing the release matrix. Moving the mac rows back to
`macos-14`, or dropping the Xcode selection step because the image "already has Xcode", reproduces the
failure — and it reproduces intermittently, so a single green run does not prove the toolchain is fine.
