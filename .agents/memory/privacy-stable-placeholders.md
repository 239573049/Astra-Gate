---
name: privacy-stable-placeholders
description: Why the privacy guard's placeholders are keyed-hash `{{KIND_xxxxxxxx}}` instead of per-request `[REDACTED:cat#n]`, what is remembered, and what stays off by default
type: project
updated: 2026-11-16
source: session
evidence: ["src/Astra.Core/Privacy/PlaceholderMint.cs:PlaceholderMint", "src/Astra.Gateway/Pipeline/PrivacyGuardService.cs:LoadKeyAsync", "src/Astra.Gateway/Pipeline/PrivacyStreamRestorer.cs:PrivacyStreamRestorer", "src/Astra.Core/Privacy/PrivacyTypes.cs:PrivacyCategories.DefaultOff"]
---
Magpie-parity work (2026-11-16): the guard's placeholders used a per-request ordinal (`[REDACTED:email#1]`), so the same value
had a different placeholder in each request of a conversation — the model lost track of it and the provider's prompt cache missed
on every turn. They are now `{{EMAIL_k3v9x2mq}}` (magpie's format; braces and a word-like body survive models copying them), the
suffix being an HMAC of category and value under a per-install key (settings row `privacy_placeholder_key`, protected).

- Masked values are remembered in memory only (4096, FIFO, ≥ 6 chars); never persisted, never logged. A restart forgets them but the
  rules and the stable key still give the same placeholders.
- `ip` (public addresses) and `home_user` (user name in `/Users/<name>`) are off until a per-category or per-rule action names them, like
  magpie: they occur in nearly every coding request and the model has to copy the value back exactly. A client default or the default
  action does not enable them.
- The guard fails closed (503) instead of sending an unmasked body; a dry run is the exception (nothing is masked there anyway).
- A local NER model (magpie's "model" masking) was explicitly out of scope: rules only.

**Why:** consistency across a conversation and prompt-cache stability; a keyed hash so a placeholder in a log reveals nothing about its value.
**How to apply:** a new category needs its rule, `PrivacyCategories.BuiltIn`, zh/en labels, the Avalonia `PrivacyMeta.Categories` and (if noisy)
`DefaultOff` + `PRIVACY_DEFAULT_OFF`; never reintroduce per-request numbering or a string replace on streamed frames (see AGENTS.md item 28).
