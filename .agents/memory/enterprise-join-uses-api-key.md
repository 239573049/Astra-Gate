---
name: enterprise-join-uses-api-key
description: Enterprise employee onboarding (astra join) takes the gateway URL + the API key the company hands out; no join/enrollment codes
type: project
updated: 2026-10-18
source: session
evidence: .plan/enterprise-connect.md; connect/src/commands/join.ts; KeyInfoEndpoints.ConnectContextAsync
---

The employee CLI (`@aidotnet/astra-connect`, pure TypeScript — the user chose TS over shipping a slim AOT
binary on 2026-10-18; design in `.plan/enterprise-connect.md`) asks the employee for the enterprise gateway URL and an API key (an Astra token).
One-time join / enrollment codes were proposed and rejected by the user.

**Why:** enterprises already distribute API keys to employees; a code-exchange step adds a table, a public endpoint
and console UI without matching how they work.

**How to apply:** do not add a join-code table, `/v1/astra/join` endpoint or "generate join code" UI. Keep the key off
argv (masked prompt, `ASTRA_API_KEY` env or `--key-stdin`), validate it against `/v1/astra/key`, and store it in
`~/.astra/connect/profile.json` (0600).

What it writes into a client is the bare token (not the client-suffixed key): the suffixed form routes through a client
enabled and bound on the server, which an enterprise gateway does not have for employees' machines.
