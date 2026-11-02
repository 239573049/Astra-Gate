---
name: desktop-memory-electron-vs-avalonia
description: measured footprint of the Electron shell vs the Avalonia app (window 316 → 214 MB, tray 157 → 53 MB, 4 → 1 processes) and the web-vs-Avalonia pixel fidelity (466 scenes, 1.6% mean diff); how it was measured
type: project
updated: 2026-10-30
source: session
evidence: desktop/src/main.ts:createWindow (1280×820, no persisted size), scripts/visual/compare.mjs, /Applications/Astra.app 0.13.4
---
Measured 2026-10-30 on a MacBook (Retina 2560×1664), `footprint` phys_footprint (Activity Monitor's "Memory"), the
desktop's own processes only — both shells run the same astra-server (44–49 MB), counted separately. Each run used a
throwaway `ASTRA_HOME` with its own port in `config.json`; samples at 30/60/90 s, the 90 s value quoted:

| state | Electron 44.5.1 (`desktop/`, rebuilt from current source) | Avalonia 0.13.4 AOT |
| --- | --- | --- |
| window open, 1280×820 | 316 MB (main 84, GPU helper 160, renderer 65, network 7) | 214 MB (single process) |
| window open, 960×640 | n/a (Electron always opens 1280×820) | 159 MB |
| tray only (`--hidden`) | 157 MB (main 83, GPU 24, tray-panel renderer 42, network 7) | 53 MB |

The Avalonia window figure is mostly GPU memory that scales with the window (graphics 65 MB + IOSurface 49 MB at
1280×820), so a smaller window is cheaper; its start-up peak was 312 MB. The live 0.13.4 app sat at 57 MB in the tray.

Fidelity, same day (`node scripts/visual/compare.mjs --no-fail` over `artifacts/visual`): 466 scenes compared
(19 groups × light/dark), mean absolute difference 1.6%, 430 within thresholds; the 36 over are all `tray__*` and
`smart-routing__*` (the web captures there predate the Avalonia changes of 2026-10-30), and 14 `kit__*` are
Avalonia-only. The web `traces__topology` capture also predates the activity rail, so it is not a fair pair.

**Why:** release notes, docs and the 2026-10-30 explainer video quote these numbers; "under 200 MB" holds for the tray
and a 960×640 window, not for a 1280×820 window (214 MB).
**How to apply:** re-measure the same way before quoting new numbers (an `--hidden` start for the tray state; never
the developer's real `~/.astra`, which the Electron shell would adopt or replace). Related: [[desktop-tray-relaunch]].
