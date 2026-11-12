---
name: website-moved-to-astra-gate-web
description: 2026-11-11 user decision: the docs/ site moved to its own project ~/code/astra-gate-web (rewritten on Arc, Pet dropped, market redesigned); docs/ was removed from this repo the same day
type: project
updated: 2026-11-11
source: session
evidence: ~/code/astra-gate-web (copied from docs/ on 2026-11-11, builds standalone; local git, no remote yet); docs/, .github/workflows/docs.yml, the root .dockerignore and the pnpm workspace entry removed here on 2026-11-11
---

On 2026-11-11 the user asked to move the website in `docs/` (landing page, fumadocs docs, plugin market, download page,
release / plugin-market / cloudflared-mirror APIs) to a separate project at `~/code/astra-gate-web` and rewrite its whole
UI with Arc (uiarc.dev, React + CSS modules; the user has Arc Pro). Arc Pro source is fetched through Astra's own MCP
gateway (`/mcp/arc`, an MCP server registered in the local Astra) as the user asked. The Astra Pet feature (`/pet`,
`/api/pet-releases/*`, `PET_UPLOAD_TOKEN`) was removed from the new project on the user's request. The plugin market is
to be redesigned from scratch and is now served at `/integrations` (`/plugins` redirects there). Three design proposals
(A console-native, B developer terminal, C editorial) were built under `/proposals/*` in the new project; the user
rejected all three. Round 2 is static HTML mockups in `~/code/astra-gate-web/design/round-2/` (index.html: 3 site
directions + 4 plugin-market concepts, chosen separately). The user picked site direction 2 「从你的问题开始」 (home
opens with a client × provider picker that answers with the exact steps; orange accent) and market concept 4 「集成目录」
(the market merged into an integrations directory of built-in providers/clients plus plugins), and it was implemented the
same day. The site keeps the plugin facts the registry lacks (sign-in method, plans, model families, region) in its own
`lib/catalog/index.ts`; moving them into the registry is a linked-update-10 change on this side.

Also on 2026-11-11, `docs/` was removed from this repository (with `.github/workflows/docs.yml`, the root `.dockerignore`
and the pnpm workspace entry); `docs/content` was identical to the new project's at that point, so nothing had to be
carried over. AGENTS.md now calls it "the website repository" and writes its paths without the `docs/` prefix.

**Why:** the user wants the website as its own service with a new design, separate from the gateway repository.

**How to apply:** change the website in `~/code/astra-gate-web`. Cross-repo couplings are kept by hand, in both
repositories: `Market/market.json` ↔ its `lib/plugin-market/upstream-fallback.json`, the plugin-market contract (linked
update 10), the release feed and install scripts (linked updates 13, 16), the cloudflared mirror allowlist (12), the docs
pages the linked updates name (`content/docs/...`), and its `scripts/sync-catalog.mjs`, which reads
`src/Astra.Providers/Templates/catalog.json` from here. `scripts/plugin-ui/gen-docs.mjs` writes the plugin-dev component
pages into the website (`$ASTRA_WEB_DIR`, default `../astra-gate-web`; it fails when that is missing), and
`scripts/visual/capture-web.mjs` uses its own copy of the screenshot helper (`scripts/visual/lib/snap-core.mjs`), with
playwright-core now a root devDependency.
