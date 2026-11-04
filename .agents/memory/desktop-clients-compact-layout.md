---
name: desktop-clients-compact-layout
description: The Avalonia Clients page deliberately departs from the web layout (compact list with filter, tabbed detail panel, one pending-changes bar); visual compare against the web no longer matches there
type: project
updated: 2026-11-04
source: session
evidence: src/Astra.Desktop/Components/Clients/ClientListView.cs, src/Astra.Desktop/Pages/Clients/ClientPanelView.cs, src/Astra.Desktop/Pages/Clients/ClientsPageViewModel.cs:ClientTab, src/Astra.Desktop.Core/Clients/ClientDraft.cs:PendingCount, .plan/clients-redesign.html
---
On 2026-11-04 the user chose option A of three compact redesigns of the desktop Clients page (mockups in
`.plan/clients-redesign.html`; B was a table + drawer, C an inspector). The desktop now has:
- a 228 px list with search and an All / Enabled / Needs-attention filter (`ClientFilter`, `ClientsLogic.Matches` / `NeedsAttention`) and 30 px rows;
- a panel with a fixed header (meta line with the config path and an open-folder button), one-line bars (role-map hint, repairs, warnings, where the drift bar opens the enable preview), and tabs `ClientTab` Routing / Models / Options / Maintenance;
- an "add provider" popup picker in place of the full list of unbound providers;
- one bottom bar for choices not yet written (`ClientDraft.PendingCount`, discard = `Revert`, apply = the enable preview), replacing the per-group apply buttons. The account pin keeps its own apply, because it is a binding and is not written to the config.

The page scrolls itself (`HostScrolls => false`). The web `ClientsPage` was not changed.

**Why:** the long single-column panel was hard to scan, and its apply buttons were scattered. The user asked for a compact style.

**How to apply:** a new client setting belongs to the tab it fits. Make its explanation a hint (`SettingsGroup.Hint`), and if it is written into the config, count it in `PendingCount`. `ClientsReveal` in `Scenes.Clients.cs` opens a tab before it scrolls. Expect `clients__*` differences against the web captures in `scripts/visual/compare.mjs`. Related: [[desktop-settings-compact-layout]].
