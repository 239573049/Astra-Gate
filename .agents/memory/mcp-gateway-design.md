---
name: mcp-gateway-design
description: Issue #29 MCP gateway — decisions taken (remote upstreams only, OAuth in phase 1, no CIMD, no legacy HTTP+SSE front) and why the official C# SDK serves the upstream side
type: project
updated: 2026-11-10
source: session
evidence: ["https://github.com/239573049/Astra-Gate/issues/29", ".plan/mcp-gateway.md", "https://github.com/microsoft/mcp-gateway", "https://modelcontextprotocol.io/specification/2026-07-28/basic/versioning"]
---
Design draft only (no code yet) in `.plan/mcp-gateway.md` (gitignored). Decided by the user on 2026-11-10: upstreams are remote
HTTP/SSE only (no stdio), tokens bind to servers through one `tokens.mcp_json` column (no model binding), OAuth upstreams are in
phase 1 (pre-registration / DCR / manual; **no CIMD**), the front end does **not** serve the deprecated HTTP+SSE transport, `/mcp/{slug}`
is a second, unprefixed view of one server, and tool calls are recorded in their own `mcp_calls` table (not the request log or traces, so linked update 15 is untouched).

**Why:** MCP has two eras — legacy (`initialize` + sessions, up to 2025-11-25) and modern (stateless, 2026-07-28). Microsoft's
gateway serves modern only and does not translate; Astra aggregates arbitrary user-chosen upstreams, most still legacy, so it must speak
both on both sides (hub-and-spoke, like the model-protocol IR). A spike (AOT console, `ModelContextProtocol.Core` 2.4.0, 0 IL warnings,
11.6 MB) showed the SDK's client already does era detection (`HttpTransportMode.AutoDetect`), DCR, PKCE, `resource` and `iss` validation,
with a pluggable `ITokenCache` — so the upstream side uses the SDK; the front end is ours (stateless, both eras, per-token filtering).
**How to apply:** keep the SDK out of `Astra.Core`. A data-plane request must never start an interactive OAuth flow: the callback handler
throws and the server becomes `needs_auth`; only an admin click opens the browser. One OAuth identity is shared by every Astra token bound
to that server — say so in the UI. Not verified yet: a full-size AOT publish with the SDK inside `Astra.Server`, SSE-mode upstreams,
refresh concurrency, and real upstreams (GitHub, Notion).
