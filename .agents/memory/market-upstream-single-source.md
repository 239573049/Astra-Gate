---
name: market-upstream-single-source
description: The docs site's plugin market reads one fixed community registry and refuses a cache written by the previous one
type: project
updated: 2026-10-14
source: init
evidence: ["6abd354", "docs/lib/plugin-market/upstream.ts"]
---
The plugin market's community list comes from exactly one address, the `registry.json` of
`astragate-community/astra-gate-plugins`, hardcoded in `docs/lib/plugin-market/upstream.ts`. The cached
copy (`upstream.json`) records the address it was fetched from, and a cache whose recorded source
differs is never served.

**Why:** Until 2026-10-14 (`6abd354`) the address was the `PLUGIN_MARKET_UPSTREAM` environment variable,
and the cache did not record where it came from. After the upstream moved, a list fetched from the old
address could be served as the new one's. The variable was removed in the same change (Dockerfile,
README, the market doc page), so nothing can point the site elsewhere.
**How to apply:** Don't reintroduce a configurable upstream. If the community registry moves again, change
the constant and expect the on-disk cache to be ignored until it is refetched — that rejection is the
point of the `source` field. The client's own default
(`src/Astra.Plugins/Market/PluginMarket.cs:PluginMarket.DefaultUrl`) is the docs site, not this address.
