---
name: builtin-tools-design
description: Magpie-parity #8 built-in tools: search for models through InternalCall rounds, the built-in MCP server as a real mcp_servers row, SSRF-guarded web_fetch, and what was deliberately left out
type: project
updated: 2026-11-16
source: session
evidence: ["src/Astra.Gateway/Tools/WebSearchBridge.cs", "src/Astra.Gateway/Mcp/McpBuiltIn.cs", "src/Astra.Gateway/Tools/WebFetcher.cs", "AGENTS.md linked update 34"]
---
Search on behalf of a model that cannot search is a bridge in front of `GatewayPipeline.HandleAsync` (`WebSearchBridge`), not a change inside it:
each round is an `InternalCall` (as the Responses socket and the Ollama face do), so token caps, failover, the log and billing stay the HTTP
route's own, at the price of one request row per round and **streaming clients getting the finished reply as events** (the rounds cannot be streamed;
the answer arrives whole). Decided against streaming pass-through because parsing four protocols' SSE for tool calls was far larger than the feature.

**Why:** the search query is the model's text restored by the pipeline (originals back in), so the bridge must run the privacy guard on the query itself
before a search API sees it (`PrivacyGuardService.Inspect`) — a test pins it (`GatewayWebSearchTests`).
**How to apply:** the built-in MCP server is a normal `mcp_servers` row (`builtin-astra`) answered by `IMcpBuiltIn` in `McpAggregator`, so token binding and
"Apply to clients" needed no new code; never give `generate_image` a caller-named output path (the server would write arbitrary files) — the CLI saves
the image itself. A search API's redirect is never followed (Go's client keeps Brave's/Exa's key headers across a redirect; .NET's would drop only
`Authorization`).
**Left out on purpose:** browser MCP / extension (magpie `browsermcp`), video generation (the images route has no video call), Tavily usage in the
Avalonia page, drag reordering in Avalonia (arrows only), search APIs that sign in as a provider (`PluginSearchDef.Auth`), mixing several APIs at once
(magpie's `Mix`), a search request-log row per search (a `gateway.search` span instead).
