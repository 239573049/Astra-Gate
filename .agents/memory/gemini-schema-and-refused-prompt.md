---
name: gemini-schema-and-refused-prompt
description: A Gemini client's OpenAPI-subset schemas were reaching Chat/Messages/Responses upstreams with upper-case types; a refused prompt answered a Gemini client with an empty candidate; what was measured and why the cached-token mapping was left alone (2026-11-23)
type: project
updated: 2026-11-23
source: session
evidence: ["src/Astra.Gateway/Protocol/Gemini/GeminiSchemas.cs", "src/Astra.Gateway/Protocol/Gemini/GeminiResponseEncoder.cs:PromptRefused", "tests/Astra.Gateway.Tests/Codecs/GeminiSchemaTests.cs", "tests/Astra.Server.IntegrationTests/RelayGatewayTests.cs", "live calls against a Copilot Gemini model, 2026-11-15: one function declaration, types OBJECT/STRING answered 400 `invalid request body`, the same with object/string answered 200 with a functionCall"]
---
**Schemas.** `GeminiRequests.DecodeTools` put Gemini's `parameters` (and `DecodeGenerationConfig` its `responseSchema`) into the IR
unchanged, but those are an OpenAPI-subset `Schema` — `type` is an upper-case enum (`OBJECT`, `STRING`), a nullable value says
`nullable: true`, `propertyOrdering` is a hint — while the IR's `InputSchema` / `ResponseFormat.Schema` are JSON Schema that every other
encoder forwards verbatim. So a Gemini client behind a Chat, Messages or Responses upstream sent `"type":"OBJECT"`; Copilot's Gemini
models answered 400 `invalid request body` (measured live, see evidence). `GeminiSchemas.ToJsonSchema` now converts at decode: lower-case
types, `TYPE_UNSPECIFIED` dropped, `nullable` as a null type (also in an `enum`, or as one more `anyOf` alternative), `propertyOrdering`
dropped; every other keyword and the key order are kept. `parametersJsonSchema` / `responseJsonSchema` are JSON Schema already and are
not touched. Not converted on purpose: `example` (valid JSON Schema keyword or ignored), int64 bounds sent as strings.

**Refused prompt.** Copilot refuses a prompt with an empty `choices` next to the usage and no finish reason. `ChatResponseDecoder`
turns that into `MessageStop(Other)` with no raw reason and no content; `GeminiResponseEncoder` used to answer one empty candidate
(`finishReason: OTHER`). Gemini says a blocked prompt in `promptFeedback`, and a client that finds neither a candidate nor feedback
reads an empty upstream answer, so the encoder now answers `candidates: []` + `promptFeedback.blockReason: OTHER` (the reason is
unknown; no other decoder produces `Other` without a raw reason). A named finish reason, `content_filter` included, still gets its
candidate (pinned by `GeminiResponseEncoderTests`).

**Cached tokens needed no change.** A Chat upstream's `prompt_tokens_details.cached_tokens` already reaches a Gemini client as
`usageMetadata.cachedContentTokenCount`, inside `promptTokenCount` (non-streaming and streaming); the tests now pin it. A client seeing
0 there is going through a different converter, not this one.

**Why:** found while pointing a Gemini-native client fleet at a Copilot relay; the schema case made most tool-carrying requests fail.
**How to apply:** a new Gemini field that carries a schema must say whether it is the OpenAPI form (convert) or JSON Schema (keep).
Other Copilot Gemini refusals are not covered here (tool answers that do not follow their calls, a schema with an array without `items`
or an object without `properties`, a request with no text, GIF/file parts, `reasoning_effort` values the models do not list).
