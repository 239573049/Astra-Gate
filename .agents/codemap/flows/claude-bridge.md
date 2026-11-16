---
flow: claude-bridge
modules: [astra-gateway, astra-core, astra-server, web-ui]
commit: fe698814
updated: 2026-11-16
---
# Claude bridge (the Claude subscription through the local `claude` program) and window warm-up

**Trigger:** a gateway request is routed to a Claude subscription account whose kind is `claude_bridge`; or the warm-up worker's
minute tick finds an account due. **Outcome:** the request is answered by `claude -p` (stream-json in and out) with the caller's tools
bridged over MCP, as an Anthropic reply the pipeline already knows how to read; a warm-up sends the cheapest request that starts a
5-hour window. Rules: item 36 of the root AGENTS.md; decisions and what is unverified: `.agents/memory/claude-bridge-decisions.md`.
Reference implementation: magpie's `docs/subsystems/claude-subscription-bridge.md` (MIT).

## Steps (bridge)

1. **Account.** `POST /api/claude-bridge/accounts` (`ClaudeBridgeAdmin.AddAccountAsync`, local-origin only) inserts a `provider_accounts`
   row of kind `claude_bridge` on the Claude subscription provider (`SubscriptionSupport.IsClaudeSubscription`): no token, `extra.claude_bridge.config_dir`,
   optional `http_proxy` (http/https only). `claude auth login --claudeai` for it is `ClaudeBridgeAdmin.StartLoginAsync` → `ClaudeLogin`.
2. **Selection.** `GatewayPipeline.SendAsync` picks the account as for any subscription (pinned / current / failover / balanced). The provider's
   `client_policy` is checked first (a Claude provider defaults to Claude Code only: the panel says so).
   `UpstreamAuthResolver.ResolveAsync` returns `UpstreamAuth.None with { Bridge = ClaudeTarget(configDir, proxy, accountId) }` for it; the request is built as
   for the API (translation to the Messages wire already happened) and sent on `_bridgeClient` = `ClaudeBridgeHandler`, with `ClaudeBridgeHandler.TargetOption`.
3. **Request read.** `ClaudeBridgeHandler.SendAsync` → `ClaudeRequest.Parse` (system minus the billing line, tools under `ClaudeNames.BridgeName`, `output_config.effort` / thinking,
   `tool_choice` none drops the tools, per-message `Digests`).
4. **Run.** `ClaudeBridgeService.AcquireAsync`: `TryMatch` finds the run that answered this conversation so far — idle (told only `RenderSince`) or parked
   (its pending tool calls get the request's `tool_result`s by `ClaudeRun.Deliver`) — else `StartAsync` takes room (`MaxRuns`, the idle-longest run is let go), writes
   `tools.json` and `callback` (0600) into a temp dir, starts `claude` (`LaunchArguments`, `ClaudeProcess.ApplyEnvironment`) with `--mcp-config` naming this binary's
   `claude-mcp-helper`, and is told the whole conversation (`RenderWhole`).
5. **Reply.** `ReadReplyAsync` forwards the program's `stream_event`s (tool names mapped back to the caller's), keeps the reply's words and call ids, and ends at `message_stop`:
   `stop_reason: tool_use` parks the run (digest of the reply appended), anything else waits up to 5 s for the turn's `result` and the run goes idle. A failed `result`, a synthetic
   `<synthetic>` assistant message or an exit before the reply becomes `ClaudeBridgeException` (429 with the `rate_limit_event`'s reset header, 401 not signed in, 5xx, 502) that the
   handler turns into the error response the failover already classifies. `ClaudeMessageBuilder` assembles the whole message for a non-streaming request.
6. **Tool call.** `claude` calls the MCP tool; `ClaudeMcpHelper` posts `{tool_call_id (claudecode/toolUseId), name, arguments}` to `ClaudeCallbackServer` (loopback, `/<token>`),
   which waits on `ClaudeRun.WaitForResultAsync(id)` until the caller's next request delivers it (or the run ends: an error result).
7. **Ending.** A client that leaves mid-reply ends its run (`AnswerAsync`'s `finally`); `MaintainAsync` ends idle runs past `IdleMinutes` and parked ones past 30 min;
   `DisposeAsync` ends them all (SIGTERM first, so `claude` removes its lock files).

## Steps (warm-up)

`WarmupWorker` (every minute, 20 s after start) → `WarmupService.RunDueAsync`: global switch, family, per-account mode (`ModeOf` / `PlanOf`), `WarmupSchedule.DailyDue` (slot within
`Grace`, nothing warmed since) or `AutoDue` (the quota snapshot's `session.resetsAtUtc` passed), `SkipReason` (provider / account off, not active, cooling, OAuth token not fresh),
then `IWarmupSender`; the outcome is kept in `extra.warmup.last` and the request is in the log as `ClientKind = warmup`.

## Files

`src/Astra.Gateway/ClaudeBridge/*` (`ClaudeBridgeService`, `ClaudeBridgeHandler`, `ClaudeRun`, `ClaudePrompt`, `ClaudeNames`, `ClaudeMcpHelper`, `ClaudeCallbackServer`, `ClaudeCodeLocator`,
`ClaudeProcess`, `ClaudeLogin`); `src/Astra.Server/Api/ClaudeBridgeAdmin.cs`, `ClaudeBridgeEndpoints.cs`, `WarmupService.cs`, `WarmupSchedule.cs`, `ProviderProbe.WarmAsync`;
`src/Astra.Core/AppSettings.cs` (`ClaudeBridgeSettings`, `WarmupSettings`), `AccountKinds.ClaudeBridge`; web `ClaudeBridgePanel.tsx`, `WarmupSettings.tsx`.
