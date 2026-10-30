---
name: clients-endpoint-memory
description: astra-server's 400–900 MB idle footprint was GET /api/clients (tray polls every 10 s): an O(n·m) diff nobody read and a 2.5 MB Codex catalog re-parsed per call; fixed 2026-10-30 with caches + GC ConserveMemory
type: project
updated: 2026-10-30
source: session
evidence: ["src/Astra.Server/Api/ClientService.cs:IsOutdatedAsync", "src/Astra.Clients/Config/ClientAdapterBase.cs:ChangesOnly/BuildStatus", "src/Astra.Clients/Config/ClientEnvironment.cs:TrackReads/Unchanged/ContentHash", "src/Astra.Data/Repositories/ClientConfigStateStore.cs:List", "src/Astra.Server/Astra.Server.csproj (System.GC.ConserveMemory)", "src/Astra.Desktop.Core/Tray/TrayController.cs:RefreshAsync"]
---
Measured 2026-10-30 on the 0.13.3 AOT binary (macOS, workstation GC, `footprint`): the desktop's server sat at 513 MB
(peak 855 MB) after an hour with 3 gateway requests; 471 MB of it was GC heap. A copy of the same data dir starts at
~45 MB and every admin GET is free except `GET /api/clients`: one call +97 MB, 40 calls → 898 MB footprint (961 peak).
The tray calls it every 10 s (`TrayController.RefreshAsync` → clients + providers), so an idle app ratchets up.

Two costs per call, both mostly large-object-heap garbage:
- **Enabled NextCoWork** (`~/.next-cowork/providers.json`, 4,214 lines): the status card's staleness check ran
  `PlanEnable` → `BuildPlan` → `DiffTool.Unified`, whose LCS is a full `int[n+1, m+1]` (~71 MB), and threw the diff away.
- **Codex `modelCatalog`** (`~/.codex/astra-models.json`, 2.47 MB: 41 models each carrying the ~60 KB
  `model_messages` instructions): `BuildStatus` re-read the file and the same 2.47 MB `applied_value_json` from SQLite
  and JsonNode-parsed both, every call.

Fixed the same day: `ClientAdapterBase.ChangesOnly` (no diffs for the staleness check), `ClientService.IsOutdatedAsync`
(reuses the answer while the `EnableContext` and every path the plan read — `ClientEnvironment.TrackReads`, SHA-256 per
file — are unchanged; never for plugin clients; `MutateAsync` clears it), the per-key drift cache in `BuildStatus`
(file hash + recorded value), and a signature-checked row cache in `ClientConfigStateStore.List`. Same data, 40 calls
back to back: 898 MB → 110 MB. What was left was GC policy, not live data: at the tray's pace the heap kept ~110 MB of
dead space around ~30 MB live; `System.GC.ConserveMemory` 5 (as 9) brought an idle server to 62–70 MB
(`DOTNET_gcConcurrent=0` did little: 117 MB), with no change in a call's latency (~50–90 ms). The final build (knob
compiled in, no env var): 77 MB idle after 10 polls, 87 MB after 40 back-to-back calls (0.13.3: 898 MB). The desktop
found the same knob useless — its floor is GPU memory.

Follow-up the same day: the tray polls `GET /api/clients/summary` (`ClientService.SummaryAsync`, database only:
~175 KB, <1 ms) instead of the full list (still ~13 MB, 30–50 ms per call after the caches: install info 1.4 MB, the 28
`Inspect`s 3.3 MB — NextCoWork alone ~4 MB with its context — and per-client DB reads), and `ClientInstallService`
reuses PATH lookups for 30 s (`Lookup`; jobs clear them) — they were ~2,000 stats per list. Measured with a throwaway
in-process harness: `AstraApp.Create` without starting the host, real `ClientEnvironment`, `GC.GetTotalAllocatedBytes`.

Side effect of the catalog: `client_config_state` rewrites left the 207 MB `astra.db` 97% free pages, and
`backups/client-configs/codex` holds 28 MB of catalog copies.

**Why:** the growth scales with the user's client config files, not with traffic, so a fresh install never shows it.
**How to apply:** keep polled paths free of diffs and of re-parsing unchanged files; a new adapter file read must go
through `ClientEnvironment` or the staleness cache goes stale. Measure with a scratch `ASTRA_HOME` copy under
`sandbox-exec` (deny `network-outbound` except unix sockets, deny writes under `$HOME`, unset `*_PROXY`); `sample <pid>`
on the AOT binary resolves symbols. `DiffTool` has a TS twin (`packages/client-config/src/editing/diff-tool.ts`), so
changing its algorithm is linked update 20. Related: [[astra-server-flag-first-serves]], [[usage-writer-ceiling]],
[[desktop-tray-relaunch]], [[subscription-refresh-on-use]].
