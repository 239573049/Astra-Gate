---
name: release-publishing-incidents
description: Three separate 0.2.0 release failures (workflow syntax, npm size, truncated uploads), the 0.15.2 NSIS/plugin-SDK failures, and the rules they left
type: project
updated: 2026-11-24
source: init
evidence: ["d282517", "4a521a2", "208801f", "415d99d", "astra-gate-web/proxy.ts", "scripts/pack-desktop.mjs", ".github/workflows/release.yml:NSIS for the Windows installer", ".github/workflows/release.yml:Publish the v2 plugin SDK", "https://github.com/239573049/Astra-Gate/actions/runs/45328819596"]
---
The 0.2.0 release (2026-10-08) failed three different ways, and a fourth mismatch showed up in the
update API the same day.

- `d282517`: a step-level `if` in `release.yml` read the `secrets` context. GitHub rejects the whole
  workflow file at startup, so every push failed before any job ran. The fix copies the secret into the
  step's `env` and evaluates that.
- `4a521a2`: the Windows/Linux npm desktop tarballs included the bundled server and the win32-arm64 one
  hit npm's size limit (E413). `scripts/pack-desktop.mjs` now strips `resources/server` from those
  packages; the signed macOS `.app` keeps it because its Resources are sealed.
- `208801f`: every installer uploaded through the docs site was stored truncated. A request that passes
  through `astra-gate-web/proxy.ts` has its body buffered and cut at Next's 10 MB proxy limit, so the proxy's
  matcher excludes `/api`. The same change taught `scripts/sync-versions.mjs` to rewrite
  `desktop/package.json`, because electron-builder stamps it into the app and the 0.2.0 installers
  reported 0.1.0.
- `415d99d`: the web UI caches the `POST /api/update/check` response as the update status, so that route
  and `GET /api/update/status` share one `Status()` builder. They had drifted.

2026-11-24 (0.15.2, run 45328819596) — two workflow traps that both hide the real cause:

- **`choco install nsis` exits 0 when the feed is down.** Chocolatey's community feed answered 503, choco
  printed `Chocolatey installed 0/0 packages` and returned 0, so `NSIS for the Windows installer`
  passed and `Package desktop installers` died with `spawnSync makensis ENOENT` — a Windows build row
  that looks like a code failure. `scripts/package-desktop.mjs` does look in the two standard install
  directories before PATH, so the only real fix is to install NSIS. Re-running the failed job was enough
  once the feed recovered; the step should re-check for `makensis` after the install and exit 1 itself.
- **The plugin-SDK publish step has never packed.** `npm pack ./packages/plugin-sdk --pack-destination
  out/sdk` fails with ENOENT because `out/sdk` is never created (npm does not create the destination),
  so `@astragate/plugin-sdk` is not on npm at all (`npm view` → 404). `continue-on-error` hides it, and
  the npm error names the tarball path, which reads like a publish failure. `mkdir -p out/sdk` before
  the pack (or `--pack-destination "$(pwd)/out"`) fixes it; verify the next release actually publishes.

**Why:** Each fix is small and looks local; the reason it exists is the release that failed.
**How to apply:** A step condition in `release.yml` must not touch `secrets`. A docs-site proxy change
must keep `/api` out of the matcher or large release uploads truncate again with no error. Don't put the
bundled server back into the Windows/Linux npm desktop packages. Any new field on the update status has
to be returned by both endpoints. A tool install step must prove the tool exists afterwards — a
package manager that returns 0 without installing anything is silent otherwise.
