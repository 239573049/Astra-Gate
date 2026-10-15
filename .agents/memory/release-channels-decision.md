---
name: release-channels-decision
description: Why the preview channel is derived from the version, why the preview feed merges in stable, and why the manifest is published after npm
type: project
updated: 2026-10-15
source: session
evidence: ["scripts/release-channel.mjs", "docs/lib/releases.ts:FEED_SOURCES", "src/Astra.Server/Hosting/UpdateCheckService.cs:UpdateChannels", ".github/workflows/release.yml (publish job step order)"]
---
On 2026-10-15 the release design gained a preview channel. The decisions and the alternatives
that were rejected:

- **The channel comes from the version** (`x.y.z` stable, `x.y.z-preview.N` preview). There is no
  workflow input or flag for it. A separate switch could be forgotten and send a preview to
  stable users. Only `-preview.N` is accepted, so the rule stays one regex wherever it is copied.
  `-beta` / `-rc` were rejected for the same reason.
- **The preview feed merges in stable** (`FEED_SOURCES`). Uploading each stable twice was
  rejected: it doubles the installer storage, and a forgotten copy would leave preview users
  stuck on an old preview after the stable ships.
- **`updateChannel` stays in SQLite `AppSettings`**, as before. The old value `"beta"` is read as
  preview. The settings API accepted it before, but nothing ever published a beta feed and the
  UI never offered it. The cost: a stopped server's setting cannot be read, so `astra update`
  falls back to stable and has `--channel` for one-off runs.
- **The manifest is uploaded after npm.** Before this change, `latest.json` reached the docs site
  ahead of the npm publish, so clients could be offered a version for up to 90 minutes (the
  `wait_for` deadline) whose server package `npm install` could not fetch yet.
- **The version comparison follows full semver precedence everywhere.** Before, the server,
  update-core and the docs site dropped the prerelease suffix (`0.10.0-preview.2` equalled
  `0.10.0`), and `releases.json` sorted versions as strings (`0.10.0` before `0.9.5`).

**Why:** Each rule closes a way a preview could reach stable users, or a user could be offered
an update they cannot install.
**How to apply:** Do not add a channel input to `release.yml` or a second prerelease form. Keep
`latest.json` the last feed upload. When the version rule or ordering changes, update all four
copies listed in `.agents/skills/release/references/channels.md`. Related:
[[release-publishing-incidents]].
