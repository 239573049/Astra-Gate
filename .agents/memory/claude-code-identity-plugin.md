---
name: claude-code-identity-plugin
description: Issue #24 — presenting Claude Code to API-key channels is a community plugin on Astra's own onUpstream hook, not a provider setting
type: project
updated: 2026-11-09
source: session
evidence: src/Astra.Plugins/Middleware/MiddlewareEngine.cs:MiddlewareRun.Upstream; astragate-community/astra-gate-plugins packages/middleware-claude-code
---

Issue #24 (channels that answer "this channel only serves official Claude Code CLI requests") is served by the
community plugin `@astragate-community/middleware-claude-code`, which runs on a middleware hook Astra added for it:
`onUpstream(body, ctx)` — per attempt, after translation and the privacy guard, with `ctx.upstream` (protocol,
provider, model, stream), `ctx.setHeader` / `ctx.removeHeader` and `ctx.sha256` (text or byte array). The user chose
this on 2026-11-09 over two alternatives: extending `subscription.mimic_claude_code` to API-key providers as a
provider setting (least work, one copy of the identity), and an onUpstream hook that only exposes the built-in
`ClaudeCodeMimicry` to the plugin.

**Why:** the user wanted it as a plugin, configured per channel (provider id or name) and protocol. magpie's hooks
could not do it: they see the agent's body before translation, no provider, and no upstream headers.

**How to apply:** the identity now lives twice — `ClaudeCodeMimicry` (subscription accounts) and the plugin's
`src/identity.mjs` (CLI version, betas, fingerprint salt, host profiles). Move them together when the CLI's capture
changes; the plugin's tests pin the C# fingerprint values (`a99` / `d4a`). The plugin's anthropic-beta set is the
mimic floor without the OAuth beta — not checked against a capture of an API-key CLI session. The provider "test"
button does not run middleware, so it does not show what the plugin sends.
