---
name: claude-bridge-decisions
description: 2026-11-16 magpie-parity #12 — the Claude subscription through the local claude program is an opt-in ACCOUNT kind in `claude -p` stream-json mode; what was ported from magpie, what was left out, and what was never run against a real claude
type: project
updated: 2026-11-16
source: session
evidence: ["src/Astra.Gateway/ClaudeBridge/ClaudeBridgeService.cs", "src/Astra.Server/Api/WarmupService.cs", "tests/Astra.Server.IntegrationTests/ClaudeBridgeTests.cs"]
---
The user asked for "完全一样的机制" as magpie's Claude subscription bridge plus a 5-hour-window warm-up. Decisions (made without the user on 2026-11-16, option chosen in bold):

- **Where it lives:** an account kind (`claude_bridge`) on the existing Claude subscription provider, not a new template or auth scheme — accounts already carry routing, pools,
  failover, per-account proxy and warm-up, and the OAuth accounts / direct mode / mimicry stay as they were ([[claude-code-identity-plugin]]).
- **Seam:** an `HttpMessageHandler` (`ClaudeBridgeHandler`) that the pipeline's `SendAsync` uses for such an account and that returns an Anthropic reply, so no codec or logging code knows about the bridge.
- **Mode:** `claude -p --input-format/--output-format stream-json` only. magpie's default is the interactive mode in a pty, with a Bun preload "tap" for streaming (`claudetui`, `conptypair`,
  ~7k lines), because Anthropic counts `-p` (`sdk-cli`) at about twice the plan allowance of the interactive `cli` entrypoint (anthropics/claude-code#97074). That cost is **not** avoided here and the
  panel says so. Do not set `CLAUDE_CODE_ENTRYPOINT=cli` to dodge it: magpie's point is that the entrypoint is real.
- **Not ported (why):** `claudelock` (nothing here writes `claude`'s files, and `claude` takes its own locks), `claudeseal` (the bridge never replays Claude's signed thinking: a run keeps its own
  state), `bridgesid` (no session files with `--no-session-persistence`), `claude-code install` (needs a download + checksum track), shelved/branched sessions, the interactive side question,
  `/advisor`, client-placed cache marks, the lean system prompt, the user's words beside tool results (a request carrying text after its tool results has that text ignored while the run is parked).
- **Never verified:** any of it against a real `claude` binary or subscription (tests use a Node script that speaks stream-json and MCP, written from magpie's documentation); the exact stream-json
  event shapes, `rate_limit_event` fields, `claude auth status --json` and the `claudecode/toolUseId` MCP meta are taken from magpie's docs/sources. Windows (`claude.cmd`, ConPTY not needed in `-p`)
  and the Native AOT publish were not run.
- **Warm-up:** `ClientKind = warmup` rows are labelled in the log, not excluded from stats. A lapsed OAuth token is skipped, never renewed ([[subscription-refresh-on-use]]).

**Why:** keep the defaults intact and the diff reviewable while giving the mechanism (run reuse by conversation digest, parked runs on tool results, MCP bridge, limit → 429 failover).
**How to apply:** if a user reports "the bridge costs too much quota", the interactive mode is the open work, not a bug in the `-p` path; a stale idle run is matched only by digests, so a client that
rewrites history starts a new run told the whole conversation.
