# UI fixtures (visual regression)

Deterministic admin-API data both clients render, so "identical to the web UI" is checkable (plan avalonia-client §3 D6, §23).

```
tests/fixtures/ui/<set>/
  index.json        route table + the frozen clock: { now, timeZone, locale, routes: [{ method, path, query?, file }] }
  *.json, */*.json  one response body per file, in the exact shape the admin API returns (web/src/api/types.ts,
                    src/Astra.Api.Contracts) — camelCase, nanodollars as integers, ISO-8601 UTC timestamps
```

The set `default` has 2 providers (OpenAI, Anthropic with a balance quota), 3 tokens (default, a limited one, a pooled one),
8 models, 4 clients (enabled / drifted / not installed), 50 requests (the last ~2 h before `now`, with failures and cache hits),
stats for `today` / `7d` / `30d` (summary, top models, providers, per-provider and per-token time series, heatmap, rate),
settings, version, health, update, tunnel, listen and autostart status. `now` is `2026-10-26T12:00:00Z`, time zone UTC.
The files are generated: edit `scripts/visual/gen-fixtures.mjs` and run `node scripts/visual/gen-fixtures.mjs`
(`--check` fails when the committed files are stale). The stats are internally plausible, not recomputed from the 50 requests.

## Matching rule (identical on both sides)

A request is answered by the FIRST route with the same `method` and exact `path` whose `query` (if any) is a subset of
the request's query string; parameters not listed in `query` are ignored (so `/api/requests?page=2&model=x` returns the one
page-1 body). Dynamic ids are listed as concrete routes (`/api/tokens/tok_ci`). No match = HTTP 404 `{"error":"no fixture"}`,
and the capture prints the unmatched calls — add them to the generator. `/api/requests/live` (SSE) is answered with an empty
event stream. A scene state is the fixture plus overrides of single paths (an empty list, a 500).

## Consuming them

- **Web** — `scripts/visual/capture-web.mjs`: Playwright `page.route('**/api/**')` reads `index.json` and fulfils from the files;
  the clock is pinned with `page.clock.setFixedTime(now)`, the time zone with `timezoneId`.
- **Avalonia** — `tests/Astra.Desktop.UiTests/FixtureSet.cs`: the files are copied beside the test binaries
  (`Fixtures/ui/<set>/`); `FixtureSet.Load().Handler()` is an `HttpMessageHandler` for `new ApiClient("http://fixture", handler)`,
  `Override(path, body, status)` sets a state, `Now` is the frozen clock a page must use instead of `DateTime.Now`.
- Any other tool: it is plain JSON and a route table; no Node and no .NET is needed to read it.
