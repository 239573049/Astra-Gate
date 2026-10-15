---
name: release-troubleshoot
description: Diagnose and recover an Astra release that failed, stalled or published only partly. Covers a red release.yml run, a version that never reached npm, the update feed or the GitHub Release, users not offered an update, and a preview published as latest. Use when a release run fails, "users don't see the update", "发布失败", "更新没推送", or a release needs to be repaired or rolled forward.
---

# Repair a release

Find the first step that did not do its job, then re-run from there. Nearly every step is
idempotent:
- the npm publish skips packages that are already on npm;
- the docs upload overwrites a file by name;
- `gh release edit` / `upload --clobber` updates an existing release;
- the docker job can be re-run.

**Never delete or reuse a version number.** npm refuses to republish it. Fix forward with the
next patch or `-preview.N+1`.

Do not re-run, cancel, dispatch or change dist-tags without being asked. Say what you found and
what you propose first.

```bash
RUN=$(gh run list --workflow release.yml --limit 1 --json databaseId -q '.[0].databaseId')
gh run view "$RUN"                  # which job / step failed
gh run view "$RUN" --log-failed | tail -80
```

## Symptoms

### The `version` job failed
- `is not a release version`: the version is not `x.y.z` / `x.y.z-preview.N`. Bump again with
  `node scripts/sync-versions.mjs --version <valid>`.
- `CHANGELOG.md has no '## [x.y.z]' section`: add it (`release-notes` skill), commit, push.
  The run for the bad commit stays failed; the new push releases.
- `Tag vX does not match`: the tag and `cli/package.json` disagree. Delete the wrong tag only if
  nothing was published from it.

### "already on npm — nothing to release"
The two-phase shortcut: `@aidotnet/astra-gate@<version>` exists, so `build` and `publish` were
skipped. Expected for a re-push of a released version. If the earlier run had died after
publishing that package, re-run the earlier run's failed jobs instead (`gh run rerun <id> --failed`).

### A build row failed
- `check-aot-warnings` / `plugin-selftest`: a real AOT break. Fix it in code and release the
  next patch (see AGENTS.md "Native AOT"; never widen the Jint exception).
- mac rows: missing signing secrets fail fast. The "Build desktop app" step already retries
  electron-builder 3× on mac (a dropped stapler connection, error 68, failed 0.9.5's osx-x64 row).
  When all three attempts fail, check the log for a real signing or notarization rejection before
  re-running the failed jobs. Link errors about `__swift_FORCE_LOAD_*` mean the Xcode selection
  broke (memory: `mac-aot-needs-xcode-26`).
- `pnpm install` / Electron header download: transient, re-run.
- `cp: out/desktop/*-mac.yml: No such file`: electron-builder produced no update file for that
  row. Inspect `out/desktop` in the log.

### `publish` failed or timed out
- `still not available on npm after 90 minutes`: npm is slow to process the large platform
  packages. Run `gh run rerun "$RUN" --failed`. The re-run skips published packages, waits again,
  then publishes the CLI and the manifest.
- `E413` / payload too large: a desktop tarball grew past npm's limit (memory:
  `release-publishing-incidents`). Do not put the bundled server back into the Windows/Linux
  desktop packages.
- `401/403` on npm: the `NPM_TOKEN` secret expired. Ask the user to renew it, then re-run.

### Green run, but clients are not offered the version
Check the feed first (`release` skill, `references/verify.md`):
- Both docs-site steps print `::warning::Not uploading…` / `Not publishing the update manifest…`
  and exit 0 when `DOCS_RELEASE_URL` / `DOCS_RELEASE_TOKEN` are unset. Read those step logs.
- The upload answered 400 `does not belong in the … channel`: the workflow sent a version to the
  wrong channel. Fix `release.yml` and re-run `publish`.
- `latest.json` still shows the old version, but `releases.json` lists the new one with only
  installers: the manifest step did not run. Re-run `publish` (everything before it is skipped
  or overwritten).
- Clients cache nothing longer than their poll (server 12 h, desktop 12 h). `astra update --check`
  or Settings → Check for updates forces a check.
- Preview users who never turned on **Receive preview updates** get no preview. That is by design.

### A preview became `latest` on npm
This must not happen (`--tag "$NPM_TAG"`), but if it does, act right away. With the user's
go-ahead:

```bash
npm dist-tag add astragate@<last-stable> latest
npm dist-tag add @aidotnet/astra-gate@<last-stable> latest
npm dist-tag add astragate@<the-preview> preview
npm dist-tag add @aidotnet/astra-gate@<the-preview> preview
```

The platform packages need no fix: the CLIs pin them by exact version. Also check the GitHub
Release (`gh release edit v<ver> --prerelease`) and `ghcr.io/…:latest`. Re-tag `latest` back by
re-running the previous stable's docker job.

### A bad version reached users
Roll forward. Ship a fix as the next version of the same channel. For stable, also consider
moving the `latest` dist-tag back to the previous good version (`npm dist-tag add …@<good> latest`)
until the fix is out. Do not delete files under the docs site's `DATA_DIR`: the feed would then
serve an older manifest, and clients never downgrade anyway.

## After a repair

Record the incident in `.agents/memory/release-publishing-incidents.md` if it taught something
the code does not show (AGENTS.md "Repository memory").
