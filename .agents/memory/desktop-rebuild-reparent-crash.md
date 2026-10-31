---
name: desktop-rebuild-reparent-crash
description: A desktop view or sheet that rebuilds its body must not put a long-lived child control into the new body — the old body still holds it, Avalonia throws "already has a visual parent", and with no global handler the app dies (issue #19)
type: project
updated: 2026-10-30
source: session
evidence: ["src/Astra.Desktop/Components/ProviderDetail/ModelsTab.cs:RemoteModelsSheet.Build", "src/Astra.Desktop/Components/ProviderDetail/PdSheet.cs:Show", "tests/Astra.Desktop.UiTests/ProviderDetailStateTests.cs:The_remote_models_sheet_opens_again_after_it_was_closed", "https://github.com/239573049/Astra-Gate/issues/19"]
---
`PdSheet.Show` calls `Build` on every opening, and `LiveView` calls `Build` again after a language change. A `Build` that
creates a new container (`Content = new StackPanel { Children = { …, _field } }`) around a control kept in a field adds that
control a second time while the previous container — no longer shown, but not cleared — is still its parent. Avalonia then
throws `InvalidOperationException: The control … already has a visual parent`. `RemoteModelsSheet` did this with its list
panel: the second click on 「从上游获取」 closed the 0.13.2 desktop app (reported 2026-10-30, issue #19).

The exception leaves a click handler or a dispatcher post unhandled, and the desktop has no `Dispatcher.UIThread.UnhandledException`
/ `AppDomain.UnhandledException` handler, so any such throw ends the process without a log.

Seen by inspection on 2026-10-30, not fixed or reproduced: `PluginAccountsView.Build` (`_sheet`), `SubscriptionAccountsView.Build`
(`_detail`, `_import`, `_login`, `_proxyLogin`) and `PrivacyEventLogView.Build` (an open `_sheet`) re-add field controls the same
way when the language changes.

**Why:** a container keeps its children until they are removed; replacing `Content` only detaches the container itself.
**How to apply:** in a method that runs more than once, create the controls it places (as most sheets do), or remove a kept
control from its old parent before adding it; open a sheet twice (and switch the language) in a headless test.
