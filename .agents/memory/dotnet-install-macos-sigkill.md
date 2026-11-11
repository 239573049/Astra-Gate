---
name: dotnet-install-macos-sigkill
description: dotnet-install.sh over an existing ~/.dotnet on macOS leaves the dotnet host dying with SIGKILL (137); copy it to a new inode to fix
type: project
updated: 2026-11-11
source: session
evidence: SDK move to 11.0.100-rc.3.26522.148 (global.json); `codesign -v ~/.dotnet/dotnet` passed while every run exited 137
---

Moving the pinned SDK with `dotnet-install.sh --install-dir ~/.dotnet` on Apple silicon rewrites the `dotnet` host in place.
The script reports success, the file's signature verifies, and every later `dotnet` call is killed with exit 137.

**Why:** the kernel keeps code-signing state per inode, so a binary overwritten in place fails its check at exec even though
the new bytes are validly signed.

**How to apply:** after installing, run `cd ~/.dotnet && cp -p dotnet dotnet.new && mv -f dotnet.new dotnet`. Also,
`global.json`'s `rollForward: latestFeature` picks the new SDK as soon as it is installed, so do not install one while a
build or test run is in progress.

The same move to the GA SDK 11.0.100 (2026-11-11) used this recipe: the `cp`/`mv` ran straight after the installer, before
any other `dotnet` call, and no 137 appeared (whether the bare install would have hit it again is unknown). The tarball is
~200 MB and can take well over 10 minutes here; run the installer in the background.
