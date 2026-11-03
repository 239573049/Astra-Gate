---
name: desktop-settings-compact-layout
description: The Avalonia Settings page deliberately departs from the web layout (side category nav, dense rows, ⓘ hints); visual compare against the web no longer matches there
type: project
updated: 2026-11-03
source: session
evidence: src/Astra.Desktop/Pages/Settings/SettingsView.cs:Tune, src/Astra.Desktop/Pages/Settings/SettingsSectionView.cs, src/Astra.Desktop/Components/Kit/SettingsRow.cs:DenseProperty, src/Astra.Desktop/Components/Kit/SettingsRow.cs:HintProperty
---
On 2026-11-03 the user chose a compact redesign of the desktop Settings page (option A of four: side category navigation,
over top tabs regrouped, a two-column card grid and a searchable form). The web's single General tab (~13 groups, 40+ rows)
is split on the desktop into General / Gateway / Models / Network / Usage / Logging / About next to Notifications / Remote /
Tray (`SettingsTab`, route segments `/settings/<lower-case name>`). The navigation is Arc.Blocks' `SettingsPage` / `SettingsSection` (gliding
pill, arrow keys, compact one-column mode), tuned per instance through its token resources (`SettingsPageTokens.Key(...)`
in `SettingsView.Tune`) rather than a hand-drawn list — a first hand-made nav looked rough next to it (user, 2026-11-03). Rows under the page are dense (`SettingsRow.Dense`,
inherited, set only by `SettingsView`); static explanations are an ⓘ tooltip (`SettingsRow.Hint`, `SettingsGroup.Hint`,
`RowBuilder.Hinted` / `WithHint`), while dynamic details (addresses, versions, status) stay as the second line. The one
security exception: the access-key row shows `settings.requireKeyOffNote` as a warning line while the key is off.
The web `SettingsPage` was not changed.

**Why:** the long page was hard to scan (51 px two-line rows, truncated hints, a tab bar not aligned with the centred
content); the user asked for a compact style.

**How to apply:** a new desktop setting goes into the category it belongs to (a `SettingsSectionView` subclass when it is
driven by `/api/settings`), with its explanation as a hint, not a detail line. The settings scenes in
`tests/Astra.Desktop.UiTests` no longer have web counterparts with the same layout (`scripts/visual/steps/settings-privacy.mjs`
still captures the long web tab), so expect `settings__*` differences or web-only/avalonia-only warnings in
`scripts/visual/compare.mjs`. Other pages keep the web metrics: do not make `Dense` the default.
