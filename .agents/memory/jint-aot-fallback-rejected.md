---
name: jint-aot-fallback-rejected
description: Jint passed its AOT gate, so the Bun-process fallback for plugin middleware was never built — don't reintroduce it as an interface
type: project
updated: 2026-10-14
source: init
evidence: [".plan/plugins.md", "src/Astra.Plugins/Js/JsRuntime.cs"]
---
Plugin gateway middleware and agent plugins run in-process on Jint. The plan that introduced them
(`.plan/plugins.md`, Phase 0) named the failure path explicitly: if the AOT self-test failed at run
time, middleware and an agent's `connect` would move to a second Bun process (`Host/astra-mw.js`, one
Worker per plugin) behind an `IMiddlewareRuntime` interface that Phase 2 was supposed to introduce first.

**Why:** The gate passed. `IMiddlewareRuntime` and `astra-mw.js` do not exist in the tree, and the only
sanctioned concession is the three Jint IL codes the ILC step ignores (see the Native AOT section of
AGENTS.md). The plan's wording was that a warning-only failure had to go back to the user rather than
be widened on the spot.
**How to apply:** When Jint or the plugin runtime misbehaves under AOT, fix it inside the in-process
runtime or report it. Don't add the Bun middleware host, and don't introduce `IMiddlewareRuntime` "so
the fallback is possible" — that abstraction was the fallback's own scaffolding, not a design goal.
