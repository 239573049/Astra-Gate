---
flow: mcp-gateway
modules: [astra-gateway, astra-core, astra-data, astra-server, web-ui]
commit: 49f362f
updated: 2026-11-11
---
# MCP gateway

**Trigger:** an MCP client posts JSON-RPC to `POST /mcp` (every MCP server its token is bound to) or `POST /mcp/{slug}` (one
server, tool names unprefixed); or an admin registers, tests or authorizes an upstream server in the console.
**Outcome:** `tools/list` and `tools/call` are answered from the upstream servers the caller's token is bound to, in the era
of the protocol the caller speaks, with nothing remembered between requests; every call is logged in `mcp_calls` (who, tool,
duration, outcome — never arguments or results). Design and decisions: `.plan/mcp-gateway.md`,
`.agents/memory/mcp-gateway-design.md`; the linked-update rules are item 25 of the root AGENTS.md.

## Steps

1. **Configuration.** `AppSettings.McpGateway` (`Enabled`, default off; `AllowedOrigins`) in `src/Astra.Core/AppSettings.cs`;
   servers are rows of `mcp_servers` (`src/Astra.Data/Migrations/0022_mcp_gateway.sql`, `McpServerRecord` in
   `src/Astra.Core/Mcp/McpServerRecord.cs`, `McpRepository`), static header values encrypted into `headers_enc` through
   `ISecretProtector`. Admin API: `src/Astra.Server/Api/McpEndpoints.cs` → `McpService`. Creating, changing, testing and
   starting an OAuth authorization are refused off this machine (`RequireLocal`, 403): they make Astra connect to an address
   the admin chose. A token reaches a server only through `tokens.mcp_json` (`PATCH /api/tokens/{id}` `mcpServers` →
   `TokenService.ReadMcpServersAsync`, read back with `TokenRecord.McpServerIds()`).
2. **Door.** `src/Astra.Gateway/Http/McpGatewayEndpoints.cs:McpGatewayEndpoints.HandleAsync` checks in this order: 404 while the
   gateway is off; 403 for a browser `Origin` that is not listed; 405 for anything but POST (no event stream, no session to
   delete); 401 without an enabled token (`Authorization: Bearer` or `x-api-key` only, never the query string); 415 / 406 for
   content type / `Accept`; 404 for a slug the token is not bound to (`McpAggregator.ViewAsync`, identical to an unknown one);
   413 above `McpLimits.MaxRequestBytes` (4 MB); 400 for unparsable JSON or a batch.
3. **Era.** A request is *modern* when `params._meta["io.modelcontextprotocol/protocolVersion"]` or `MCP-Protocol-Version` says
   `2026-07-28`; `CheckModernAsync` then requires the header to agree with `_meta` (`-32020`), `Mcp-Method` / `Mcp-Name` to
   agree with the body, and `clientCapabilities` in `_meta` (an unknown version is `-32022`). Otherwise it is *legacy*
   Streamable HTTP: `initialize` answers a version from `LegacyVersions` and issues **no** `Mcp-Session-Id`, later requests
   stand alone, notifications get 202.
4. **Methods.** `server/discover` (modern), `ping` (legacy), `tools/list`, `tools/call`; anything else is `-32601`. `Result` shapes the
   reply for the caller's era (modern adds `resultType` and the server identity in `_meta`; legacy loses `resultType`,
   `ttlMs`, `cacheScope`). The reply is always JSON with `Cache-Control: no-store`, never SSE.
5. **Aggregation.** `src/Astra.Gateway/Mcp/McpAggregator.cs`: the view is the enabled servers bound to the token. `/mcp` names
   tools `<slug>__<tool>` (`McpNames`: a slug has no underscore and is at most 32 characters, so the first `__` ends it — a
   published contract, clients put these names in allow-lists); `/mcp/{slug}` leaves them alone. `ListToolsAsync` sorts by slug
   then tool name and leaves a failing server out. `CallToolAsync` resolves the name, calls the pool, converts an upstream
   failure into an `isError` result whose text never names the upstream, and writes the `mcp_calls` row. An unknown tool and a
   tool of an unbound server are both `McpToolNotFoundException` → `-32602`.
6. **Upstream.** `src/Astra.Gateway/Mcp/McpUpstreamPool.cs`: one `McpClient` (official `ModelContextProtocol.Core` SDK) per
   server, found by server id **and** its `updated_at` stamp, so editing a server drops the connection. The SDK detects the
   era (`HttpTransportMode.AutoDetect`, `Sse` when the row says so) and keeps an initialize-based upstream's session. The tool
   list is cached for `McpLimits.ToolListTtl` (1 min) and fetched by one caller at a time; a call runs outside the gate, within
   the server's own timeout (1–600 s, default 60), and is **never retried**. `RunAsync` sorts a failure with
   `McpUpstreamErrors.Classify`, forgets a broken connection (unreachable / timeout / protocol) and writes the status back
   (`ok`, `needs_auth`, `auth_error`, `unreachable`, `version_unsupported`; skipped when nothing changed within 60 s).
7. **OAuth upstreams.** The data plane never opens a browser: its authorization callback throws `McpNeedsAuthException`, and
   `ClientAsync` does not even connect while `mcp_oauth` has no row for the server (the SDK would register a new client at
   the authorization server first). An admin's click goes through `McpOAuthService.StartAsync` (`state` kept in memory,
   single-use, 30 s to learn the address and 5 min for consent); the browser lands on `GET /api/mcp/oauth/callback`
   (`McpOAuthService.CompleteAsync`; the SDK checks `iss` before the code is spent). The token container — tokens, client
   registration, issuing authorization server — is one encrypted value in `mcp_oauth` through `McpTokenCache` (`ITokenCache`).
   One OAuth identity is shared by every token bound to the server.
8. **Log.** `mcp_calls` holds one row per call; `McpAggregator` prunes rows older than `McpLimits.CallRetentionDays` (30) on every
   500th call, not on a timer. `GET /api/mcp/calls` reads it.
9. **Consoles.** Web: `web/src/pages/McpPage.tsx` → `web/src/components/McpGateway.tsx` (`useMcp*` in `web/src/api/hooks.ts`), the
   token sheet's "MCP servers" choice in `TokensPage.tsx`. Avalonia: `src/Astra.Desktop/Pages/Mcp`,
   `Components/Mcp/McpServerSheet.cs`, `src/Astra.Desktop.Core/Mcp/McpApi.cs`.

## Sequence

```mermaid
sequenceDiagram
  participant C as MCP client
  participant E as McpGatewayEndpoints
  participant A as McpAggregator
  participant P as McpUpstreamPool
  participant U as upstream MCP server
  participant D as astra-data
  C->>E: POST /mcp (Bearer token, JSON-RPC)
  E->>E: disabled? Origin? method? token? headers vs body?
  E->>A: ViewAsync(token, slug)
  A->>D: mcp_servers bound by tokens.mcp_json
  alt tools/list
    E->>A: ListToolsAsync(view)
    A->>P: ListToolsAsync(server) per server (cached 1 min)
    P->>U: tools/list (era found by the SDK)
    A-->>E: tools sorted by slug, tool; prefixed on /mcp
  else tools/call
    E->>A: CallToolAsync(view, name, args)
    A->>P: CallToolAsync(server, tool, args)
    P->>U: tools/call (never retried)
    A->>D: mcp_calls row (no arguments, no result)
  end
  E-->>C: JSON-RPC result in the caller's era
```

## Errors and cancellation

- A client that closes the request cancels it: `OperationCanceledException` with the request token is swallowed and nothing is
  answered. A timeout of the upstream is a `timeout` call (`McpErrorCategories`), not a cancel.
- Upstream trouble reaches the caller only as an `isError` tool result (or an empty server in `tools/list`) and as the server's
  status in the console; the upstream's address never appears in either.
- A tool call whose upstream died mid-flight may already have had its effect — which is why nothing retries it.
- `mcp_calls` and status writes failing (SQLite closed during shutdown) are logged at debug level and swallowed.

## Where to hook in

- **Supported:** a new method (resources, prompts) is a case in `HandleAsync`'s switch, `Capabilities()`, a list/route pair in
  `McpAggregator` and a call on the pool; a new era is `Initialize` / `CheckModernAsync` / `Result`. Anything crossing the admin
  API moves the mirrors listed in AGENTS.md item 25 together.
- **Looks tempting but breaks:** issuing an `Mcp-Session-Id` (the gateway is stateless on purpose); accepting the token in the
  URL; retrying `tools/call`; letting the data plane run an interactive OAuth flow, or connect to an OAuth server with no
  tokens; changing the `__` separator or the slug rule; starting OAuth or creating a server from another machine; putting the
  upstream's address or an argument into an error or into `mcp_calls`; referencing `ModelContextProtocol.Core` from `Astra.Core`.
- **Not built (by decision):** the deprecated HTTP+SSE front end, request-scoped SSE (progress), resources / prompts. An
  SSE-mode *upstream* has not been run under Native AOT.

## Related
- Modules: [astra-gateway](../modules/astra-gateway.md), [astra-core](../modules/astra-core.md),
  [astra-data](../modules/astra-data.md), [astra-server](../modules/astra-server.md), [web-ui](../modules/web-ui.md)
- [admin-api-request](admin-api-request.md), [token-limits-enforcement](token-limits-enforcement.md)
