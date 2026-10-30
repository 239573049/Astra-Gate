---
name: subscription-refresh-on-use
description: subscription tokens are renewed only by the call that needs them (2026-10-30 user decision); no worker refreshes them, and background quota rounds skip lapsed tokens
type: project
updated: 2026-10-30
source: session
evidence: ["src/Astra.Providers/Subscription/SubscriptionTokenService.cs:IsFresh/GetValidAccessTokenAsync", "src/Astra.Server/Api/SubscriptionQuotaSync.cs:IsDue/IsCooledDue", "src/Astra.Gateway/Pipeline/UpstreamAuthResolver.cs (GetValidAccessTokenAsync, RefreshAfterUnauthorizedAsync)", "tests/Astra.Server.IntegrationTests/AlertTests.cs:Background_Quota_Rounds_Never_Renew_A_Token"]
---
On 2026-10-30 the user asked that subscription tokens (Kimi's lasts 15 min) be checked and refreshed when a call uses them —
refresh first, then send — instead of in the background. `SubscriptionRefreshWorker` (every 5 min, accounts expiring within
11 min) and `IProviderAccountStore.ListExpiringAsync` were removed. Its "令牌已提前刷新" log line had been misleading anyway:
it printed whenever `RefreshAsync(force: false)` returned, including the usual case where `IsFresh` returned without a call.
The background quota sync used to renew as a side effect (`GetValidAccessTokenAsync` inside the probe, every 30 min by
default), so `SubscriptionQuotaSync.IsDue` / `IsCooledDue` now also require `SubscriptionTokenService.IsFresh(account, now)`;
a skipped account records no failure. A quota query the user starts, the gateway, Claude direct mode and the 401/403
recovery still refresh as before.

**Why:** the user wants no token traffic while an account is idle; a refresh can also rotate a refresh token that a local
CLI imported into Astra still holds.
**How to apply:** do not add a timer that renews tokens or a background path that calls `GetValidAccessTokenAsync` for an
account without checking `IsFresh` first. Consequences to keep in mind: an idle account's card shows an access-token expiry
in the past (expected); a cooling account whose token lapsed is no longer rechecked early; a provider whose refresh token
dies after long disuse needs a new sign-in. Related: [[direct-credentials-stay-direct]].
