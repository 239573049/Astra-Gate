---
name: release-notes
description: Write the CHANGELOG.md section for an Astra release (stable or preview) from the commits since the last release. Use while cutting a release, or when asked to draft release notes, a changelog entry, "更新日志" or "版本说明". The section becomes the GitHub Release body, the download-page notes and the in-app update dialog, so it is written for users.
---

# Write the release notes

`release.yml` reads the section with `scripts/changelog-section.mjs <version>` and fails without
one. The same text goes to three audiences: the GitHub Release, the download page, and the update
dialog and tray notification (cut at 600 characters, so the first bullet matters most).

## Gather what changed

```bash
PREV=$(git describe --tags --abbrev=0 --match 'v*' 2>/dev/null || git log --format=%h --grep '^chore(release)' -n1)
git log --no-merges --format='%h %s' "$PREV"..HEAD
git diff --stat "$PREV"..HEAD -- src cli web desktop packages docs/public
```

Read the diffs of anything user-visible. Commit subjects alone are not enough: the notes say
what changed for the user, not which files moved. A preview's notes cover the commits since the
previous release of either channel.

## Shape (match the existing sections)

```markdown
## [0.10.0-preview.1] - 2026-10-20

> 预览版：提前体验下一个正式版的功能，可能不稳定。在 设置 → 关于 打开「接收预览版更新」后才会收到。

### 新增

- **功能名**：一两句话说明用户能做什么、在哪里用（页面 / 命令 / 设置项）。

### 改进

- …

### 修复

- **现象**：用户看到的问题，以及现在的行为。
```

- Chinese, the same voice as the sections already in `CHANGELOG.md`. Use the headings that apply
  (`### 新增` / `### 改进` / `### 修复`) in that order, and leave out empty ones.
- The heading is exactly `## [<version>] - <YYYY-MM-DD>`, with today's date. The version is
  copied from `cli/package.json`.
- Each bullet starts with a bold name, then the effect for the user. Name the UI location, the
  command or the setting. Mention a database migration (`数据库迁移 00NN`), a new API route and
  anything the user must do (restart, re-enable a client).
- A **preview** starts with the blockquote above, so a reader of the GitHub Release or download
  page knows what they are looking at.
- A **stable that promotes previews** (`0.10.0` after `0.10.0-preview.N`) gets complete notes for
  everything since the previous stable. Stable users never saw the preview sections, so merge
  them rather than writing "see the previews". Keep the preview sections in the file; they are
  history.
- Leave out internal-only changes (refactors, CI, test-only work, the codemap) unless they change
  what users get.
- No secrets, no internal hostnames, no personal data.

## Check

```bash
node scripts/changelog-section.mjs <version>   # prints exactly your section
```
