---
name: images-api-forwarding
description: The OpenAI Images API (issue #17) is forwarded untranslated beside the generation pipeline, logged as "openai-images" rather than a new ApiProtocol; forms stream after their model field
type: project
updated: 2026-10-30
source: session
evidence: ["src/Astra.Gateway/Pipeline/GatewayPipeline.Images.cs:GatewayPipeline.HandleImagesAsync", "src/Astra.Gateway/Pipeline/MultipartForm.cs", "src/Astra.Gateway/Pipeline/GatewayPipeline.cs:SendAsync (content/resendable)", "tests/Astra.Server.IntegrationTests/GatewayImagesTests.cs"]
---
On 2026-10-30 `POST /v1/images/generations` and `/v1/images/edits` were added (issue #17: `gpt-image-*` calls failed —
the paths hit the SPA fallback's 404, and an image model sent to chat/responses was rejected upstream). Decisions:

- **Not an `ApiProtocol` value.** A new enum member would reach provider endpoints and templates, client protocol choices
  (`ClientProtocols`), the web `ApiProtocol` union and its `Record<ApiProtocol, …>` maps, desktop protocol lists and the
  codec registry — for a call nothing translates. The request row stores the string `openai-images` (inbound and upstream,
  passthrough); both UIs print protocol ids as raw text, so it needs no i18n (it shows without a vendor mark).
- **A second entry on `GatewayPipeline`, not a second pipeline**: same route/limiter/privacy/`SendAsync`/`Finish`/trace
  steps and contracts, so the relay serves it too. `SendAsync` grew an optional caller-built `content` and `resendable`.
- **Edits forms are streamed** once the `model` part is read (the OpenAI SDKs and docs send it first), unless the privacy
  guard is on (the prompt usually comes after the images and must be inspected before anything leaves), the body is
  `Content-Encoding`-compressed, or the host has a body deadline (`RequestBodyLimits.ReadTimeout`, i.e. the relay). A
  streamed body is send-once: no 401 renewal or account failover for it.
- **Pools**: the strategy's first member only, no cross-member failover. **Billing** from `usage` via
  `UsageNormalizer.FromOpenAIImages`; image tokens fall back to input/output prices. The seed catalog has no image models.

**Why:** the maintainer promised "forward the Images API, token auth, request log and billing from usage" with the smallest
change that keeps one pipeline; base64 images and uploads must never be buffered into logs, traces or captures.

**How to apply:** a future `/v1/images/variations` or audio endpoint follows the same shape (string protocol id, no codec).
If image calls ever need translation (e.g. to Gemini image models), that is when an IR/`ApiProtocol` becomes worth it.
