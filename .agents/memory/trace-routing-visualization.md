---
name: trace-routing-visualization
description: 链路追踪 topology shows accounts as rows + a live activity rail (option B); tool args / prompt / reply text are recorded only when the user turns it on
type: project
updated: 2026-10-30
source: session
evidence: .plan/prototypes/trace-routing-options.html (local prototype, options A/B/C)
---

On 2026-10-30 the user compared three designs for showing multi-account providers, per-attempt routing and agent
content on the 链路追踪 canvas and picked **B**: the topology canvas draws each provider's accounts/keys as rows inside
its card (edges land on the row that served the attempt, a failed attempt bounces back to the hub and the retry goes to
the next row/provider), and a live activity rail beside it lists each request — its cause (user prompt or which tool
results), the route it took (failed accounts with status code and switch reason), and what came back (thinking,
text, each tool call with its arguments). Rejected: A (accounts as separate nodes + bubbles on the canvas — crowded,
arguments truncated), C (per-turn sequence diagram — a later candidate for the session view, not the topology).

Content recording (tool-call arguments, the user prompt and the reply text) is **off by default** and turned on in
settings; tool names, counts, timings and attempts are always recorded.

**Why:** the user wants to see which account served a call and watch retries/switches as they happen, and to read tool
calls in full; they chose the privacy-conservative default for anything that copies conversation content into the log.

**How to apply:** keep content capture behind the setting (never default it on); keep the canvas for routing and the
rail for content. Related: [[smart-routing-decisions]].
