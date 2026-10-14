---
module: docs-site
sources: ["docs/**", "docs/Dockerfile", ".github/workflows/docs.yml"]
commit: 6324e30
updated: 2026-10-14
---
# docs-site (`@aidotnet/docs`)

The private Next.js + Fumadocs package in `docs/`: the bilingual documentation site (`/docs/**` zh, `/en/**` en), the official marketing website, **and** three runtime services the rest of the product depends on — the plugin-market registry, the desktop client-release feed, and the cloudflared download mirror. It ships as its own Docker image built from the repo root.
**Not responsible for:** the plugin runtime and the client-side market parser (`src/Astra.Plugins/Market/PluginMarket.cs`), the update-manifest format and its client readers (`packages/update-core`), the release workflow that uploads bytes here (`.github/workflows/release.yml`), and the admin UI screenshots' *source* pages (`web/`).

## Entry points
| Symbol | File | Role |
|---|---|---|
| `scripts` (`dev`/`build`/`start`/`types:check`) | `docs/package.json:6` | the only commands; no `lint`, no `test` |
| `GET /api/plugin-market/registry.json` | `docs/app/api/plugin-market/registry.json/route.ts:14` | the registry Astra's client reads by default; ETag/304 |
| `GET /api/plugin-market/search` | `docs/app/api/plugin-market/search/route.ts:14` | market + npm + GitHub `magpie-plugin` search for the client |
| `POST /api/plugin-market/packages` | `docs/app/api/plugin-market/packages/route.ts:43` | publish a tarball (Bearer `PLUGIN_UPLOAD_TOKEN`) |
| `POST/DELETE /api/plugin-market/entries` | `docs/app/api/plugin-market/entries/route.ts:35` | list/re-describe an npm-only package, take entries down |
| `GET /api/client-releases/<channel>/**` | `docs/app/api/client-releases/[channel]/[...path]/route.ts:42` | `releases.json`, `latest-mac.yml`, `latest.json`, file downloads |
| `POST /api/client-releases/upload` | `docs/app/api/client-releases/upload/route.ts:22` | release-artifact upload (Bearer `CLIENT_UPLOAD_TOKEN`) |
| `GET /api/tools/cloudflared/<version>/<asset>` | `docs/app/api/tools/cloudflared/[version]/[asset]/route.ts:18` | streaming cloudflared mirror |
| `proxy` + `config.matcher` | `docs/proxy.ts:14`, `docs/proxy.ts:38` | markdown negotiation rewrite for `/docs/*`; excludes `/api` |
| `Dockerfile` (base/deps/build/runtime) | `docs/Dockerfile:9` | standalone-output image, context = repo root |
| `Docs image` workflow | `.github/workflows/docs.yml:31` | builds/pushes `ghcr.io/<owner>/<repo>/docs` on `docs/**` changes |

## Key types and functions
| Symbol | File | Role |
|---|---|---|
| `siteUrl` / `docsContentRoute` / `getPageMarkdownUrl` | `docs/lib/shared.ts:12`, `:16`, `:20` | fixed origin `https://astra-gate.si`; canonical/hreflang/sitemap/robots/llms/OG URLs |
| `source` / `docsLlms` | `docs/lib/source.ts:21`, `:27` | Fumadocs loader over `content/docs` (lucide icons plugin); `llms()` renderer |
| `RegistryDocument` / `RegistryListing` / `HostedTarball` / `SearchResult` | `docs/lib/plugin-market/types.ts:70`, `:56`, `:11`, `:76` | the market document shapes (v2) |
| `buildListings` / `buildRegistry` / `findListing` | `docs/lib/plugin-market/registry.ts:33`, `:46`, `:53` | merge upstream + published; ETag over content only |
| `loadUpstream` / `parseUpstream` | `docs/lib/plugin-market/upstream.ts:160`, `:58` | fetches the one fixed community registry, 6 h TTL, npm enrichment |
| `readEntries` / `updateEntries` / `verifyPublishToken` / `packageUrl` | `docs/lib/plugin-market/store.ts:69`, `:91`, `:57`, `:52` | `${DATA_DIR}/plugin-market/` storage, serialized RMW, constant-time token |
| `receiveTarball` / `readPackageJson` | `docs/lib/plugin-market/tarball.ts:28`, `:73` | streams upload with hashing; reads only `<top>/package.json` from the `.tgz` |
| `cleanMeta` / `metaFromPackageJson` / `kindOfKeywords` | `docs/lib/plugin-market/meta.ts:33`, `:82`, `:107` | metadata validation/mirroring of the client's rules |
| `searchPlugins` | `docs/lib/plugin-market/search.ts:81` | market-first search composition, 10 min cache |
| `readIndex` / `saveUpload` / `latestVersion` / `renderLatestMacYml` / `compareVersions` | `docs/lib/releases.ts:95`, `:134`, `:183`, `:205`, `:190` | release storage, index merge, electron-updater feed |
| `CLOUDFLARED_ASSETS` / `openCloudflared` / `dataDir` | `docs/lib/tools-mirror.ts:22`, `:55`, `docs/lib/releases.ts:21` | asset allowlist, tee-to-cache with digest verification, `${DATA_DIR}` |
| `PluginListView` / `PluginDetailView` / `copy` / `pluginBase` | `docs/components/plugins/plugin-list.tsx:8`, `plugin-detail.tsx:17`, `market.tsx:106`, `:9` | bilingual market UI (client-safe: no `node:fs` import) |
| `homeOptions` / `baseOptions` | `docs/lib/layout.shared.tsx:21`, `:6` | nav links + language switch (`/` ↔ `/en`) |

## Dependencies
- Uses: `astra-plugins` via HTTP (build-time snapshot only) — `docs/lib/plugin-market/upstream-fallback.json` is byte-identical to `src/Astra.Plugins/Market/market.json` (both 27360 B, sha256 `b54a6b41…a49f`, 21 plugins); the live list comes from the fixed GitHub URL at `docs/lib/plugin-market/upstream.ts:18`.
- Uses: npm registry + GitHub API via HTTP — `docs/lib/plugin-market/search.ts:10`, `:38`; cloudflared GitHub releases via `docs/lib/tools-mirror.ts:31`.
- Used by: `astra-plugins` via HTTP — `PluginMarket.DefaultUrl` is `https://astra-gate.si/api/plugin-market/registry.json` (`src/Astra.Plugins/Market/PluginMarket.cs:47`); the same registry document is parsed there.
- Used by: `cli`, `desktop-app`, `update-core` via HTTP — the feed URL is baked in at `.github/workflows/release.yml:254` (`https://astra-gate.si/api/client-releases/stable`); `latest.json` is served by `docs/app/api/client-releases/[channel]/[...path]/route.ts:80`.
- Used by: `astra-server` via HTTP — the cloudflared asset names are this mirror's allowlist (`src/Astra.Server/Tunnel/CloudflaredRuntime.cs`, mirrored at `docs/lib/tools-mirror.ts:22`), and `ModelSyncService.SourceUrl` is `https://astra-gate.si/api/models-catalog.json` (`src/Astra.Server/Api/ModelSyncService.cs:76`) served from `docs/public/api/models-catalog.json`.
- Used by: `packaging-release` via CI — `docs.yml` builds the image; `release.yml` posts artifacts to `/api/client-releases/upload` (`release.yml:422`).

## State it owns
- **Content** (source of truth, edit here): `docs/content/docs/**/*.mdx` plus one `meta.json` per module folder. Root `docs/content/docs/meta.json:4` fixes the module order `guide, console, cli, desktop, advanced`; each folder's `meta.json` carries `title`/`description`/`icon`/`defaultOpen` and a `pages` array whose `---[Icon]Name---` entries become sidebar separators (rendered by `docs/app/docs/sidebar-components.tsx:10`).
- **Runtime volume** `${DATA_DIR}` (default `/app/data`, `docs/Dockerfile:41`, `docs/lib/releases.ts:21`): `client-releases/<channel>/<version>/` + `index.json` (`docs/lib/releases.ts:25`), `plugin-market/entries.json`, `plugin-market/packages/<dir>/<version>.tgz`, `plugin-market/upstream.json` cache (`docs/lib/plugin-market/store.ts:9`), `tools/cloudflared/<version>/<asset>` (`docs/lib/tools-mirror.ts:40`). Mounted volume; nothing here is committed.
- **Env tokens**: `PLUGIN_UPLOAD_TOKEN` (unset ⇒ market writes answer 503, `store.ts:59`), `CLIENT_UPLOAD_TOKEN` (unset ⇒ uploads 503, `releases.ts:36`), optional `GITHUB_TOKEN` (search/mirror rate limit only). `DOCS_RELEASE_URL`/`DOCS_RELEASE_TOKEN` are the *release workflow's* secrets, not read here.
- **Generated, do not hand-edit**: `docs/public/screenshots/*.png` (from `docs/scripts/snap.mjs` / `snap-mock.mjs`), `docs/public/api/models-catalog.json` (from `docs/scripts/refresh-model-catalog.mjs`), `docs/.source` and `docs/.next` (gitignored).
- **Derived, must be kept in step**: `docs/lib/plugin-market/upstream-fallback.json` is a copy of `src/Astra.Plugins/Market/market.json`, not an independent file.

## Contracts it exposes
- `GET https://astra-gate.si/api/plugin-market/registry.json` → `RegistryDocument` v2 (`docs/lib/plugin-market/types.ts:70`): `plugins[].{package,name,kind,providers,summary,origin:'astra'|'upstream',latest,tarballs[],spec,icon,…}`; ETag covers content only (`registry.ts:49`). Field list is documented for users at `docs/content/docs/advanced/plugin-market.mdx`.
- `GET …/api/plugin-market/search?q=` (≤100 chars, `search/route.ts:17`) → `SearchResult[]` with `source: 'astra'|'npm'|'github'`.
- `GET …/api/plugin-market/packages/<dirName>/<version>.tgz` where `dirName = package.replace('/','__')` (`store.ts:35`); published versions are immutable (`packages/route.ts:97` returns 409).
- Publish headers: `Authorization: Bearer $PLUGIN_UPLOAD_TOKEN`, `x-astra-package`, `x-astra-meta-b64` (`packages/route.ts:24-30`); entries body shape at `entries/route.ts:18-24`.
- `GET …/api/client-releases/<channel>/{releases.json,latest-mac.yml,latest.json,v/<version>/<file>,<file>}` — documented at `docs/app/api/client-releases/[channel]/[...path]/route.ts:13-18`; upload headers `x-astra-{version,platform,kind,channel,notes-b64}` at `upload/route.ts:10-13`.
- `GET …/api/tools/cloudflared/<version>/<asset>`, asset allowlist `docs/lib/tools-mirror.ts:22`, version regex `:36`.
- Site metadata: `robots.txt` disallows `/api/` (`docs/app/robots.ts:11`); `sitemap.xml` lists `/`, `/en`, `/download`, `/plugins`, `/en/plugins` plus every listing and doc page (`docs/app/sitemap.ts:13`); `llms.txt` / `llms-full.txt` / `/llms.mdx/docs/<path>/content.md` (`docs/app/llms.txt/route.ts:56`, `llms-full.txt/route.ts:28`, `llms.mdx/.../route.ts:7`).
- Sidebar metadata contract: per-folder `meta.json` `{title,description,icon,defaultOpen,pages}`; separators spelled `---[LucideName]Label---`.

## How to extend
- To add a docs page: create `docs/content/docs/<folder>/<name>.mdx`, add `<name>` to that folder's `pages` array (`docs/content/docs/<folder>/meta.json`), and — for a new module folder — also to `docs/content/docs/meta.json:4`. Missing the folder entry makes the group invisible even though the page builds.
- To add a new module folder: create the directory with a `meta.json` (title/description/icon/defaultOpen/pages) **and** add it to `docs/content/docs/meta.json:4`; missing the root entry leaves the whole folder out of the sidebar and out of `llms.txt`/`llms-full.txt` ordering.
- To add a field to the market contract: add it in `docs/lib/plugin-market/types.ts`, emit it in `registry.ts`/`meta.ts`, then mirror it in `src/Astra.Plugins/Market/PluginMarket.cs` and `web/src/api/types.ts` (and `cli/src/commands/plugin.ts` for search results), then document it in `docs/content/docs/advanced/plugin-market.mdx`. Missing the .NET side makes the field silently invisible to Astra.
- To add a hosted-asset kind to the tools mirror: add the name to `CLOUDFLARED_ASSETS` (`docs/lib/tools-mirror.ts:22`) **together with** `CloudflaredRuntime.Asset` in `src/Astra.Server/Tunnel/CloudflaredRuntime.cs`; a name only on one side means the server asks for an asset the mirror 404s (or the mirror proxies a file nobody requests).
- To change the domain: edit `siteUrl` in `docs/lib/shared.ts:12` only — canonical, hreflang, sitemap, robots, llms URLs and the documented `DefaultUrl` all derive from it.

## Invariants and traps
- The `proxy.ts` matcher must keep excluding `/api` — anything through the proxy gets its body buffered and cut at Next's 10 MB limit, which silently truncated desktop installers sent to `/api/client-releases/upload` — see `docs/proxy.ts:34`.
- Pages that read runtime state must be `dynamic = 'force-dynamic'`: `/plugins`, `/download` and the API routes carry it (`docs/app/(home)/plugins/page.tsx:7`, `docs/app/(home)/download/page.tsx:8`); without it Next freezes a build-time answer that has no `DATA_DIR` behind it.
- `docs/components/plugins/*` must stay free of the registry store — `docs/lib/plugin-market/pages.ts:6` states that importing it pulls `node:fs` into a component the client also bundles and breaks the build. Keep `buildListings` calls in the `app/**` server pages.
- `upstream-fallback.json` exists only because the docs image build context has no `src/`: it must be regenerated from `src/Astra.Plugins/Market/market.json` (same bytes today, sha256 `b54a6b41…a49f`), never edited on its own.
- The upstream cache is only served when it records the *current* fixed address — `docs/lib/plugin-market/upstream.ts:118` drops a copy from another address so one list is never served as another's.
- A published tarball version is immutable and up to 50 MB (`store.ts:16`, `packages/route.ts:97`); the tarball scan is capped (200 MB unpacked, `tarball.ts:11`) so a gzip bomb cannot tie the server up.
- `docs` has no `lint`/`test` script, so `pnpm -r --if-present lint|typecheck|test` skips it silently (`docs/package.json:6`).
- `pnpm --filter @aidotnet/docs build` is a full Next.js build and fails when the shell exports a non-standard `NODE_ENV` — unset it first (`docs/README.md:10`).
- `/docs` itself has no page; `next.config.mjs:12` redirects it to `/docs/guide`.
- Screenshots contain the developer's real usage data; `docs/README.md:84` warns to check them before publishing, and `.dockerignore:15` keeps `docs/shots` out of the image. The only non-live screenshot is `login.png`, rendered from `docs/shots/login.html` by `docs/scripts/snap-mock.mjs` (`docs/README.md:78`).

## Tests
There are none: `docs/package.json` defines only `dev`, `build`, `start` and `types:check`, and the repo-wide `pnpm -r --if-present lint|typecheck|test` passes over the package without doing anything. When this module changes, run both by hand from the repo root: `pnpm --filter @aidotnet/docs types:check` (which is `next typegen && tsc --noEmit`) and, with `NODE_ENV` unset, `pnpm --filter @aidotnet/docs build`. Behaviour of the API routes is otherwise only exercised in production — the .NET-side guards live in `tests/Astra.Server.IntegrationTests/JsonRegistrationTests.cs` and the plugin tests, not here.

## Related flows
- [request-tracing](../flows/request-tracing.md) — `docs/content/docs/console/traces.mdx` is the user-facing description of what the spans record.
- [release-and-update](../flows/release-and-update.md)
- [plugin-install-and-run](../flows/plugin-install-and-run.md)
