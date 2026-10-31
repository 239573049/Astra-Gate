# Repository memory

Index of this directory: one line per entry. Open an entry before relying on it.
Format and rules: [README.md](README.md). Personal memory lives in `.next-cowork/memory/` (not committed).

## Project
- [Avalonia native menu is set once](avalonia-native-menu-set-once.md) — a second NativeMenu.SetMenu on a macOS window throws "The menu being updated does not match"; relabel in place
- [Windows hidden title bar leaves a caption strip](windows-hidden-title-bar-caption-hint.md) — hint -1 keeps ~31 DIP of HTCAPTION over the content; set it to the frame thickness, draw the caption buttons with roles; unverified on Windows
- [Avalonia window units](avalonia-window-units.md) — save window bounds in the screen's units (points on macOS), not render-scaled pixels; the sidebarCollapsed report was a harness leak, not a bug in real launches
- [Desktop page lifetime](desktop-page-lifetime.md) — pages subscribe to shared queries via Subscribe/OnDispose (2026-10-27 leak + Requests redraw storm); one Arc-held instance per page type is expected; polled queries a view rebuilds from use SameData + a QueryWatch select (issue #16)
- [Desktop tray relaunch](desktop-tray-relaunch.md) — a shown window leaves GPU memory nothing frees; 15 s after a close the app restarts `--hidden` (~250 → ~50 MB, 2026-10-30), which builds no main window; what the ~50 MB floor is; guards and hand-over rules
- [Avalonia DispatcherTimer starts itself](avalonia-dispatchertimer-autostart.md) — the (interval, priority, callback) constructor runs the timer at once; it made tray-only processes restart in a loop
- [Windows tray menu is an Avalonia window](avalonia-win32-tray-menu-window.md) — TrayPopupRoot sized to a MenuFlyoutPresenter; without menu themes it was 0 x 0 (issue #18, fixed 0.13.2); TrayMenuWindowTests builds it headless
- [Jint AOT fallback was rejected](jint-aot-fallback-rejected.md) — middleware stays in-process; the Bun middleware host was the failure plan, not a design
- [Webhook formats were added](webhook-formats-added.md) — Feishu, DingTalk, WeCom, Slack and Discord renderers exist despite the "generic JSON only" design
- [Plugin market has one upstream](market-upstream-single-source.md) — the community registry address is fixed, and a cache from the old one is never served
- [macOS AOT needs Xcode 26](mac-aot-needs-xcode-26.md) — the 0.3.0 link failure, and why the release rows pin macos-15 plus an Xcode 2x
- [Release publishing incidents](release-publishing-incidents.md) — the 0.2.0 workflow, npm-size, truncated-upload and update-status failures
- [GUI-launched updates need a PATH](gui-launch-npm-path.md) — Dock/Finder launches cannot see node, and the manifest is keyed by RID
- [Client protocols left out on purpose](client-protocols-not-used.md) — Zed Anthropic, Hermes Responses, OpenCode Gemini, WorkBuddy Responses: why each stays translated
- [Windows autostart uses the Run key](autostart-run-key-not-schtasks.md) — schtasks logon tasks need elevation; the server never loads/unloads its own entry
- [Orphaned server after a failed update](orphaned-server-after-failed-update.md) — two servers on one data dir: UI shows the old one, the updater says up to date
- [Avalonia updater was server-only](avalonia-updater-server-only.md) — packaged apps drifted behind their server; fixed 2026-10-30 with the track rule + app self-update (option B), unverified in a real release
- [Release channels decision](release-channels-decision.md) — preview is derived from the version, the preview feed merges in stable, the manifest goes up after npm
- [Self-service shows the requested model](key-self-service-requested-model.md) — holders see their request log and per-requested-model stats; never provider, account or upstream model
- [Subscription tokens refresh on use](subscription-refresh-on-use.md) — no worker renews them (2026-10-30 user decision); background quota rounds skip lapsed tokens; idle cards show a past expiry
- [Smart routing decisions](smart-routing-decisions.md) — virtual model trigger, judge is not a Provider, session pin by default, neither a fallback nor the lowest grade is pinned, a grade names its provider (limited tokens keep to theirs), the request log marks each decision, the OpenAI dialect is still a draft
- [Smart routing's cold judge](smart-routing-cold-judge.md) — the first real use timed out on every cold connection and sent "1+1" to the fallback grade; why the judge has its own long-lived HTTP/2 client
- [Enterprise join uses API key](enterprise-join-uses-api-key.md) — `astra join` takes gateway URL + company-issued API key; join codes rejected
- [Gateway read cache](gateway-read-cache.md) — why per-request SQLite reads go through RowCache (Generation + 2 s MaxAge), and the SQLite mutex/PRAGMA findings behind it
- [/api/clients drove server memory](clients-endpoint-memory.md) — 400–900 MB idle = tray's 10 s poll re-parsing a 2.5 MB Codex catalog + an unused O(n·m) diff; fixed 2026-10-30 (caches, ConserveMemory 5, tray polls /api/clients/summary)
- [Usage writer ceiling](usage-writer-ceiling.md) — 2,000-record buffering trades ~6% sustained throughput for ~39% lower RSS; 1,000 cost more throughput; ordinal binding +73% is isolated AOT; include drain
- [Dapper.AOT sequence cast](dapper-aot-sequence-cast.md) — a List<anonymous> sequence parameter throws InvalidCastException at run time; pass IEnumerable
- [astra-server --help starts a server](astra-server-flag-first-serves.md) — a flag-first call or `serve --help` runs `serve` on ~/.astra and overwrites runtime.json; use ASTRA_HOME for throwaway servers
- [Harness thinking picker needs reasoningEfforts](harness-reasoning-efforts-field.md) — `reasoning` is a route default; models without `reasoningEfforts` get no effort picker (issue #7)
- [Client default provider is read-side](client-default-provider-read-side.md) — a client with no bindings reads as bound to the first usable provider; stored on enable or a bindings edit, never on read
- [WorkBuddy's Anthropic route](workbuddy-anthropic-route.md) — only hy3 / hy3-x answer on /v1/messages (China build), no thinking switch there; the plugin declares them @ai-sdk/anthropic (probed 2026-10-20)
- [Provider key alternatives](provider-key-alternatives.md) — issues #8/#9: wrong-template keys are fixed by template-switch + `keyAlternatives`, not by key prefix; no endpoint speed test yet
- [Relay decisions](relay-decisions.md) — `astra-server relay`: same pipeline behind contracts, one template per process, per-request credentials never stored/logged or sent to a request-named address, raw model list
- [Keep-awake decisions](keep-awake-decisions.md) — issue #14 `PreventSleep`: idle sleep only, server host only, blittable DllImport, Linux `sleep` lock side effect, web server stops first on shutdown
- [Images API forwarding](images-api-forwarding.md) — issue #17: `/v1/images/*` forwarded untranslated on the same pipeline, logged as `openai-images` (not an ApiProtocol); forms stream after `model` unless guard/encoding/deadline; no pool failover
- [Trace routing visualization](trace-routing-visualization.md) — topology draws accounts as rows + a live activity rail (option B, 2026-10-30); tool args / prompt / reply text only when the user turns capture on

## Reference

## Feedback
- [Direct credentials stay direct](direct-credentials-stay-direct.md) — callable imported tokens are sent as provided; renewal needs a renewal grant, and auth rejection follows the account-switch policy
