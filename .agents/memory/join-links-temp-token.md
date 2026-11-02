---
name: join-links-temp-token
description: Employee onboarding via one-time join links (desktop) reverses the earlier "no join codes" decision; clients go through the local Astra, not straight to the gateway
type: project
updated: 2026-11-03
source: session
evidence: src/Astra.Server/Api/TokenInviteService.cs; ConnectService.cs; AGENTS.md linked update 24; .agents/memory/enterprise-join-uses-api-key.md
---

On 2026-11-03 the user asked for the desktop app to "connect to a unified gateway" with a link the admin copies from the token row:
`astra://join?gateway=…&token=<one-time code>`. The local server exchanges the code for the employee's key, stores it as a provider
(encrypted), and clients are bound to that provider, so they request the local Astra and the local Astra requests the gateway.

**Why:** a link pasted into chat must not hold the key (single use, short lifetime, only the hash stored); and the key must not be written
into client configs in plaintext (the user: "本地客户端的 key 应该是本地 astra 单独生成的令牌"). The earlier decision
([[enterprise-join-uses-api-key]], 2026-10-18: "no join codes") still holds for `astra-connect` (npx), which keeps taking a typed key.

**How to apply:** do not copy the key into a client config on the desktop path; do not extend `astra-connect` with codes unless asked. The
UI says "统一网关" (unified gateway), never "公司". Menu hiding (settings `hiddenNav`) is web-only by decision: the desktop has no clients menu to hide.
