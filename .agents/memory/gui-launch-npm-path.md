---
name: gui-launch-npm-path
description: An update run from the Dock or Finder cannot see node, so the npm child gets a reconstructed PATH
type: project
updated: 2026-10-14
source: init
evidence: ["628e692", "packages/update-core/src/npm-runner.ts:withNodeOnPath"]
---
`packages/update-core` gives its npm child a PATH that contains `node` before running it
(`npm-runner.ts:withNodeOnPath`). When `node` is already on PATH the environment is returned untouched;
otherwise it appends `/opt/homebrew/bin` and `/usr/local/bin`, then the user's login-shell PATH.

**Why:** A desktop app launched from Finder or the Dock inherits launchd's minimal PATH
(`/usr/bin:/bin:/usr/sbin:/sbin`). npm's `#!/usr/bin/env node` shebang then cannot find node, and
nvm, volta, fnm and Homebrew installs are invisible, so the desktop update failed only for GUI
launches (fixed 2026-10-10, `628e692`). Windows is left alone: a GUI process there inherits the user
PATH. The same change keys the update manifest by the .NET RID (`osx-arm64`), not by
`process.platform` (`darwin-arm64`) — a wrong key silently dropped the sha256 check.
**How to apply:** Don't "simplify" `withNodeOnPath` down to the inherited PATH, and don't key manifest
lookups by `process.platform`. A terminal launch is not a test of this path: node is already on PATH
there, which is exactly the branch that returns the environment unchanged.
