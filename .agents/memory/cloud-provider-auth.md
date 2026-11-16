---
name: cloud-provider-auth
description: magpie-parity push #13 (2026-11-16) — Bedrock IAM via hand-written SigV4 on the compatible endpoints (no Converse codec), Vertex via service-account JWT, JSON documents in the one encrypted secret; what is verified and what is not
type: project
updated: 2026-11-16
source: session
evidence: ["src/Astra.Providers/Cloud/AwsSigV4.cs", "src/Astra.Providers/Cloud/GoogleServiceAccount.cs", "src/Astra.Gateway/Pipeline/CloudProviders.cs", "src/Astra.Server/Api/CloudSecrets.cs", "tests/Astra.Providers.Tests/AwsSigV4Tests.cs", "magpie cb4ad12 internal/provider/presets.go, vertex.go"]
---
Amazon Bedrock and Google Vertex AI were added as templates (`bedrock`, `bedrock-iam`, `bedrock-mantle`, `vertex-ai`, plus Azure's `api-key` header). The credential is a JSON document inside the provider's one `api_key_enc` (IAM access key / service account key file), not new columns, so no migration and the admin DTO's write-only `apiKey` string is unchanged.

**Why:** magpie itself serves Bedrock with a Bedrock API key (bearer) on the runtime's OpenAI-compatible `/openai/v1` and Anthropic-compatible `/anthropic` paths — no SigV4, no Converse. IAM keys were asked for on top; SigV4 is signed on those same compatible endpoints (service `bedrock`, Mantle `bedrock-mantle`), which avoids a fifth wire protocol (a Converse / ConverseStream codec plus AWS event-stream decoding would touch the IR, codecs, client adapters and the UI). Vertex follows magpie's `vertex.go` (generateContent / rawPredict / the project's OpenAI-compatible chat).
**How to apply:** do not add Converse unless a model that only Converse serves is wanted; then it is a new `ApiProtocol` through the hub (IR), not a pairwise adapter. **Unverified against live AWS/Google:** SigV4 on `/openai/v1` and `/anthropic` of bedrock-runtime (AWS documents it for API keys; IAM is assumed — the signature algorithm itself is pinned by AWS's test-suite vectors), Mantle's service name, and Vertex's partner-model paths (taken from magpie). Region is read from the endpoint host (a VPC endpoint needs `settings.aws.region`).
