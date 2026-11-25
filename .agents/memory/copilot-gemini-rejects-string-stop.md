---
name: copilot-gemini-rejects-string-stop
description: GitHub Copilot's Gemini models answer 400 "Request body JSON is invalid." to a Chat request whose stop is a string and accept a list of any length; the Chat encoder used to send a lone stop sequence as a string (2026-11-25)
type: project
updated: 2026-11-25
source: session
evidence: ["src/Astra.Gateway/Protocol/Chat/ChatCodec.cs:EncodeRequest", "tests/Astra.Gateway.Tests/Codecs/ChatRequestTests.cs:Stop_Is_Always_Encoded_As_A_List", "tests/Astra.Server.IntegrationTests/RelayGatewayTests.cs", "GitHub issue #54", "direct calls to https://api.githubcopilot.com/chat/completions, 2026-11-25: gemini-3.8-flash and gemini-4-neon / -argon / -hydrogen / -carbon, stop as string 400, as list 200"]
---
`ChatCodec.EncodeRequest` wrote a request's only stop sequence as a string (`"stop":"END"`) and two or more as a list, since the initial commit and with no recorded reason. A Gemini client (`stopSequences`) or an Anthropic client (`stop_sequences`) with exactly one sequence therefore reached a Chat upstream with a string, and GitHub Copilot's Gemini models refused it: 400 `{"error":{"message":"Request body JSON is invalid.","code":"invalid_request_body"}}`. It now always writes a list.

Measured directly against `https://api.githubcopilot.com/chat/completions` on 2026-11-25 (gemini-3.8-flash, gemini-4-neon, -argon, -hydrogen and -carbon, with the short-lived token of a Copilot account): no `stop` 200; `"END"` 400; `["END"]` 200; `["END","STOP"]` 200; `[""]` 200; `""` 400. An empty string inside a list is accepted, so the encoder does not filter empty entries.

**Why:** found when a client (SillyTavern with one custom stopping string) failed on every request through a Gemini-format channel; the same traffic had been fine while the gateway in front sent a list.
**How to apply:** a Chat client's own `stop` is forwarded as sent (same-protocol pass-through, `passthrough = upstreamProtocol == inbound` in `GatewayPipeline.cs`), so a string a client writes still reaches the upstream as a string. Other Chat upstreams were not checked one by one; no provider template special-cases `stop`. Related: [[gemini-tool-choice-without-tools]].
