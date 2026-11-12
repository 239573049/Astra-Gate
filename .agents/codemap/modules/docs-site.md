---
module: docs-site
sources: []
external: astra-gate-web
commit: 6324e30
updated: 2026-11-11
---
# docs-site (the website repository, `astra-gate-web`)

Not in this repository any more. Until 2026-11-11 this module was `docs/` (`@aidotnet/docs`); it is now the website
repository (`astra-gate-web`, served at astra-gate.si; locally `../astra-gate-web`), a separate Next.js + Fumadocs
project with its own pnpm install, `AGENTS.md`, README, Dockerfile and image workflow (`.github/workflows/docker.yml`).
Its internals are mapped there, not here; this page keeps only what this repository depends on. Paths below are relative
to that repository and carry no line numbers (its UI is being rewritten).
It is the documentation site (`/docs/**`), the website (landing page, `/download`, the plugin market at `/integrations` and
`/en/integrations` — `/plugins` redirects there), **and** three runtime services the product depends on: the
plugin-market registry, the client-release feed and the cloudflared download mirror.
**Not responsible for:** the plugin runtime and the client-side market parser (`src/Astra.Plugins/Market/PluginMarket.cs`),
the update-manifest format and its client readers (`packages/update-core`), the release workflow that uploads bytes there
(`.github/workflows/release.yml`), and the admin UI it screenshots (`web/`).

## Entry points this repository uses
| Contract | Website file | Used by |
|---|---|---|
| `GET /api/plugin-market/registry.json`, `/search`, `/packages/<dir>/<version>.tgz` | `app/api/plugin-market/**`, `lib/plugin-market/` | `PluginMarket.DefaultUrl` (`src/Astra.Plugins/Market/PluginMarket.cs`), `cli/src/commands/plugin.ts` |
| `GET /api/client-releases/<channel>/**`, `POST /api/client-releases/upload` | `app/api/client-releases/**`, `lib/releases.ts` | `packages/update-core`, `DesktopReleaseFeed`, `release.yml` upload steps |
| `GET /api/tools/cloudflared/<version>/<asset>` | `app/api/tools/cloudflared/**`, `lib/tools-mirror.ts` (`CLOUDFLARED_ASSETS`) | `CloudflaredRuntime` (`src/Astra.Server/Tunnel/CloudflaredRuntime.cs`) |
| `GET /api/models-catalog.json` | `public/api/models-catalog.json` (from `scripts/refresh-model-catalog.mjs`) | `ModelSyncService.SourceUrl` |
| `/install.sh`, `/install.ps1` | `public/install.sh`, `public/install.ps1` | users; AGENTS.md linked update 16 |
| docs pages | `content/docs/**/*.mdx` + one `meta.json` per folder | AGENTS.md linked updates that name `content/docs/...` |

## Couplings kept by hand (two repositories, no shared commit or CI)
- `lib/plugin-market/upstream-fallback.json` there is a copy of `src/Astra.Plugins/Market/market.json` here (the website has
  no `src/`): regenerate it from this file, never edit it on its own (AGENTS.md linked update 10).
- The market document shape (`lib/plugin-market/types.ts`, `toV2`, `metaFromAstraManifest`) ↔ `PluginMarket.Parse` /
  `ToV2Listing`, `web/src/api/types.ts`, `cli/src/commands/plugin.ts` (linked updates 10, 27).
- `CLOUDFLARED_ASSETS` ↔ `CloudflaredRuntime.Asset` (linked update 12).
- `lib/releases.ts` (`compareVersions`, `FEED_SOURCES`, `canonicalFileName`, upload tags) ↔ `release.yml`,
  `packages/update-core`, `UpdateCheckService.IsNewer`, `ReleaseVersion`, `DesktopReleaseFeed` (linked updates 13, 16, 22).
- `content/docs/plugin-dev/components/` is written by `node scripts/plugin-ui/gen-docs.mjs` here (`$ASTRA_WEB_DIR`, default
  `../astra-gate-web`; linked update 27).
- `scripts/sync-catalog.mjs` there copies `src/Astra.Providers/Templates/catalog.json` from here into its
  `lib/catalog/providers.json` (default path `../LocalGate`, or `$ASTRA_REPO`).
- Its `scripts/snap.mjs` screenshots this repository's `web` dev server (:5173) + a running astra-server into
  `public/screenshots/`. `scripts/visual/lib/snap-core.mjs` here is a copy of its `scripts/lib/snap-core.mjs`, used by
  `scripts/visual/capture-web.mjs`.

## Runtime state (on the website's server, not here)
`${DATA_DIR}` (default `/app/data`): `client-releases/<channel>/…`, `plugin-market/…`, `tools/cloudflared/<version>/<asset>`.
Env tokens: `PLUGIN_UPLOAD_TOKEN`, `CLIENT_UPLOAD_TOKEN`, optional `GITHUB_TOKEN`. `DOCS_RELEASE_URL` / `DOCS_RELEASE_TOKEN`
are this repository's release-workflow secrets, not read there.

## Related flows
- [request-tracing](../flows/request-tracing.md) — `content/docs/console/traces.mdx` there is the user-facing description of what the spans record.
- [release-and-update](../flows/release-and-update.md)
- [plugin-install-and-run](../flows/plugin-install-and-run.md)
