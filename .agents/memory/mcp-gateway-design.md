---
name: mcp-gateway-design
description: Issue #29 MCP gateway — decisions taken (remote upstreams only, OAuth in phase 1, no CIMD, no legacy HTTP+SSE front) and why the official C# SDK serves the upstream side
type: project
updated: 2026-11-11
source: session
evidence: ["https://github.com/239573049/Astra-Gate/issues/29", ".plan/mcp-gateway.md", "https://github.com/microsoft/mcp-gateway", "https://modelcontextprotocol.io/specification/2026-07-28/basic/versioning"]
---
Implemented 2026-11-10/11 (backend `6adffe1`, web `1444d04`, Avalonia `49f362f`; flow page `.agents/codemap/flows/mcp-gateway.md`); the design
is in `.plan/mcp-gateway.md` (gitignored). Decided by the user on 2026-11-10: upstreams are remote
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
to that server — say so in the UI. Verified later (2026-11-10): the full AOT publish with the SDK inside `Astra.Server` works (only the 6 allowed Jint warnings, 56.8 MB) and `/mcp` was exercised on that binary. The OAuth flow was also run on the AOT binary (simulated browser, authorization server saw nothing before the admin started it). Still not verified: SSE-mode upstreams,
refresh concurrency, and real upstreams (GitHub, Notion).

**Implementation traps found (2026-11-10):** the SDK registers a client at the authorization server *before* it calls the authorization
callback, so a data-plane request to an OAuth server without tokens would register a new client every time —
`McpUpstreamPool.ClientAsync` refuses to connect at all until `mcp_oauth` has a row (`McpOAuthTests.An_Unauthorized_Server_Is_Left_Out_And_Says_Why`
asserts the authorization server saw nothing). The OAuth callback needs no open-path entry in `SecurityMiddleware` (a GET navigation; local
needs no session, others need the admin session). The `OAuth` DTO property is pinned to `oauth` with `JsonPropertyName`.
