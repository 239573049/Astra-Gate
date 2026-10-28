---
name: desktop-page-lifetime
description: Avalonia pages must hang handlers on shared queries through Subscribe/OnDispose; the 2026-10-27 leak, redraw storms, the heatmap text-layout hotspot and the bounded Arc Transitions retention
type: project
updated: 2026-10-27
source: session
evidence: src/Astra.Desktop/Shell/PageViewModel.cs:Subscribe, src/Astra.Desktop/Shell/Navigator.cs:Navigate, tests/Astra.Desktop.UiTests/PageLifetimeTests.cs, src/Astra.Desktop.Core/Api/Query.cs:QueryOptions.CacheTime
---
On 2026-10-27 a headless tour of all 17 desktop routes leaked ~19 MB of managed heap per tour (145 MB after 6 tours, flat 41.6 MB
after the fix): page view models subscribed to shared `Query` objects in their constructors and never unsubscribed, and the
`QueryClient` keeps every query for the process, so every visited page (and the view it holds) stayed alive and kept handling
refetches. The navigator now disposes dropped pages; `PageViewModel.Subscribe` / `OnDispose` undo the registrations.

The same probe showed the Requests page taking ~2.5 s on every fifth visit (143 gen0 GCs): each query notification (up to eight
per fetch) rebuilt the body and re-attached the ~4,300-visual table. Fixed by filtering/coalescing notifications and keeping the
children attached when unchanged; `QueryHost` coalesces the same way.

One dropped Requests / Traces instance may still survive: a `Transitions` object an Arc control theme shares through a style
setter keeps `CollectionChanged` handlers of old Buttons/Borders (heap dump `gcroot`). It stays at one per page type, so
`PageLifetimeTests` tolerates one and fails on more.

**Why:** shared queries and stores outlive every page; a plain `+=` from a page is a leak, and a rebuild per notification is a
redraw storm.
**How to apply:** in a page view model use `Subscribe(query, handler)` (or `OnDispose`) for anything not owned by the page; in a
control, subscribe in `OnAttachedToVisualTree` and unsubscribe in `OnDetachedFromVisualTree`. React to `Data` (and `HasData` /
`IsError` when shown) rather than every property, or coalesce. Give per-id queries with large answers a `CacheTime`.
In a custom-drawn control, never lay out text inside per-cell geometry: the Overview heatmap measured seven weekday labels
for each of its 371 cells (≈8,000 text shapings per render, 92–143 MB allocated per visit); cache the width instead.
