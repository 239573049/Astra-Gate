---
name: webhook-formats-added
description: Alert webhooks grew Feishu, DingTalk, WeCom, Slack and Discord renderers after the design said generic JSON only
type: project
updated: 2026-10-14
source: init
evidence: [".plan/alerts-webhooks-token-limits.md", "src/Astra.Core/Alerts/AlertModels.cs:WebhookFormats", "src/Astra.Server/Alerts/WebhookFormatter.cs:WebhookFormatter"]
---
The alerts design (`.plan/alerts-webhooks-token-limits.md`, decided before the 2026-10-14 implementation)
said a webhook payload would be generic JSON with an HMAC signature and would not grow built-in formats
for Feishu, DingTalk and similar. The shipped code does both: the stored payload is still the generic
JSON, and `WebhookFormatter.Render` turns it into a platform body at send time.

**Why:** The delivery log keeps the generic payload so a signature can carry a fresh timestamp — Feishu
and DingTalk reject old ones — which is why rendering happens at send time rather than when the alert
fires. `WebhookFormats` lists `generic`, `feishu`, `dingtalk`, `wecom`, `slack` and `discord`.
**How to apply:** Treat the plan's "generic JSON only" line as superseded. A new platform is another
`WebhookFormats` constant plus a `Render` branch and a success check in `Rejection`, not a reason to
remove the existing ones. The generic payload shape is still the public contract documented in
`docs/content/docs/console/notifications.mdx`.
