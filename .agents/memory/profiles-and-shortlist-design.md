---
name: profiles-and-shortlist-design
description: Profiles are one settings document applied through the normal enable/disable path; the model shortlist lives in extras and filters lists server-side (no adapter change)
type: project
updated: 2026-11-16
source: session
evidence: ["src/Astra.Server/Api/ClientService.Profiles.cs:RunProfileAsync", "src/Astra.Server/Api/ProfileService.cs", "src/Astra.Core/Clients/ModelShortlist.cs", "tests/Astra.Server.IntegrationTests/ProfileApiTests.cs"]
---
Magpie-parity feature #6 (2026-11-16). Decisions and traps:

- **Storage**: profiles are one JSON document under `settings` key `client_profiles`, not a table. A handful of rows, always read and written
  whole with the active marker; migration 0031 was reserved but not needed.
- **Apply = the regular path.** `ClientService.Profiles.cs` runs `EnableCoreAsync` / `DisableCoreAsync` (the bodies of the old lambdas, extracted so
  a whole profile runs under one `_writes` lock). The `ClientConfigApplier` invariants therefore hold. Bindings are replaced first and put back if
  the enable is refused. A client the profile does not list is left alone (a never-configured client has no row, so it is in no snapshot).
- **Claude Code's `extras.models` holds the user's slots**, while for `HasModelList` clients the same key is the generated model list.
  `SnapshotExtras` drops `models` only for the latter.
- **Shortlist** (`extras.modelShortlist`) is applied where lists are built (gateway `ModelListAsync`, `ClientService.ModelsAsync`, `ContextAsync`),
  so adapters and the TS port are untouched. The empty array is the profile's explicit "no shortlist" marker so an enable's carry-over rule
  (keep the stored shortlist when `extras` omits the key) does not resurrect an old one.
- The tray polls `GET /api/profiles/summary` (names only): the full list runs a drift check that probes client detection.
- Drift ignores clients that cannot be applied here (not installed, gone), so a profile with a missing client is not "drifted" forever; a profile
  that lists no clients is never drifted.
