---
name: jaunt-replaces-jint
description: Plugin middleware/agents moved from Jint to Jaunt (user request 2026-11-11); its limit faults, tail calls and lifetime memory count behave unlike Jint's
type: project
updated: 2026-11-11
source: session
evidence: ["src/Astra.Plugins/Js/JsRuntime.cs:JsRuntime", "src/Astra.Plugins/Middleware/MiddlewareEngine.cs:MiddlewareRun.Apply", "https://github.com/AIDotNet/Jaunt"]
---
On 2026-11-11 the user asked to replace the built-in JS engine (Jint 4.17) with Jaunt (`AIDotNet/Jaunt`, NuGet
`Jaunt` 1.0.2), a managed NativeAOT-clean engine. That also removed the only ILC warning exception
(`--nowarnaserr` IL2026/IL2104/IL3053 for Jint); `check-aot-warnings.mjs` is now zero-tolerance.

Differences from Jint that shaped the port (each checked against Jaunt 1.0.2 in a scratch program):
- A limit hit (timeout, stack depth, tracked bytes, cancellation) **faults the engine for good**; a JS throw does
  not. Owners drop a faulted runtime: `MiddlewareRun.Apply` frees it so the next hook gets a fresh engine,
  `AgentPluginCatalog.LoadedAgent.Ready` reloads the module.
- Strict code (all ES modules) has **proper tail calls**: `return f(n + 1)` recursion never hits `MaxStackDepth`; it
  loops until the deadline. The self-test's recursion check uses `1 + deep(n + 1)` for that reason.
- `MaxTrackedBytes` counts allocations over the engine's whole life and never goes down (2 MB after 1000 1 KB
  JSON parses), so pooled engines are retired past 128 MiB (`JsRuntime.Worn`) well below the 512 MiB limit.
- The host API cannot read/set object properties or create functions outside a native callback; the `Bridge`
  script in `JsRuntime` hands helpers to the host through one native global that it then deletes.
- Module resolver exceptions come back wrapped in a `JsEngineFaultedException`; `JsRuntime.Load` unwraps them.

**Why:** User decision; Jaunt's selling points are AOT cleanliness and speed (the self-test measured ~5 µs per
1 KB event, ~0.6 ms per 78 KB request under the JIT).
**How to apply:** Keep host interop inside `JsRuntime`; never hold a `JsValue` across a fault; when bumping Jaunt,
re-run `astra-server plugin-selftest` on an AOT publish and recheck the three behaviours above.
