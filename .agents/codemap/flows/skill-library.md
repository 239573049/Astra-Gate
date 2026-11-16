# Skill library (resource library, P1)

Design: `.plan/resources-library.md` (decisions §0). UI prototype: `.plan/prototypes/resources-skills.html`.

## What it is

Skills live once in `~/.astra/library/skills/<name>/` (`AstraPaths.LibrarySkillsDir`). A client's Skills directory holds a **link** into it,
never a copy (a copy only where a link is impossible). Switching a client off removes the link only.

## Pieces

| Where | What |
|---|---|
| `Astra.Core/Library/LibraryContracts.cs` | records, `PlacementHows` / `PlacementOrigins` / `SkillTargets`, `ILibraryLedger` |
| `Astra.Data/Repositories/LibraryRepository.cs` + migration `0023_library_skills.sql` | `library_skills`, `library_placements` — the placements table is the ownership ledger |
| `Astra.Clients/Library/FsLinks.cs` | symlink → junction (`mklink /J`) → copy; `RemoveLink` is non-recursive; directory hash |
| `Astra.Clients/Library/SkillPlaces.cs` | which clients, their Skills dir, whether they read `~/.agents/skills`, which other client's dir they also read. A table, not an adapter method (no golden cases, no TS port) |
| `Astra.Clients/Library/SkillLibrary.cs` | the engine: state, switch on/off, probe/install, migrate, return, delete, take-edits / overwrite-copy. One change at a time (`SemaphoreSlim`) |
| `Astra.Clients/Library/SkillGit.cs` | hardened `git` fetch (no `-`/`transport::` addresses, no credentials in the URL, no shell) |
| `Astra.Server/Api/LibraryService.cs`, `LibraryEndpoints.cs` | `/api/library/skills/*`; probe and install are local-origin only; the probe (a temp dir for Git) lives 15 minutes |
| `Astra.Api.Contracts/LibraryDtos.cs` | wire DTOs (`ContractsJsonContext`) |
| `Astra.Server/Commands.cs` `RestoreAllAsync` | gives every Skill back; exit code 3 = a Skill is still a link (the CLI then keeps `~/.astra` on `uninstall --purge`) |
| `web/src/pages/SkillsPage.tsx`, `web/src/components/skills/*` | the page, badges, install and migrate sheets |
| `Astra.Desktop/Pages/Skills`, `Components/Skills`, `Astra.Desktop.Core/Library/SkillsApi.cs` | the Avalonia mirror: chips are clickable Arc badges (`SkillsApi.LookOf` = the web's `badgeLook`); the nav group `nav.group.resources` is in `NavTable` |

## Rules that hold it together

- Ownership: only a placement in the ledger that is still Astra's is removed (a link into the library, or a copy whose hash is unchanged). A same-named directory of the user's, or a link they repointed, is never touched.
- A client that reads the shared directory gets its link there (so every reader sees it); switching it off needs confirmation when others read it too (`SkillSetResult.NeedsConfirm`).
- Migration links back **in place** (never into the shared directory) so no client gains a Skill it never had; originals go to `~/.astra/backups/library/<timestamp>/`, last 30 kept.
- Nothing is deleted without a backup first (migrate, delete, take-edits, overwrite-copy).

## The rest of the library (linked update 37, migration 0028)

| Where | What |
|---|---|
| `Astra.Core/Library/ResourceContracts.cs` | `ResourceKinds`, `WriteModes`, `LibraryTarget`, `LibraryItemRecord`, `LibraryWriteRecord`, `ILibraryResourceLedger` |
| `Astra.Data/Repositories/LibraryResourceRepository.cs` | `library_items`, `library_writes` (the ledger), `library_projects`, `library_router` |
| `Astra.Clients/Library/ResourcePlaces.cs` | per client: instruction file, agent / command folders and formats, hook file + event names, env setting; user-wide and project |
| `Astra.Clients/Library/ResourceText.cs` | the marked section (`<!-- astra:begin id -->`), front matter, TOML text, argument styles, item JSON |
| `Astra.Clients/Library/ResourceLibrary.cs` | the engine: validate, save / sync / delete, state, projects, `RemoveAllAsync` (restore-all). Files and sections: ledger + backups; hooks / env: `ClientConfigApplier` |
| `Astra.Clients/Library/SkillLibrary.Updates.cs`, `SkillMarket.cs`, `SkillRouter.cs` | git update check / update, registry search, the router |
| `Astra.Server/Api/ResourceLibraryService.cs`, `ResourceLibraryEndpoints.cs`, `LibraryService.cs` | `/api/library/{kind}`, `/api/library/projects`, `skills/{updates,market,router,projects}`; writes are local-origin only |
| `web/src/pages/LibraryPage.tsx`, `components/library/*`, `components/skills/Skill{MarketTab,RouterPanel,ProjectSheet}.tsx` | the web UI |
| `Astra.Desktop/Pages/Library`, `Components/Library`, `Astra.Desktop.Core/Library/LibraryApi.cs` | the Avalonia Library page (not the market / router / updates of the Skills page) |

Rules: a write is recorded in `library_writes` with the exact hash / entry; removal only when still as written (otherwise the user's now); a project that is gone is `project-missing`; the router lists the Skills a client has no link for.

## Not done yet

Claude Desktop (copy-only), the Avalonia Skills page's market / router / updates / project placement, Claude Code's Read permission rules for the router's files, sub-agents naming library Skills / MCP servers, Grok TOML hooks, magpie's MCP router (out by decision).
