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

## Not done yet

Hooks / instructions / sub-agents menus, project-level Skills, Claude Desktop (copy-only), Skills market, update checks for Git Skills.
