---
name: currency-manual-rates-only
description: Display currencies use rates the user types; Astra must never make a network request for exchange rates (user decision 2026-11-16)
type: project
updated: 2026-11-16
source: user
evidence: ["src/Astra.Core/Currency/DisplayCurrency.cs:Currencies", "src/Astra.Core/AppSettings.cs:CurrencyRates"]
---
The magpie-parity feature "display currencies" (magpie's `internal/fx` refreshes rates over the network) was
decided to be manual only: the user enters units of currency per 1 USD in settings, a currency without a rate
cannot be the display currency, and no code path may fetch rates. Money stays USD nanodollars everywhere it is
stored or sent (webhook payload fields, token self-service views, pricing JSON); only display and typed
amounts convert. Do not add an auto-refresh, even as an opt-in, without asking.
