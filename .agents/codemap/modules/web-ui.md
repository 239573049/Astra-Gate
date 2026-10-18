---
module: web-ui
sources: ["web/**"]
commit: 6324e30
updated: 2026-10-18
---
# web-ui

The private workspace package `@aidotnet/web` (`web/`) — the bilingual (zh/en) React admin console built with
Vite: the app shell, the route tree behind a single auth gate, the whole `/api` client, and the tray popover
that the desktop app embeds. It ships twice as a build artifact only — `web/dist` into
`src/Astra.Server/wwwroot` (served by astra-server) and `web/dist` into `desktop/renderer` — and has no
library entry point.
**Not responsible for:** the HTTP surface itself and its security rules (`astra-server` — this package is only
a client of it), the `/v1` gateway and `/key` backend (`astra-gateway`), the window, tray, menus, updater and
`window.__ASTRA__` preload (`desktop-app`), the CLI's own client of the same API (`cli`), and all persistence
and domain logic (`astra-core`, `astra-data`, `astra-providers`, `astra-clients`, `astra-plugins`).

## Entry points
| Symbol | File | Role |
|---|---|---|
| `main.tsx` (module body) | `web/src/main.tsx:29` | `createRoot` with the provider stack QueryClient → `I18nProvider` → `AppearanceProvider` → `MotionConfig` → `FeedbackProvider`; also imports `arc/foundation.css` before `styles/index.css`. |
| `queryClient` | `web/src/main.tsx:19` | App-wide react-query defaults: `staleTime: 5_000`, one retry for network/5xx only (never for a 4xx `ApiError`). |
| `App` | `web/src/App.tsx:32` | Chooses `HashRouter` (desktop, `app://` has no deep-link fallback) vs `BrowserRouter`, and routes `/key` outside the admin tree. |
| `AdminApp` | `web/src/App.tsx:50` | The admin route table: `/tray` outside the shell, every other page inside `<AppShell />`; `*` redirects to `/`. |
| `AuthGate` | `web/src/App.tsx:83` | Wraps every admin route; renders `LoginPage` until `/api/auth/status` says `required && !signedIn`. |
| `AppShell` | `web/src/components/layout/AppShell.tsx:75` | Sidebar (expanded / rail / drawer), content area, `ConnectionBanner`, command handling, tray title reporting. |
| `Page` | `web/src/components/layout/Page.tsx:15` | The per-page floating toolbar + scrolling content every page renders into. |
| `api<T>` | `web/src/api/client.ts:30` | The only fetch helper for the admin API; adds the header and parses the `{error}` envelope. |

## Key types and functions
| Symbol | File | Role |
|---|---|---|
| `ApiError` / `apiBase()` | `web/src/api/client.ts:3`, `:23` | `status`/`details` on failures (`isNetwork` = `status === 0`, what the retry policy keys on); the origin is `''` in dev (same-origin via the Vite proxy) and the bridge's `apiBase` otherwise — never build an absolute URL by hand. |
| `qs` / `enc` / `createSseParser` | `web/src/api/client.ts:66`, `:76`, `:88` | Query building (drops empty values), path encoding, and the WHATWG frame splitter. |
| `apiStream` / `apiEventStream` | `web/src/api/client.ts:126`, `:139` | The two SSE consumers — provider tests and the live request log; a failure before the stream starts arrives as the same `{error}` JSON. |
| `keys` + the endpoint hooks | `web/src/api/hooks.ts:93` (and the whole file) | One registry of react-query keys plus 145 exports, mostly one-liner hooks, one per endpoint, each owning its path, method and invalidation set (`useProviders` `:354`, `useClients` `:829`, `useTokens` `:996`, `runProviderTest` `:496`). |
| `LiveRequestStore` | `web/src/api/liveRequests.ts:18` | Out-of-React store of the in-flight request rows with per-row subscriptions; batches list rebuilds into a microtask. |
| `useLiveRequestFeed` / `useLiveRequestList` / `useLiveRequest` | `web/src/api/liveRequests.ts:199`, `:234`, `:240` | Reference-counted SSE connection with backoff and a hidden-tab grace, the list view, and a single-row view. |
| API contract types | `web/src/api/types.ts` | Hand-mirrored server contract (`RequestSummary`/`RequestDetail` `:454`/`:484`, `Provider` `:129`, `ClientInfo` `:289`, `Token` `:411`, `Settings` `:725`, `KeyInfo` `:944`); statement of intent at `:1`. |
| `I18nProvider` / `useI18n` / `useT` | `web/src/i18n/index.tsx:40`, `:58`, `:64` | Locale context; `translate` `:26` and `format` `:21` do the `{name}` substitution. |
| `zh` / `MessageKey` / `en` | `web/src/i18n/messages.ts:3`, `:1852`, `:1854` | `zh` defines the key set, `en` is typed `Record<MessageKey, string>`. |
| `components/arc/**` (40 folders) + `components/ui/*` | `web/src/components/arc/`, `web/src/components/ui/` | Presentation layers, not pages: the vendored "uiarc" kit (one folder per component with `.module.css` + `.tsx`: button, dialog, tabs, sortable-data-table, activity-heatmap …; tokens in `arc/foundation.css`, motion in `arc/lib/motion-tokens.ts`) and Astra's own `controls.tsx`/`overlays.tsx` built on top (`Button`/`Input`/`Row`/`Group`/`Badge`/`KV`/`CodeBlock`/`Spinner`/`EmptyState` `:44`–`:398`; `Sheet`/`Menu`/`Select`/`FeedbackProvider`/`useFeedback`/`errorText` `:17`–`:250`). |
| `components/layout/*`, `components/tray/*`, `components/*.tsx` | `web/src/components/` | The shell (`AppShell`, `Page`, `nav.tsx`, `ConnectionBanner`); the tray popover stack (`TrayPanelView` `tray/TrayPanelView.tsx:77`, `TraySettings` `:20`, `useTrayBridge` `useTrayBridge.ts:22`, `useTrayData` `useTrayData.ts:45` — only the menu-bar title's queries keep polling while hidden; `titleOnly` for the main window, `useTrayTitle` `TrayTitleSource.tsx:16`); and the page-sized feature panels split out of `pages/` (`ProviderQuotaSection`, `SubscriptionAccounts*`, `PluginAccounts`, `Privacy*`, `ClientOptionsGroup`, `PricingEditor`, `Billing`, `ProviderTestDialog`, `RequestFilters`, `icons.tsx`). |
| `DesktopBridge` / `getBridge` / `shell` / `isDesktop` | `web/src/shell/bridge.ts:53`, `:96`, `:120`, `:122` | The preload surface, and the module-level shell detection the router and every layout branch read. |
| `useCommands` / `commandForKey` / `emitCommand` | `web/src/shell/commands.ts:48`, `:11`, `:73` | One subscription point for keyboard accelerators and native menu commands, plus a page-scoped bus. |
| `systemActions` | `web/src/shell/systemActions.ts:7` | Host integration that degrades: open folder / reveal logs / native context menu / start service are `null`/`false` without the bridge, clipboard works in both. |
| `useAppearance` / `applyInitialTheme` | `web/src/shell/appearance.tsx:157`, `:84` | Theme + accent (system accent comes from the bridge on desktop, CSS `AccentColor` in the browser). |

## Dependencies
- Uses: `astra-server` via HTTP — `api()` (`web/src/api/client.ts:30`) against `/api/**` for every hook in
  `web/src/api/hooks.ts`, SSE at `web/src/api/liveRequests.ts:167`.
- Uses: `astra-gateway` via HTTP — `fetchKeyInfo` (`web/src/api/hooks.ts:1352`) GETs `/v1/astra/key` with the
  user's key as a Bearer token; the console only ever *prints* the `/v1` and `/key` addresses
  (`web/src/pages/TokensPage.tsx:51`, `:52`).
- Uses: `desktop-app` via IPC (preload bridge) — `getBridge()` in `web/src/shell/bridge.ts:96`, consumed by
  `web/src/shell/appearance.tsx:91`, `web/src/shell/commands.ts:61`, `web/src/shell/systemActions.ts:12`,
  `web/src/components/tray/useTrayBridge.ts:22`, `web/src/components/layout/ConnectionBanner.tsx:31`.
- Used by: `astra-server` via static files — `web/dist` is packed next to the binary as `wwwroot` and served
  by `UseStaticFiles` + the SPA fallback (`src/Astra.Server/Hosting/AstraApp.cs:47`, `:289`).
- Used by: `desktop-app` via build artifact + `window.__ASTRA__` — `web/dist` is copied into
  `desktop/renderer`; the bridge interface here mirrors `desktop/src/preload.ts` (comment at
  `web/src/shell/bridge.ts:52`) and the tray types mirror `desktop/src/shared/prefs.ts` (`:23`).

## State it owns
Nothing server-side: all persistence belongs to astra-server (`~/.astra/**`, SQLite). Everything here is
browser-local or in-memory, and none of it is authoritative:
- `localStorage`: `astra.locale` (`web/src/i18n/index.tsx:6`), `astra.theme` + `astra.accent`
  (`web/src/shell/appearance.tsx:18`–`:19`, read before React mounts by `applyInitialTheme` `:84`),
  `astra.sidebar` (`web/src/components/layout/AppShell.tsx:31`).
- `sessionStorage`: `astra.keyLookup` — the self-service token, deliberately per-tab
  (`web/src/pages/KeyPage.tsx:12`).
- The react-query cache (`web/src/main.tsx:19`): server data only, keyed through `keys`
  (`web/src/api/hooks.ts:93`); write it through the hooks' `onSuccess`/`setQueryData`, never directly.
- `LiveRequestStore` singleton (`web/src/api/liveRequests.ts:98`): in-flight rows, dropped on every feed
  disconnect (`:58`) because the "persisted" event that would evict them may be lost.
- Tray preferences are owned by the desktop main process (`desktop-prefs.json`) and pushed over the bridge;
  without a bridge the web build runs on `DEFAULT_TRAY_PREFS` (`web/src/components/tray/useTrayBridge.ts:7`)
  and derives service state from `useHealth` (`:55`).

## Contracts it exposes
- The admin HTTP surface it consumes, typed by hand in `web/src/api/types.ts` (statement of intent at `:1`–`:5`):
  `/api/models*`, `/api/providers*`, `/api/provider-accounts/*`, `/api/clients/*`, `/api/tokens/*`,
  `/api/requests*`, `/api/stats/*`, `/api/privacy*`, `/api/plugins*`, `/api/webhooks*`, `/api/alerts/*`,
  `/api/settings`, `/api/tunnel*`, `/api/update/*`, `/api/auth/*`, `/api/health`, `/api/version`.
  Streams: `GET /api/requests/live` (`web/src/api/liveRequests.ts:167`),
  `POST /api/providers/{id}/test` (`web/src/api/hooks.ts:496`). Gateway: `GET /v1/astra/key` (`:1352`).
- The bridge it *requires* from the host (declared at `web/src/shell/bridge.ts:53`): `apiBase`, `shell`,
  `platform`, `openPath`, `revealLogs`, `startService`, `restartService`, `setThemeSource`, `getAccentColor`,
  `onAccentColorChanged`, `showContextMenu`, `onMenuCommand`, `updates.*`, and the whole `tray.*` group.
- The URL contract (also a deep-link contract) — `web/src/App.tsx:36`–`:74`: `/`, `/requests`, `/traces`, `/traces/sessions[/:key]`, `/traces/requests[/:id]`, `/tokens`,
  `/clients[/:kind]`, `/providers[/:id]`, `/models[/:id]`, `/plugins[/:tab]`, `/privacy[/:tab]`,
  `/settings[/:tab]`, `/tray`, plus `/key` outside the gate. The docs site's screenshots and the tray
  window both depend on these paths.
- i18n keys, not strings: every user-visible string lives in `web/src/i18n/messages.ts` (`zh` `:3`,
  `en` `:1854`, `MessageKey` `:1852`); `MenuCommand`/`NavTarget` ids (`web/src/shell/bridge.ts:5`–`:13`) are
  a second, non-i18n name contract shared with the desktop menus.

## How to extend
- To add a page: write `web/src/pages/XPage.tsx` exporting `XPage`, add a `lazy()` import in
  `web/src/App.tsx:13` (or eager, only if it must be in the entry chunk — see the comment at `:11`), add the
  `<Route>` inside `AdminApp` (`:58`), and add a `NavItem` to `NAV_GROUPS` (`web/src/components/layout/nav.tsx:17`).
  Missing the nav entry still routes by URL but is unreachable from the sidebar, missing from `NAV_PATHS`
  (`:41`) and `nav:<id>` commands navigate to `undefined`, and a new `NavTarget` also belongs in
  `web/src/shell/bridge.ts:5`.
- To consume a new admin endpoint: add the DTO to `web/src/api/types.ts`, add the hook + key to
  `web/src/api/hooks.ts:93`, wire the UI, then add zh **and** en keys. Missing the invalidation in the
  mutation's `onSuccess` leaves stale data on the page that wrote it (the `invalidateProviders` pattern,
  `web/src/api/hooks.ts:364`).
- Provider credential usage: `useProviderUsage` polls GET `/api/providers/{id}/usage` every five seconds while mounted;
  `AccountUsageStats` shows each credential's Today/Total request count, input+output tokens and saved model cost on
  subscription cards/details, plugin account rows and the ordinary API-key tab. Zero usage is explicit; unavailable
  data stays `—`. `ProviderUsageHint` explains subscription equivalents and the server's usage time zone.
- To add a UI string: add it to `zh` (`web/src/i18n/messages.ts:3`) and `en` (`:1854`). `en` is
  `Record<MessageKey, string>`, so a zh-only key fails the typecheck in the web build; `web/src/test/i18n.test.ts:9`
  additionally pins key-set and `{placeholder}` parity — a mismatch only surfaces when that suite runs.
- To add a per-client option control: nothing per-option is written in the UI — the panel renders from the
  descriptors the server reports (`ClientOptionDescriptor`, `web/src/api/types.ts:314`, rendered by
  `web/src/components/ClientOptionsGroup.tsx`).
- To add tray behaviour: change `TRAY_TITLE_MODES` / `TRAY_SECTIONS` (`web/src/shell/bridge.ts:24`, `:27`) on
  both sides (they mirror `desktop/src/shared/prefs.ts`), then the calculation in `web/src/lib/tray.ts` and
  the block in `web/src/components/tray/TrayPanelView.tsx:77`. Missing the desktop half makes the new value
  arrive as an unknown id and fall back silently.
- To add a bridge capability: implement it in `desktop/src/preload.ts` + `desktop/src/bridge.d.ts` + the main
  process first, then extend `DesktopBridge` here (`web/src/shell/bridge.ts:53`). Call sites must stay
  optional-chained (`getBridge()?.x`) or the web build breaks in a plain browser.

## Invariants and traps
- Every mutating `/api` call must send `X-Astra-Admin: 1`; `api()` adds it for any non-GET (`web/src/api/client.ts:32`)
  and `readStream` for its POST form (`:162`). Hand-rolled `fetch` against `/api` therefore 403s from
  `SecurityMiddleware`. The one deliberate exception in this package is `fetchKeyInfo`
  (`web/src/api/hooks.ts:1352`) — that path is a gateway endpoint and authenticates with the token itself.
- The error envelope is `{ error, details? }` (`web/src/api/client.ts:59`); `errorText` (`web/src/components/ui/overlays.tsx:250`)
  is the display path. A `204` returns `undefined`, so hooks typed `api<void>` are the only ones that may be `204`.
- Money is integer nano-USD everywhere on the wire and the UI only divides for display
  (`web/src/lib/format.ts:3`) — never send a rounded/decimal amount back.
- `PricingSchedule` is the exception to camelCase: it is the **storage** shape (snake_case, token types as
  keys) and is passed verbatim because users edit it as raw JSON (`web/src/api/types.ts:2`, `:43`).
- The self-service page is outside `AuthGate` on purpose (`web/src/App.tsx:37` vs `:52`) and must not touch
  `/api`; it talks to `/v1/astra/key` with the pasted key and keeps it in `sessionStorage`
  (`web/src/pages/KeyPage.tsx:12`, contract note `:33`).
- Routing is `HashRouter` in the desktop shell because `app://` has no server-side deep-link fallback
  (`web/src/App.tsx:29`); any externally produced link must match. The server routes unknown non-`/api`,
  non-`/v1` paths to `index.html` (`src/Astra.Server/Hosting/AstraApp.cs:289`).
- Pages are lazy by default (`web/src/App.tsx:13`); making one eager pulls it into the entry chunk and into
  the tray panel's hidden window, which is why only the overview and the login page ship eagerly (`:11`).
- `web/tsconfig.json:21` turns `noUncheckedIndexedAccess` **off** while `tsconfig.base.json` sets it on for
  `cli/`, `desktop/` and `packages/*` (AGENTS.md "Conventions"). Code copied from `web/` to those packages
  needs guards/non-null assertions (`web/src/components/icons.tsx:203` shows the web-side style); code copied
  the other way compiles here and would not there.
- The live log exists only while a stream is open (`web/src/api/liveRequests.ts:110`): the server records
  live rows only for connected clients, and the tab deliberately drops the stream after a 10 s hidden grace
  (`:103`).
- Shapes in `web/src/api/types.ts` are mirrored by hand from the .NET DTOs; an added server field is invisible
  here until someone extends the interface (and only the endpoints listed in `types.ts`'s trailing comments
  document their method/status behaviour).
- The dev server proxies only `/api` (`web/vite.config.ts:20`, target `ASTRA_DEV_SERVER`, default
  `http://127.0.0.1:17321`) — a UI change that needs `/v1` must be exercised against the real origin.
- `vite.config.ts:13` sets an absolute `base: '/'` because assets must resolve for deep links like
  `/providers/x`; a relative-base change breaks every non-root route in the browser build.

## Page inventory
Every page renders into `Page` (`web/src/components/layout/Page.tsx:15`) unless noted; routes are in
`web/src/App.tsx:58`–`:74`.
| Page | File | What it is for |
|---|---|---|
| `OverviewPage` | `web/src/pages/OverviewPage.tsx:59` | Dashboard: range stats, request/cost charts, top models, activity heatmap, gateway `/v1` address to copy. |
| `RequestsPage` | `web/src/pages/RequestsPage.tsx:64` | The request log — filters (kept in the address under the router: `?token=&provider=&client=&status=&origin=&model=`), live rows from the stream, RPM/TPM header, and `RequestDetailSheet` as the drawer. Each row links to its trace. |
| `TracesPage` | `web/src/pages/TracesPage.tsx:TracesPage` | 链路追踪: topology, session and request canvases (`web/src/components/traces/`, laid out by `model.ts` `SPAN`), fed by `useTraceTopology` / `useTraceSessions` / `useTraceSession` / `useTraceRequest`. |
| `TokensPage` | `web/src/pages/TokensPage.tsx:28` | Gateway tokens: create/enable/reveal/reset/delete, the provider pool editor (`web/src/components/TokenPoolEditor.tsx`), per-token budgets, self-service addresses, links to the token detail and its filtered request log. |
| `TokenDetailPage` | `web/src/pages/TokenDetailPage.tsx` | `/tokens/:id`: one token's totals, trend, per-provider (with pool failovers) and per-model breakdown, live pool health, latest requests. |
| `ClientsPage` | `web/src/pages/ClientsPage.tsx:46` | Client take-over: enable/disable, provider bindings + routing order, install/update jobs, backups, options, Claude Code direct panel. |
| `ProvidersPage` | `web/src/pages/ProvidersPage.tsx:49` | Provider list with quota lines, create-from-template stepper, import from other apps, reorder. |
| `ProviderDetailPage` | `web/src/pages/ProviderDetailPage.tsx:47` | One provider: endpoints/auth/headers, model list + overrides + model map, pricing, quota, accounts, plugin accounts, connection test. |
| `ModelsPage` | `web/src/pages/ModelsPage.tsx:21` | System model catalog with search/vendor filter, create, and the models.dev sync preview/apply. |
| `ModelDetailPage` | `web/src/pages/ModelDetailPage.tsx:42` | One model: fields, capabilities, seed-reset, per-price-key prices, billing simulator. |
| `PluginsPage` | `web/src/pages/PluginsPage.tsx:77` | Installed plugins / market / Bun runtime tabs: install, enable, options, reorder, sign-in accounts, magpie import. |
| `PrivacyPage` | `web/src/pages/PrivacyPage.tsx:14` | Privacy guard tabs: overview stats, interception logs, rules, dry-run tester. |
| `SettingsPage` | `web/src/pages/SettingsPage.tsx:32` | General (retention, limits, proxy, locale, update), notifications, remote access, tray panel (desktop only). |
| `LoginPage` | `web/src/pages/LoginPage.tsx:11` | The password form `AuthGate` shows; says why when a tunnel keeps the admin UI local. |
| `KeyPage` | `web/src/pages/KeyPage.tsx:38` | `/key` outside the shell and outside `AuthGate`: token self-service against `/v1/astra/key` (limits and usage), `/v1/astra/key/stats` and `/v1/astra/key/requests` (statistics and request log tabs). |
| `TrayPanelPage` | `web/src/pages/TrayPanelPage.tsx:23` | `/tray`, the desktop popover window: transparent background, content height reporting, menu-bar title. |

## Tests
`web/src/test/**` — vitest with jsdom + testing-library, wired in `web/vite.config.ts:30`–`:34`
(`environment: 'jsdom'`, `setupFiles: ['./src/test/setup.ts']`, `css: false`); the setup file only imports
`@testing-library/jest-dom/vitest`. Naming convention: `<topic>.test.ts` for pure helpers
(`lib/` `format`, `stats`, `usage`, `client-filter`, i18n, `shell`), `<feature>.test.tsx` for rendered
components/pages with the API mocked at the `../api/hooks` boundary via `vi.mock` + `vi.hoisted`
(`web/src/test/components.test.tsx:23`, `web/src/test/privacy-settings.test.tsx:40`). Pages that render need
`I18nProvider` (+ `FeedbackProvider` when they toast). Run all: `pnpm --filter @aidotnet/web test`; one file:
`pnpm --filter @aidotnet/web test src/test/i18n.test.ts`; type/lint gates are
`pnpm --filter @aidotnet/web typecheck` and `pnpm --filter @aidotnet/web lint` (the build runs `tsc --noEmit`
first — `web/package.json:9`).

## Related flows
- [admin-api-request](../flows/admin-api-request.md)
- [request-tracing](../flows/request-tracing.md)
- [client-config-takeover](../flows/client-config-takeover.md)
- [plugin-install-and-run](../flows/plugin-install-and-run.md)
- [provider-quota-refresh](../flows/provider-quota-refresh.md)
- [subscription-account-refresh](../flows/subscription-account-refresh.md)
- [token-limits-enforcement](../flows/token-limits-enforcement.md)
- [privacy-redaction](../flows/privacy-redaction.md)
- [release-and-update](../flows/release-and-update.md)
- [server-startup](../flows/server-startup.md)
