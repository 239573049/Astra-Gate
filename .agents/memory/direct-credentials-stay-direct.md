---
name: direct-credentials-stay-direct
description: Directly usable imported tokens must remain direct credentials rather than being exchanged or refreshed without a renewal grant.
type: feedback
updated: 2026-10-18
source: session
evidence: User clarification on 2026-10-18; src/Astra.Server/Api/SubscriptionEndpoints.Import.cs:ImportOneAsync; src/Astra.Providers/Subscription/SubscriptionTokenService.cs:GetValidAccessTokenAsync
---

An imported token that is already usable for inference should be sent as provided. The user clarified this while
reviewing Copilot's 401 account switching: exchanging a GitHub authorization for a Copilot token and using an imported
Copilot token directly are different credential paths.

**Why:** importing a callable token does not provide the long-lived authorization needed to replace it. A failed
request should not turn into an attempt to exchange that callable token for another key.

**How to apply:** only attempt renewal when the account has renewal credentials (or a plugin owns renewal). Keep a
direct token usable until its actual expiry; on an upstream auth rejection, apply the selected account-switch policy.
