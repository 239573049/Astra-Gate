---
name: desktop-rebuild-reparent-crash
description: A desktop view or sheet that rebuilds its body must not put a long-lived child control into the new body — the old body still holds it, Avalonia throws "already has a visual parent", and with no global handler the app dies (issue #19)
type: project
updated: 2026-11-24
source: session
evidence: ["src/Astra.Desktop/Components/ProviderDetail/ModelsTab.cs:RemoteModelsSheet.Build", "src/Astra.Desktop/Components/ProviderDetail/PdSheet.cs:Show", "tests/Astra.Desktop.UiTests/ProviderDetailStateTests.cs:The_remote_models_sheet_opens_again_after_it_was_closed", "https://github.com/239573049/Astra-Gate/issues/19", "tests/Astra.Desktop.UiTests/DailyBillSheetTests.cs:The_bill_opens_again_after_it_was_closed", "src/Astra.Desktop/PluginUi/PluginViewHost.cs:OfferInspector", "tests/Astra.Desktop.UiTests/PluginUiRenderTests.cs:A_dev_plugin_gets_the_inspector_button_beside_its_view"]
---
`PdSheet.Show` calls `Build` on every opening, and `LiveView` calls `Build` again after a language change. A `Build` that
creates a new container (`Content = new StackPanel { Children = { …, _field } }`) around a control kept in a field adds that
control a second time while the previous container — no longer shown, but not cleared — is still its parent. Avalonia then
throws `InvalidOperationException: The control … already has a visual parent`. `RemoteModelsSheet` did this with its list
panel: the second click on 「从上游获取」 closed the 0.13.2 desktop app (reported 2026-10-30, issue #19).

The exception leaves a click handler or a dispatcher post unhandled, and the desktop has no `Dispatcher.UIThread.UnhandledException`
/ `AppDomain.UnhandledException` handler, so any such throw ends the process without a log.

It happened again on 2026-11-11, before any release: the new daily-bill sheet's `Open()` wrapped its kept switch slot and
preview in a new `StackPanel` on every opening (the user's dev run died on 「生成账单」), and `OverviewPage.BuildHeader`, rerun on a
language change, put the kept refresh button into a new `ActionGroup` (a `StackPanel` too). Both fixed; the test above opens the
sheet three times and changes the language twice, and fails with the original exception when either fix is undone.

Seen by inspection on 2026-10-30, not fixed or reproduced: `PluginAccountsView.Build` (`_sheet`), `SubscriptionAccountsView.Build`
(`_detail`, `_import`, `_login`, `_proxyLogin`) and `PrivacyEventLogView.Build` (an open `_sheet`) re-add field controls the same
way when the language changes.

2026-11-24 (reported from a dev run, screenshot): clicking a **dev** plugin's page in the desktop threw exactly this — the error
box read `ContentControl (Content = Border) already has a visual parent ContentPresenter (PART_ContentPresenter, Host =
PluginViewHost)`. `PluginViewHost.OfferInspector` waited for the session to answer whether it can be inspected (only a plugin
installed from a folder can), then set `Content = new Panel { Children = { _body, button } }` — but `_body` is the host's own
`Content` and its template's `PART_ContentPresenter` still held it. Only the trigger is dev-only; any view built this way dies
the same way. Fixed by giving the host a `_shell` `Panel` from the constructor with `_body` in it and adding the button there
(`tests/Astra.Desktop.UiTests/PluginUiRenderTests.cs:A_dev_plugin_gets_the_inspector_button_beside_its_view` fails with the
original exception when undone). The view also holds i18n rebuilds: `OfferInspector` runs once, but `Rebuild` only swaps
`_body.Content`, never the host's.

**Why:** a container keeps its children until they are removed; replacing `Content` only detaches the container itself.
**How to apply:** in a method that runs more than once, create the controls it places (as most sheets do), or remove a kept
control from its old parent before adding it; open a sheet twice (and switch the language) in a headless test. Better still:
give the view one container at construction and only ever add to / clear *its* children.
