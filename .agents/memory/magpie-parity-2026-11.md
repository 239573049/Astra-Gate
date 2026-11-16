---
name: magpie-parity-2026-11
description: 2026-11-16 user asked for magpie feature parity in 13 areas; scope decisions (rules-only privacy, all 62 agents, manual FX only)
type: project
updated: 2026-11-16
source: session
evidence: magpie cb4ad12 (github.com/yetone/magpie) vs Astra 05af4338; branch feat/magpie-parity
---
On 2026-11-16 the user asked to close the functional gaps against magpie (cb4ad12): quota-aware routing and named
routing groups, multiple API keys per provider, privacy-guard improvements, more gateway endpoints, all 62 agents
magpie supports and we don't, profiles + per-agent model shortlists, the rest of the resource library, built-in
tools (web search, image MCP …), a re-vendored magpie host.js, full backup + sync, more currencies and per-request
context-window fill, driving the real `claude` binary for the Claude subscription plus 5-hour window warm-up, and
more built-in provider presets. Work happens on `feat/magpie-parity`, batched in parallel worktrees.

Scope decisions taken with the user:
- Privacy: rules only this round (stable placeholders, checksums, more categories, tool-argument restore,
  fail-closed). The local NER model (~1.6 GB + ONNX Runtime) is deferred.
- Agents: all 62 missing agents, not a first batch.
- Currency: manual exchange rates only — Astra makes no network request for FX rates.
- Subagents for this work run on Sonnet (user request).

**Why:** the user wants parity with magpie as a product decision; the deferrals above were explicit choices.
**How to apply:** don't add an FX feed or a NER model without asking again; treat magpie's source as the reference
for agent config formats (MIT), but verify against each agent's own docs where possible.
