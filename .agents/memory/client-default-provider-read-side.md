---
name: client-default-provider-read-side
description: A client with no stored bindings defaults to the first usable provider on read only; enabled clients with none stay unbound
type: project
updated: 2026-10-19
source: session
evidence: ["src/Astra.Server/Api/ClientService.cs:ClientService.BindingsOrDefaultAsync", "tests/Astra.Server.IntegrationTests/ClientApiTests.cs:A_Client_Nobody_Configured_Defaults_To_The_First_Usable_Provider_Without_Storing_It"]
---
The user asked (2026-10-19) that a client should use the first provider by default, as cc-switch does: the client card used
to show an empty "使用的提供商" with every provider as a "+" row, and *Enable…* stayed disabled until one was added.
`ClientService.BindingsOrDefaultAsync` now reports the first usable provider (`ClientRouting.IsUsable`, order of
`ProviderRepository.ListAsync`) as the binding of a client that has none stored and is not enabled. It feeds `InfoAsync`,
`PrepareAsync` and `ModelsAsync`, so the web page needed no change.

**Why:** it is a read-side default, not a write. `Listing_Supported_Clients_Does_Not_Configure_Any_Of_Them` asserts a
`GET /api/clients` leaves `clients` empty, and storing it eagerly would also pin the client to a provider the user never
saw (a later first-by-name provider would not take over). An enabled client with no bindings is excluded because the gateway
refuses it (`GatewayRouter`: "还没有选择提供商"), so the card must not claim a provider that traffic does not reach.

**How to apply:** the default is stored by `EnableAsync` (primary → slot 0) or the next `PUT …/bindings`. Do not turn it into a
write on read. `SetBindingAsync` / `SetBindingsAsync` / `SyncModelListCoreAsync` still read stored bindings on purpose.
