---
name: plugin-v2-design
description: Plugin system v2 (design/plugin-v2/) — 2.0 and 2.1 implemented 2026-11-11 on the proposed decisions after the user said to implement all plans; which decisions the user made vs. took as proposed
type: project
updated: 2026-11-11
source: session
evidence: ["design/plugin-v2/README.md", "src/Astra.Plugins/V2/PluginV2Host.cs", "packages/plugin-sdk/package.json"]
---
On 2026-11-11 the user asked for a plugin system v2.0: a full redesign that does not have to be compatible with
v1 (the magpie-compatible plugins), while v1 stays supported; a plugin that has a v2 version prompts the user to
update. Plugins build UI from JS through a custom UI API that must render consistently on the web console and the
Avalonia desktop. All design goes under `design/` (not `.plan/`). On 2026-11-11 the user said "开始实现所有的计划" (implement all the
plans) without answering the open decisions, so the implementation follows the README's proposals for all of them.

**Decided by the user:** the UI vocabulary must include tree, date pickers, line charts and Markdown (decision
D10 in the README: the full component set, those four first).

**Taken as proposed, never explicitly confirmed:** D1–D9, D11–D14 in `design/plugin-v2/README.md` — notably
server-driven UI (plugin UI code runs in the server sandbox, shells only render), sandbox-only runtime with no Bun,
v2 market entries in a separate `pluginsV2` array, and the 2.0 / 2.1 scope split (`ui.flows` etc. not built).

**How to apply:** if the user later overrules one of those decisions, that is a design change to existing code, not
an open question — update the README's table and the code together. 2.0 and 2.1 are built (the
user's stop condition was "all the plans": the palette, dev tools, gallery, scene comparison, `astra plugin test` and 2.1's
providers / clients / flows / streaming all went in on 2026-11-11). Not built: 01 §11's "之后" items (mcp.tools,
routing.judges, alerts.channels, plugin-to-plugin API) — the design says each needs its own design first.
