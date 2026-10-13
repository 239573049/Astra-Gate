-- Webhook payload format (plan: .plan/alerts-webhooks-token-limits.md §3.4): 'generic' is Astra's own signed JSON;
-- 'feishu' | 'dingtalk' | 'wecom' | 'slack' | 'discord' render the alert as that platform's bot message. For feishu and
-- dingtalk secret_enc holds the bot's own signing secret (entered by the user), not an Astra-generated one.
-- Plain DDL (NOT idempotent by design — 0001 is the only idempotent script).

ALTER TABLE webhooks ADD COLUMN format TEXT NOT NULL DEFAULT 'generic';
