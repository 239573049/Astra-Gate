---
name: gemini-tool-choice-without-tools
description: A Gemini client's toolConfig with no function declared (or beside only a tool Chat cannot carry) reached a Chat upstream as a tool_choice with no tools, and GitHub Copilot's Gemini models answered 400; the Chat encoder now writes tool_choice only when the body has tools (2026-11-24)
type: project
updated: 2026-11-24
source: session
evidence: ["src/Astra.Gateway/Protocol/Chat/ChatCodec.cs:EncodeRequest", "tests/Astra.Gateway.Tests/Codecs/ChatRequestTests.cs:Tool_Choice_Is_Sent_Only_With_Tools", "tests/Astra.Server.IntegrationTests/RelayGatewayTests.cs", "GitHub issue #52", "production logs, 2026-11-24: 3 of 489 requests in a 5-minute window answered `tools are required when tool choice is specified`"]
---
Gemini's `toolConfig` and `tools` are separate fields, so a request can carry a `functionCallingConfig` with no function declared, or beside only a
tool the Chat encoder drops (`googleSearch`, `codeExecution`, …). The decoder keeps the choice in the IR; the Chat encoder wrote `tool_choice` anyway
and GitHub Copilot's Gemini models refused the request (400 `tools are required when tool choice is specified`). `ChatCodec.EncodeRequest` now
writes it only when the body it built has `tools`. The test is on the encoded `tools`, not on `Tools.Count`: `EncodeTools` drops a builtin tool of
another protocol, so the IR can list a tool while the body has none (case 2 of the repro in #52).

**Why:** a Gemini-native client fleet pointed at Copilot had about 0.6% of its requests refused this way. The request bodies were not logged, so the
link to `toolConfig` is read from the error text and the code, and shown by the repro in #52.
**How to apply:** left alone on purpose, because no upstream has been seen rejecting them: `AnthropicRequestCodec` and `ResponsesCodec` also write
`tool_choice` without looking for tools, and the Chat encoder's `parallel_tool_calls` tests `Tools.Count`. Check what the upstream answers before
extending the rule to them. Related: [[gemini-schema-and-refused-prompt]].
