---
name: macos-13-floor
description: Issue #27 — the AOT binaries keep macOS 13 on .NET 11 through a Swift lookup shim, by user decision; statically checked, not yet run on a real macOS 13
type: project
updated: 2026-11-09
source: session
evidence: ["src/MacOsFloor/swift-lookup-shim.c", "scripts/check-macos-floor.mjs", "0c0781a", "https://github.com/239573049/Astra-Gate/issues/27"]
---
.NET 11 (first shipped in 0.11.2, `0c0781a`) raised the AOT default deployment target to 14.0 and compiles its runtime
pack for 14, so `astra-server` died under dyld on macOS 13 with `_swift_getTypeByMangledNameInContext2` missing. The user
chose (2026-11-09) to keep 13 working over declaring 14 the minimum or going back to .NET 10.

**Why:** of ~690 imports only that symbol and its `InMetadataState2` sibling are absent from the macOS 13.3 SDK stubs. In
Swift's `MetadataLookup.cpp` the "2" versions differ only by authenticating `context`, a no-op off arm64e, so forwarding
them is equivalent on our RIDs. Objects remain minos 14 (ld warns; harmless) and Microsoft does not support this.
**How to apply:** Do not treat a green check as proof it runs: no CI image has macOS 13, and code the minos 14 objects
compiled without availability guards could still reach a 14-only Objective-C method, which a symbol check cannot see. Ask
someone on macOS 13 to run a build, then remove this caveat. If a future runtime pack needs more than an equivalent
forward, raising the floor is the expected outcome; see AGENTS.md (Native AOT).
