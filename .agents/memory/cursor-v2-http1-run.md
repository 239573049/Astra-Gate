---
name: cursor-v2-http1-run
description: The v2 Cursor plugin chats over Cursor's HTTP/1.1 Run (RunSSE + BidiAppend) on host features added for it (2026-11-12); HTTP/2 to the regional host was given up; not yet tried against real Cursor
type: project
updated: 2026-11-12
source: session
evidence: ["design/plugin-v2/08-2.1-providers-clients.md §2.2", "src/Astra.Plugins/V2/Reply/FrameReaders.cs:ConnectFrameReader", "src/Astra.Plugins/V2/V2ProviderTransport.cs:SendAsync", "astragate-community/astra-gate-plugins packages/cursor/src/agent.js"]
---
On 2026-11-12 the user asked to make the v2 Cursor plugin chat (the v2 rewrite had left it sign-in/models/usage only). Cursor's
agent Run is a Connect stream both ways; v1 ran it over HTTP/2 on the regional agent host `GetServerConfig` names, with an
HTTP/1.1 fallback on `api2.cursor.sh`: `AgentService/RunSSE` streams the reply down, each client message goes up as its own
`BidiService/BidiAppend`. The v2 plugin uses only that HTTP/1.1 path, on five host additions: `prepare` may return a byte body
(`bodyBase64` over the bridge) and a `state` the reply starts with; `reply.framing: "connect"`; a frame's `{ $end: true }` ends
the reply and closes the upstream (the Run stays open waiting for tool results); `transformResponse.dispose`. Side calls
(blob answers, heartbeat) are unawaited `http.fetch`es from `frame` / timers, stopped in `dispose`.

**Why:** the host's fetch has no request-side stream, so HTTP/2 bidi is impossible, and a regional host is not a static `net:`
origin. Accepted losses: region-locked teams may get a 403; Max Mode / region retries within one request (the plugin remembers
Max-Mode-only models in storage instead); exact usage of a step ending in tool calls (estimated).

**How to apply:** the same five features are what devin and qoder lack for their non-JSON bodies. Until someone runs the plugin
against a real Cursor account, treat "can chat" as unverified (tests use built frames; one throwaway run of the built bundle in
the real Jaunt host passed). In the SDK harness `timers.sleep` is capped at 50 ms, so per-run timers fire almost at once in
tests — sweep by age instead of a long `setTimeout`.
