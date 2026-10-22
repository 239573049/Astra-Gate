---
name: provider-key-alternatives
description: Issues #8/#9 (2026-10-23): a key on the wrong same-service template is fixed by template-switch + keyAlternatives, not by guessing from the key prefix; the wizard got "more options" but no endpoint speed test
type: project
updated: 2026-10-23
source: session
evidence: ["src/Astra.Providers/Templates/ProviderTemplateCatalog.cs:ProviderTemplate.KeyAlternatives", "src/Astra.Server/Api/ProviderEndpoints.cs:template-switch", "web/src/components/TemplateSwitch.tsx:KeyAlternativeHint", "https://github.com/239573049/Astra-Gate/issues/9", "https://github.com/239573049/Astra-Gate/issues/8"]
---
Issue #9: a RoutinAI pay-as-you-go key (`ak-…`) on the `routin` template (plan endpoint `https://api.routin.ai/plan/v1`) gets
`HTTP 401: Please use plan API key` on every call. Until 0.10.0 `routin` was the only Routin template and was just called
"Routin", so older providers fall into this; `routin-ai` (`/v1`) came with 0.10.0. The fix is data plus a recovery path:
`keyAlternatives` pairs the two templates (the console names the other one in the wizard and under a 401/403 from
"fetch from upstream" or the connection test), and `POST /api/providers/{id}/template-switch` moves the provider over while
keeping its key and models. Importing now prefers the template with the exact address when two share a host.

**Why:** the key type cannot be told from the key: RoutinAI does not document its prefixes (docs.routin.ai was unreachable on
2026-10-23, routin.ai/plans has no API reference), so auto-picking the endpoint by prefix was not built. Silently retrying the
other endpoint was not built either. For #8 the user chose the wizard's "more options" (endpoints, protocol order, auth,
headers, proxy, multiplier, initial models, notes) over a cc-switch-style endpoint speed test, which is not built.
**How to apply:** another service whose plan and pay-as-you-go keys need different addresses gets `keyAlternatives` on both
templates (they must name each other, the catalog load throws otherwise), not a provider-specific error matcher. If RoutinAI
publishes key prefixes, revisit auto-selection.
