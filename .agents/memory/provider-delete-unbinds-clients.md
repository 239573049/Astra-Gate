---
name: provider-delete-unbinds-clients
description: Deleting a provider unbinds its clients (user request 2026-11-11) instead of refusing with 409; a client left with none is disabled
type: project
updated: 2026-11-11
source: session
evidence: ["src/Astra.Server/Api/ClientService.cs:ClientService.UnbindProviderAsync", "src/Astra.Server/Api/ProviderEndpoints.cs:MapDelete", "tests/Astra.Server.IntegrationTests/ClientApiTests.cs:Deleting_A_Provider_Unbinds_Its_Clients_And_Disables_One_Left_With_None"]
---
Until 2026-11-11 `DELETE /api/providers/{id}` refused with 409 "Provider is bound to clients" while any client was bound, and the
consoles told the user to switch those clients first. The user asked for the delete to unbind them itself. It now calls
`ClientService.UnbindProviderAsync` first — the same steps `ConnectService.LeaveAsync` used for the unified gateway, now shared:
a client with other providers keeps them in order (`SetBindingsAsync`, so model lists re-sync); an **enabled** client left with
none is disabled (`DisableAsync(force: false)`, drifted keys stay); a bound-but-not-enabled client only loses the binding rows.
The confirm dialogs name the clients beforehand (`providers.deleteDetailBound`); the response stays 204.

**Why:** an enabled client with no binding is refused by the gateway anyway ([[client-default-provider-read-side]]), so restoring
its own config leaves it working on its own; `LeaveAsync` was the precedent. Same day, the "Provider is disabled" refusal
(`RequireProviderAsync`) gained `DisabledProviderDetailsDto` (name + `pluginOff`), because the enable dialog showed only the bare
English message when a plugin provider was switched off with its plugin.

**How to apply:** `DeleteIfUnboundAsync` still guards the delete, so the 409 with `boundClients` remains — only for a client bound
in between. Do not drop that guard; the FK cascade would silently remove a fresh binding.
