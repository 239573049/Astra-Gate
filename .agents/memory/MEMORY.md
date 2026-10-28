# Repository memory

Index of this directory: one line per entry. Open an entry before relying on it.
Format and rules: [README.md](README.md). Personal memory lives in `.next-cowork/memory/` (not committed).

## Project
- [Avalonia native menu is set once](avalonia-native-menu-set-once.md) — a second NativeMenu.SetMenu on a macOS window throws "The menu being updated does not match"; relabel in place
- [Windows hidden title bar leaves a caption strip](windows-hidden-title-bar-caption-hint.md) — hint -1 keeps ~31 DIP of HTCAPTION over the content; set it to the frame thickness, draw the caption buttons with roles; unverified on Windows
- [Avalonia window units](avalonia-window-units.md) — save window bounds in the screen's units (points on macOS), not render-scaled pixels; the sidebarCollapsed report was a harness leak, not a bug in real launches
- [Desktop page lifetime](desktop-page-lifetime.md) — pages subscribe to shared queries via Subscribe/OnDispose (2026-10-27 leak + Requests redraw storm); one Arc-held instance per page type is expected
- [Desktop tray relaunch](desktop-tray-relaunch.md) — a shown window leaves ~110–170 MB GPU memory nothing frees; after 2 min closed the app restarts `--hidden` (259 → 105 MB), hand-over rules
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
- [Gateway read cache](gateway-read-cache.md) — why per-request SQLite reads go through RowCache (Generation + 2 s MaxAge), and the SQLite mutex/PRAGMA findings behind it
- [Usage writer ceiling](usage-writer-ceiling.md) — 2,000-record buffering trades ~6% sustained throughput for ~39% lower RSS; 1,000 cost more throughput; ordinal binding +73% is isolated AOT; include drain
- [Dapper.AOT sequence cast](dapper-aot-sequence-cast.md) — a List<anonymous> sequence parameter throws InvalidCastException at run time; pass IEnumerable
- [astra-server --help starts a server](astra-server-flag-first-serves.md) — a flag-first call runs `serve` on ~/.astra and overwrites runtime.json; use ASTRA_HOME for throwaway servers
- [Harness thinking picker needs reasoningEfforts](harness-reasoning-efforts-field.md) — `reasoning` is a route default; models without `reasoningEfforts` get no effort picker (issue #7)
- [Client default provider is read-side](client-default-provider-read-side.md) — a client with no bindings reads as bound to the first usable provider; stored on enable or a bindings edit, never on read
- [WorkBuddy's Anthropic route](workbuddy-anthropic-route.md) — only hy3 / hy3-x answer on /v1/messages (China build), no thinking switch there; the plugin declares them @ai-sdk/anthropic (probed 2026-10-20)
- [Provider key alternatives](provider-key-alternatives.md) — issues #8/#9: wrong-template keys are fixed by template-switch + `keyAlternatives`, not by key prefix; no endpoint speed test yet
- [Relay decisions](relay-decisions.md) — `astra-server relay`: same pipeline behind contracts, one template per process, per-request credentials never stored/logged or sent to a request-named address, raw model list

## Reference

## Feedback
- [Direct credentials stay direct](direct-credentials-stay-direct.md) — callable imported tokens are sent as provided; renewal needs a renewal grant, and auth rejection follows the account-switch policy
