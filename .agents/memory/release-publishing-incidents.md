---
name: release-publishing-incidents
description: Three separate 0.2.0 release failures (workflow syntax, npm size, truncated uploads) and the shape mismatch they left rules for
type: project
updated: 2026-10-14
source: init
evidence: ["d282517", "4a521a2", "208801f", "415d99d", "docs/proxy.ts", "scripts/pack-desktop.mjs"]
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
  through `docs/proxy.ts` has its body buffered and cut at Next's 10 MB proxy limit, so the proxy's
  matcher excludes `/api`. The same change taught `scripts/sync-versions.mjs` to rewrite
  `desktop/package.json`, because electron-builder stamps it into the app and the 0.2.0 installers
  reported 0.1.0.
- `415d99d`: the web UI caches the `POST /api/update/check` response as the update status, so that route
  and `GET /api/update/status` share one `Status()` builder. They had drifted.

**Why:** Each fix is small and looks local; the reason it exists is the release that failed.
**How to apply:** A step condition in `release.yml` must not touch `secrets`. A docs-site proxy change
must keep `/api` out of the matcher or large release uploads truncate again with no error. Don't put the
bundled server back into the Windows/Linux npm desktop packages. Any new field on the update status has
to be returned by both endpoints.
