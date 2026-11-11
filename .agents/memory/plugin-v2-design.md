---
name: plugin-v2-design
description: Plugin system v2.0 is being designed in design/plugin-v2/ (draft, 2026-11-11); what the user has decided so far and what is still open
type: project
updated: 2026-11-11
source: session
evidence: ["design/plugin-v2/README.md", "design/plugin-v2/05-ui-components.md"]
---
On 2026-11-11 the user asked for a plugin system v2.0: a full redesign that does not have to be compatible with
v1 (the magpie-compatible plugins), while v1 stays supported; a plugin that has a v2 version prompts the user to
update. Plugins build UI from JS through a custom UI API that must render consistently on the web console and the
Avalonia desktop. All design goes under `design/` (not `.plan/`). No code exists yet.

**Decided by the user:** the UI vocabulary must include tree, date pickers, line charts and Markdown (decision
D10 in the README: the full component set, those four first).

**Still open (my proposals, not confirmed):** D1–D9, D11, D12 in `design/plugin-v2/README.md` — notably
server-driven UI (plugin UI code runs in the server sandbox, shells only render), sandbox-only runtime with no Bun,
and the 2.0 / 2.1 scope split.

**How to apply:** treat everything in `design/plugin-v2/` other than D10 as a draft; check the README's decision
table before implementing, and update it when the user settles one.
