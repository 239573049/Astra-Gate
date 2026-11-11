# Repository memory

Index of this directory: one line per entry. Open an entry before relying on it.
Format and rules: [README.md](README.md). Personal memory lives in `.next-cowork/memory/` (not committed).

## Project
- [Skill library decisions](skill-library-decisions.md) — issue #34: `~/.astra/library/skills` (not `~/.astra-gate`), "Resources" nav group, links not copies, `SkillPlaces` table instead of adapter methods; Windows paths unverified
- [MCP gateway design](mcp-gateway-design.md) — issue #29: remote upstreams only, OAuth in phase 1 (no CIMD), no legacy-SSE front end; the SDK serves the upstream side, the front end is ours
- [Avalonia native menu is set once](avalonia-native-menu-set-once.md) — a second NativeMenu.SetMenu on a macOS window throws "The menu being updated does not match"; relabel in place
- [Windows hidden title bar leaves a caption strip](windows-hidden-title-bar-caption-hint.md) — hint -1 keeps ~31 DIP of HTCAPTION over the content; set it to the frame thickness, draw the caption buttons with roles; unverified on Windows
- [Avalonia window units](avalonia-window-units.md) — save window bounds in the screen's units (points on macOS), not render-scaled pixels; the sidebarCollapsed report was a harness leak, not a bug in real launches
- [Desktop page lifetime](desktop-page-lifetime.md) — pages subscribe to shared queries via Subscribe/OnDispose (2026-10-27 leak + Requests redraw storm); one Arc-held instance per page type is expected; polled queries a view rebuilds from use SameData + a QueryWatch select (issue #16)
- [Desktop settings compact layout](desktop-settings-compact-layout.md) — the Avalonia Settings page has a side category nav, dense rows and ⓘ hints on purpose; it no longer mirrors the web's long General tab
- [Desktop clients compact layout](desktop-clients-compact-layout.md) — the Avalonia Clients page has a filtered compact list, a tabbed panel and one pending-changes bar on purpose (option A, 2026-11-04); web unchanged
- [Rebuilt bodies must not re-add kept controls](desktop-rebuild-reparent-crash.md) — a Build that wraps a field control in a new container throws "already has a visual parent" and kills the app (issue #19, 2026-10-30; again in the daily-bill sheet and overview header, 2026-11-11); three language-change cases still open
- [Desktop tray relaunch](desktop-tray-relaunch.md) — a shown window leaves GPU memory nothing frees; 15 s after a close the app restarts `--hidden` (~250 → ~50 MB, 2026-10-30), which builds no main window; what the ~50 MB floor is; guards and hand-over rules
- [Electron vs Avalonia memory](desktop-memory-electron-vs-avalonia.md) — measured 2026-10-30: window 316 → 214 MB, tray 157 → 53 MB, 4 → 1 processes; web-vs-Avalonia 466 scenes, 1.6% mean pixel diff; how to re-measure
- [Avalonia DispatcherTimer starts itself](avalonia-dispatchertimer-autostart.md) — the (interval, priority, callback) constructor runs the timer at once; it made tray-only processes restart in a loop
- [Windows tray menu is an Avalonia window](avalonia-win32-tray-menu-window.md) — TrayPopupRoot sized to a MenuFlyoutPresenter; without menu themes it was 0 x 0 (issue #18, fixed 0.13.2); TrayMenuWindowTests builds it headless
- [JS engine AOT fallback was rejected](jint-aot-fallback-rejected.md) — middleware stays in-process; the Bun middleware host was the failure plan, not a design
- [Plugin v2 design](plugin-v2-design.md) — draft in `design/plugin-v2/` (2026-11-11); user decided tree / date / line chart / Markdown are required, the other decisions (D1–D9, D11, D12) are still proposals
- [Jaunt replaces Jint](jaunt-replaces-jint.md) — 2026-11-11 user request; limit hits fault the engine, strict tail calls dodge the depth limit, tracked bytes only grow
- [Webhook formats were added](webhook-formats-added.md) — Feishu, DingTalk, WeCom, Slack and Discord renderers exist despite the "generic JSON only" design
- [Plugin market has one upstream](market-upstream-single-source.md) — the community registry address is fixed, and a cache from the old one is never served
- [macOS AOT needs Xcode 26](mac-aot-needs-xcode-26.md) — the 0.3.0 link failure, and why the release rows pin macos-15 plus an Xcode 2x
- [macOS 13 floor](macos-13-floor.md) — issue #27: .NET 11 needs macOS 14; a Swift lookup shim keeps 13 working (user decision 2026-11-09), statically checked, not yet run on a real 13
- [Release publishing incidents](release-publishing-incidents.md) — the 0.2.0 workflow, npm-size, truncated-upload and update-status failures
- [GUI-launched updates need a PATH](gui-launch-npm-path.md) — Dock/Finder launches cannot see node, and the manifest is keyed by RID
- [Client protocols left out on purpose](client-protocols-not-used.md) — Zed Anthropic, Hermes Responses, OpenCode Gemini, WorkBuddy Responses: why each stays translated
- [Windows autostart uses the Run key](autostart-run-key-not-schtasks.md) — schtasks logon tasks need elevation; the server never loads/unloads its own entry
- [Orphaned server after a failed update](orphaned-server-after-failed-update.md) — two servers on one data dir: UI shows the old one, the updater says up to date
- [Avalonia updater was server-only](avalonia-updater-server-only.md) — packaged apps drifted behind their server; fixed 2026-10-30 with the track rule + app self-update (option B); 0.13.5 (stable) is the first self-update target, unverified on any OS
- [Release channels decision](release-channels-decision.md) — preview is derived from the version, the preview feed merges in stable, the manifest goes up after npm
- [Self-service shows the requested model](key-self-service-requested-model.md) — holders see their request log and per-requested-model stats; never provider, account or upstream model
- [Subscription tokens refresh on use](subscription-refresh-on-use.md) — no worker renews them (2026-10-30 user decision); background quota rounds skip lapsed tokens; idle cards show a past expiry
- [Smart routing decisions](smart-routing-decisions.md) — virtual model trigger, judge is not a Provider, session pin by default, neither a fallback nor the lowest grade is pinned, a grade names its provider (limited tokens keep to theirs), the request log marks each decision, the OpenAI dialect is still a draft
- [Smart routing's cold judge](smart-routing-cold-judge.md) — the first real use timed out on every cold connection and sent "1+1" to the fallback grade; why the judge has its own long-lived HTTP/2 client
- [Enterprise join uses API key](enterprise-join-uses-api-key.md) — `astra join` takes gateway URL + company-issued API key; join codes rejected
- [Join links and the unified gateway](join-links-temp-token.md) — one-time `astra://join` links reverse the no-join-codes decision for the desktop; clients go through the local Astra, gateway key stays encrypted locally
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
- [Images API forwarding](images-api-forwarding.md) — issue #17 (Copilot images live under /v1; "not available for integrator" = renamed model id): `/v1/images/*` forwarded untranslated on the same pipeline, logged as `openai-images` (not an ApiProtocol); forms stream after `model` unless guard/encoding/deadline; no pool failover
- [Trace routing visualization](trace-routing-visualization.md) — topology draws accounts as rows + a live activity rail (option B, 2026-10-30); tool args / prompt / reply text only when the user turns capture on
- [Daily bill styles](daily-bill-styles.md) — receipt (A) + comic (E) picked from five mockups (2026-11-11); web and desktop draw the same picture; PNG export is bill.css in an SVG foreignObject
- [Avalonia clipboard images are promised](avalonia-clipboard-bitmap-promised.md) — SetBitmapAsync encodes at paste time on macOS: a disposed bitmap or an ended process gives an empty paste; use MacPasteboard (2026-11-11)
- [Copilot Responses item ids](copilot-responses-item-ids.md) — 401 "input item does not belong to this connection" = stale encrypted reasoning from an earlier session (reproduced 2026-11-03); dropping encrypted_content fixes it, dropping ids does not; the gateway answers 400 with the reason (user decision 2026-11-08)
- [CI UI test cleanup flake](ci-ui-test-cleanup-flake.md) — one random SceneCaptureTests case fails after 1 ms with "Test Case Cleanup Failure" on ubuntu; an Avalonia headless race, not the scene; does not gate release.yml
- [Claude Code identity plugin](claude-code-identity-plugin.md) — issue #24: a community middleware on Astra's own onUpstream hook (user decision 2026-11-09); the identity now lives in C# and in the plugin, move them together
- [Four-digit patch versions](four-digit-patch-versions.md) — from 0.14.3100 on the patch number stays four digits on purpose; still x.y.z, not a typo
- [dotnet-install kills the host on macOS](dotnet-install-macos-sigkill.md) — installing an SDK over ~/.dotnet leaves `dotnet` exiting 137; replace the host with a fresh copy; rollForward picks a new SDK at once

## Reference

## Feedback
- [Direct credentials stay direct](direct-credentials-stay-direct.md) — callable imported tokens are sent as provided; renewal needs a renewal grant, and auth rejection follows the account-switch policy
- [Client provider failover](client-provider-failover.md) — issue #28: bound providers fall back in binding order, global switch default on, on the web and Avalonia settings pages
