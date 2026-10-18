# Repository memory

Index of this directory: one line per entry. Open an entry before relying on it.
Format and rules: [README.md](README.md). Personal memory lives in `.next-cowork/memory/` (not committed).

## Project
- [Jint AOT fallback was rejected](jint-aot-fallback-rejected.md) — middleware stays in-process; the Bun middleware host was the failure plan, not a design
- [Webhook formats were added](webhook-formats-added.md) — Feishu, DingTalk, WeCom, Slack and Discord renderers exist despite the "generic JSON only" design
- [Plugin market has one upstream](market-upstream-single-source.md) — the community registry address is fixed, and a cache from the old one is never served
- [macOS AOT needs Xcode 26](mac-aot-needs-xcode-26.md) — the 0.3.0 link failure, and why the release rows pin macos-15 plus an Xcode 2x
- [Release publishing incidents](release-publishing-incidents.md) — the 0.2.0 workflow, npm-size, truncated-upload and update-status failures
- [GUI-launched updates need a PATH](gui-launch-npm-path.md) — Dock/Finder launches cannot see node, and the manifest is keyed by RID
- [Client protocols left out on purpose](client-protocols-not-used.md) — Zed Anthropic, Hermes Responses, OpenCode Gemini, WorkBuddy Responses: why each stays translated
- [Windows autostart uses the Run key](autostart-run-key-not-schtasks.md) — schtasks logon tasks need elevation; the server never loads/unloads its own entry
- [Orphaned server after a failed update](orphaned-server-after-failed-update.md) — two servers on one data dir: UI shows the old one, the updater says up to date
- [Release channels decision](release-channels-decision.md) — preview is derived from the version, the preview feed merges in stable, the manifest goes up after npm
- [Self-service shows the requested model](key-self-service-requested-model.md) — holders see their request log and per-requested-model stats; never provider, account or upstream model
- [Smart routing decisions](smart-routing-decisions.md) — virtual model trigger, judge is not a Provider, session pin by default, neither a fallback nor the lowest grade is pinned, a grade names its provider (limited tokens keep to theirs), the request log marks each decision, the OpenAI dialect is still a draft
- [Smart routing's cold judge](smart-routing-cold-judge.md) — the first real use timed out on every cold connection and sent "1+1" to the fallback grade; why the judge has its own long-lived HTTP/2 client
- [Enterprise join uses API key](enterprise-join-uses-api-key.md) — `astra join` takes gateway URL + company-issued API key; join codes rejected

## Reference

## Feedback
- [Direct credentials stay direct](direct-credentials-stay-direct.md) — callable imported tokens are sent as provided; renewal needs a renewal grant, and auth rejection follows the account-switch policy
