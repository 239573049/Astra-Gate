---
name: broader-gateway-surface
description: Magpie-parity gateway routes (embeddings, rerank, audio, native passthrough, Responses WebSocket, Ollama face) — why they reuse the HTTP pipeline, why Ollama lives under /ollama, what was left out
type: project
updated: 2026-11-16
source: session
evidence: ["src/Astra.Gateway/Pipeline/GatewayPipeline.Images.cs:HandleForwardedAsync", "src/Astra.Gateway/Http/InternalCall.cs", "src/Astra.Gateway/Http/ResponsesSocket.cs", "src/Astra.Gateway/Http/OllamaEndpoints.cs", "src/Astra.Gateway/Pipeline/GatewayPipeline.Native.cs", "tests/Astra.Server.IntegrationTests/GatewayForwardedTests.cs"]
---
Added 2026-11-16 (feature #4 of the magpie-parity push; behaviour learned from magpie cb4ad12, MIT):

- **One forwarding handler for images, embeddings, rerank and audio** (`ForwardedCall` records), each with its own request-log
  protocol id (like `openai-images`; see images-api-forwarding.md). Embeddings for a Gemini-only provider are translated
  (`embedContent` / `batchEmbedContents`), unbilled (Gemini reports no tokens). There is no model "kind": an embedding model is
  routed like any model id, so with several providers the model has to be listed on the right one.
- **WebSocket turns and Ollama chats are synthetic HTTP requests** (`InternalCall`): the original headers + a new body through
  `GatewayPipeline.HandleAsync`, the answer parsed back. Do not wire them to services directly — the token caps, failover, log
  and bill come only from the HTTP route. The socket holds its last two replies (previous_response_id), a warmup
  (`generate:false`) is answered locally, `response.interrupt` cancels the turn. Codex's ChatGPT-backend-only frames
  (turn state, rate-limit windows) are not produced. Unverified against a real Codex.
- **Ollama lives under `/ollama`** (client Ollama URL = `http://host:port/ollama`) because `/api` is the admin API and
  `SecurityMiddleware` guards it; `/ollama` is a gateway path (token off this machine). Most Ollama clients send no key, so
  they work on this machine with the key requirement off.
- **Native passthrough** (`/v1/astra/native/{provider}/{path}`): the provider's origin (not its base path) + a checked path;
  API-key providers only; tokens with cost/token budgets refused (cannot be priced); privacy guard masks JSON and refuses other
  bodies. The raw request target is read from `IHttpRequestFeature.RawTarget` (Kestrel); TestServer has none, so tests cover the
  encoded-slash cases in `ForwardedCallsTests`.
- **Left out**: relay serves rerank and audio through the pipeline but not the WebSocket, Ollama or native routes; embeddings stay
  raw-relayed there. No UI: the new kinds show in the request log as raw protocol ids.
