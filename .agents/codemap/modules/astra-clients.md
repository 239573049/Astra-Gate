---
module: astra-clients
sources: ["src/Astra.Clients/**"]
commit: 6324e30
updated: 2026-10-14
---
# astra-clients

> `packages/client-config` is a byte-exact TypeScript port of this module (adapters, editors, applier) used by `astra-connect`; the golden
> cases in `tests/fixtures/client-config` are run by both — see [connect](connect.md) and AGENTS.md linked update 20.

One adapter per local AI client that knows the client's config files, plans (never performs) the
edits that point the client at the Astra gateway, and inspects the resulting state; a single
applier executes those plans with first-write + rolling backups, per-keyPath original/applied
bookkeeping and drift-aware restore. It also holds the offline import readers (CC Switch, Alma,
Claude Code, Codex, magpie) and the pure install/update planning for client CLIs.
**Not responsible for:** the HTTP surface and the `clients`/`clients_bindings` rows
(`astra-server` — `ClientService`, `ClientEndpoints`), the `client_config_state` table and its
Dapper store (`astra-data` — `ClientConfigStateStore`), the declarations of `ClientKinds`,
`ClientOptions`, `ClientRecord`, `ClientConfigStateEntry`/`IConfigStateStore` and `Json`
(`astra-core`), the endpoints the clients are pointed at and the gateway-side reading of client
options (`astra-gateway`), the plugin runtime that produces `AgentPluginDescriptor`
(`astra-plugins`), and the TypeScript/Docker side (`cli`, `web-ui`, `desktop-app`, `docs-site`,
`packaging-release`).

## Entry points
| Symbol | File | Role |
|---|---|---|
| `ClientAdapterRegistry.CreateDefault` | `src/Astra.Clients/Adapters/ClientAdapterRegistry.cs:18` | Builds the per-machine registry: built-ins in `ClientKinds.All` order, then loaded plugin clients, then remembered plugin clients (restore-only). |
| `ClientAdapterRegistry.Require` / `Get` | `src/Astra.Clients/Adapters/ClientAdapterRegistry.cs:62` | Kind → adapter lookup; `Require` throws `KeyNotFoundException` on an unknown kind. |
| `ClientConfigApplier.Apply` | `src/Astra.Clients/Config/ClientConfigApplier.cs:50` | Executes a plan: backups, atomic writes, state recording. |
| `ClientConfigApplier.Repair` | `src/Astra.Clients/Config/ClientConfigApplier.cs:ClientConfigApplier.Repair` | Rewrites the files an adapter lists as strict JSON into strict JSON (`StrictJson.Repair`): comments and trailing commas only, same backups as any write, no key state. |
| `ClientConfigApplier.Disable` / `ForceRestore` / `RestoreAll` | `src/Astra.Clients/Config/ClientConfigApplier.cs:ClientConfigApplier.Disable` | Drift-aware restore; forced restore; the offline `restore-all [--purge]` loop over adapters. |
| `ClientConfigApplier.Preview` / `ListBackups` / `RestoreFromBackup` | `src/Astra.Clients/Config/ClientConfigApplier.cs:35` | Simulation without writes; backup listing; restore of one backup directory. |
| `ClientInstallPlanner.Plan` | `src/Astra.Clients/Install/ClientInstallPlanner.cs:42` | Pure install/update decision for one client (`Npm`/`VsCodeExtension`/`Script`), with a `Blocker` instead of a command. |
| `ClientInstallCatalog.All` / `Get` | `src/Astra.Clients/Install/ClientInstallCatalog.cs:89` | The only source of commands Astra is willing to run (package, self-update args, script URLs). |
| `ImportSourceRegistry.CreateDefault` / `ReadAll` | `src/Astra.Clients/Import/ImportSupport.cs:128` | The five read-only provider importers plus normalization/merge rules. |
| `PluginClientAdapter.PlanRestoreStale` | `src/Astra.Clients/Adapters/PluginClientAdapter.cs:158` | Puts back keys a plugin's `connect` stopped returning. |

## Key types and functions
| Symbol | File | Role |
|---|---|---|
| `IClientAdapter` | `src/Astra.Clients/Config/IClientAdapter.cs:8` | The adapter contract: `Kind`, `Mode`, `Availability`, `Detect`, `ConfigPaths`, `PlanEnable`/`PlanDisable`/`PlanPurge`, `Inspect`. |
| `ClientAdapterBase` | `src/Astra.Clients/Config/ClientAdapterBase.cs:7` | Shared plumbing: `PrimaryConfigPath`, `BuildPlan` (per-file unified diff by simulating), `CurrentValue`, `FormatOfPath`, `BuildStatus` (drift by comparing state to file), `StillOurs`, `ModelEntries`/`ModelHints`. |
| `ConfigEditorOps.Create` | `src/Astra.Clients/Config/ConfigEditorOps.cs:26` | Format → editor facade over `TomlEditor`/`JsoncEditor`/`DotEnvEditor`/`YamlEditor`/`ShellBlockEditor`; `Read`/`Apply`/`RestoreSource`/`Remove`. |
| `ConfigValueCodec` | `src/Astra.Clients/Config/ApplyReports.cs:8` | JSON-vs-raw encoding of plan/state values, and structure-aware equality (JSON/YAML) vs ordinal comparison (TOML/env/shell). |
| `ConfigChange` / `ConfigChangePlan` / `ConfigFileFormat` / `ConfigChangeKind` | `src/Astra.Clients/Config/EnableContext.cs:82` | One key or owned container edit; the reviewable plan (`Changes` + `Diffs`, `ByFile`); the five formats; `Key` vs `Table`. |
| `EnableContext` | `src/Astra.Clients/Config/EnableContext.cs:7` | What an enable writes with: gateway base URL, this client's local key, selected model, extras JSON, `ModelsWithoutContext1M`. |
| `ClientEnvironment` | `src/Astra.Clients/Config/ClientEnvironment.cs:10` | Every machine input (home, os, env vars, file IO, tool lookup). `Real` (`:31`) for production, an injected fake in tests; `LoginShellPath` (`:170`) resolves the user's terminal PATH. |
| `ClientStatus` / `ClientDetection` / `ClientMode` / `ClientAvailability` | `src/Astra.Clients/Config/ClientStatus.cs:6` | Status card: detection, enabled, `DriftedKeys`, `AppliedKeys`, warnings; `Switch` vs `Coexist`; `Available` vs `ComingSoon`. |
| `JsoncEditor` | `src/Astra.Clients/Editing/JsoncEditor.cs:18` | Byte-splice JSONC editing preserving comments/format; selectors (`Selector`, `IsSelector`) address array elements. |
| `TomlEditor` / `TomlValueText` | `src/Astra.Clients/Editing/TomlEditor.cs:16`, `:417` | Tomlyn-syntax-based TOML value/table editing; `ForString` renders raw TOML string text. |
| `YamlEditor` | `src/Astra.Clients/Editing/YamlEditor.cs:26` | YAML mappings and sequence *rows* (`[id=x].config.model`), values as JSON. |
| `DotEnvEditor` / `ShellBlockEditor` | `src/Astra.Clients/Editing/DotEnvEditor.cs:10`, `ShellBlockEditor.cs:9` | `.env` keys; one `# >>> astra <id> >>>` … `# <<< astra <id> <<<` block in a shell rc file. |
| `DiffTool.Unified` | `src/Astra.Clients/Editing/DiffTool.cs:13` | Unified diff shown in the enable preview. |
| `EditorException` | `src/Astra.Clients/Editing/EditorException.cs:4` | The module's failure type; the server maps it to HTTP 400. |
| `ImportContext` / `IImportSource` | `src/Astra.Clients/Import/ImportSupport.cs:12` | Reader inputs (fake-friendly) and the read-only source contract. |

Adapter files that exist today, all under `src/Astra.Clients/Adapters/`: `CodexClientAdapter.cs`,
`ClaudeCodeClientAdapter.cs`, `ClaudeDesktopClientAdapter.cs`, `GeminiCliClientAdapter.cs`,
`OpenCodeClientAdapter.cs` (also serves `mimo-code` via `OpenCodeFlavour`),
`GrokBuildClientAdapter.cs`, `PiClientAdapter.cs`, `HermesAgentClientAdapter.cs`,
`MiniMaxCodeClientAdapter.cs`, `CopilotCliClientAdapter.cs`,
`VsCodeCopilotClientAdapter.cs` (stable / Insiders / VSCodium via `VsCodeFlavour`),
`CrushClientAdapter.cs`, `QwenCodeClientAdapter.cs`, `DroidClientAdapter.cs`,
`KimiCodeClientAdapter.cs`, `ZedClientAdapter.cs`, `OmpClientAdapter.cs`,
`DeepSeekHarnessClientAdapter.cs`, `WorkBuddyClientAdapter.cs` (also `workbuddy-ai`),
`MuseCodeClientAdapter.cs`, `NextCoWorkClientAdapter.cs`, `PaseoClientAdapter.cs`,
`ZCodeClientAdapter.cs`, `TKCodeClientAdapter.cs`, plus `PluginClientAdapter.cs` for `plugin-<agent id>` kinds and `ClientAdapterRegistry.cs`.
`ClientAvailability.ComingSoon` is used only by an unavailable plugin client today
(`PluginClientAdapter.Availability`); every built-in adapter is `Available`.

## Dependencies
- Uses: `astra-core` via import — `ClientKinds`/`ClientRecord`/`ClientConfigStateEntry`/`IClientConfigStateStore`
  (`src/Astra.Core/Clients/ClientContracts.cs:4`, `:88`, `:128`, `:143`), `ClientOptions`/`ClientOptionDescriptor`
  (`src/Astra.Core/Clients/ClientOptions.cs:10`), `ClaudeCodeModels`/`ClaudeDesktopRoles`, `Json`/`JsonContexts`
  (`src/Astra.Clients/Config/ClientConfigApplier.cs:414`), `AgentPluginDescriptor` (`src/Astra.Clients/Adapters/PluginClientAdapter.cs:19`).
- Uses: `astra-data` via DB — only through the injected `IClientConfigStateStore`; the production
  binding is `ClientConfigStateStore` (`src/Astra.Data/Repositories/ClientConfigStateStore.cs:10`)
  wired in `AstraDatabase`.
- Used by: `astra-server` via import — `ClientService` (`src/Astra.Server/Api/ClientService.cs:41`, `:43`,
  `:363`), `Commands.RestoreAllAsync` (`src/Astra.Server/Commands.cs:21`), `ClientInstallService`
  (`src/Astra.Server/Api/ClientInstallService.cs:121`), `ImportService` (`src/Astra.Server/Api/ImportService.cs:41`),
  registered in `AstraApp` (`src/Astra.Server/Hosting/AstraApp.cs:81`).
- Used by: `cli` via process — `astra restore-all` spawns the server binary's offline subcommand
  (`cli/src/commands/restore-all.ts:9`).
- Used by: `astra-gateway` via DB, not import — the gateway re-derives request behaviour from the
  client row's `extra_json` (`ClientOptions.GetCacheTtl` at `src/Astra.Gateway/Pipeline/GatewayPipeline.cs:297`,
  `ClaudeDesktopRoles.Map` at `src/Astra.Gateway/Pipeline/GatewayRouter.cs:43`); the contract is the
  extras JSON the adapters' `PlanEnable` consumes and the server stores.

## State it owns
- Backups under `AstraPaths.ClientBackupsDir` (`~/.astra/backups/client-configs`, `src/Astra.Core/AstraPaths.cs:21`):
  `<kind>/first-write/` with `manifest.json` plus one copy of each file as it was before Astra's
  very first write (never pruned), and `<kind>/<yyyyMMdd-HHmmss-fff>[-n]/` rolling snapshots, at
  most 20 kept (`ClientConfigApplier.MaxRollingBackups`, `:16`, `PruneRollingBackups` `:464`).
  A manifest entry is `BackupFileEntry(OriginalPath, Existed, BackupFile)` (`Config/ApplyReports.cs:102`);
  `Existed: false` is how restore knows a *file* was created by Astra.
- The client config files themselves — only the keyPaths in a plan are touched; the rest of the
  user's bytes are preserved (`PruneEmptyParents` `:230`, `ReconcileToOriginal` `:265`).
- NOT `client_config_state`: the module declares `IClientConfigStateStore` and the meaning of its
  columns (`OriginalAbsent`/`OriginalValueJson`/`AppliedValueJson`), `astra-data` owns storage and
  `astra-server`'s `ClientService` owns the `clients` row (`enabled`, `token_id`, `selected_model`,
  `extra_json`). Never write those tables from here.

## Contracts it exposes
- `IClientAdapter` (`Config/IClientAdapter.cs:8`) — what an adapter must answer; `ClientAdapterBase`
  is the intended base.
- `ConfigChangePlan` / `ConfigChange` (`Config/EnableContext.cs:82`, `:114`) — plans are pure data:
  `Before`/`After` are JSON texts (`null` = absent; `null` after = remove), `Kind: Table` marks a
  container Astra owns whole.
- `ApplyReport` / `RestoreReport` / `RestoredKey` / `RestoreKeyOutcome`
  (`Config/ApplyReports.cs:62`, `:85`, `:70`, `:72`) — outcomes `Restored` / `Drifted` / `NotTracked`,
  `DeletedFiles`, `Warnings`, `HasDrift`.
- `ClientOptionDescriptor` (`src/Astra.Core/Clients/ClientOptions.cs:10`) — option key/type/default/scope
  (`config` written by an adapter on enable, `gateway` applied to requests), plus
  `ClientOptions.ConfigKeyPaths(kind)` (`:57`), which lists the config key paths unsetting an option
  or disabling a client must remove.
- `ClientInstallSpec` / `ClientInstallPlan` / `ToolCommand` (`Install/ClientInstallCatalog.cs:30`,
  `Install/ClientInstallPlanner.cs:34`, `:7`) — install facts and the only commands Astra will run.
- `ImportSourceResult` / `ImportCandidate` from `astra-core` (`src/Astra.Core/Import/`) plus the
  `IImportSource` reader contract.

## How to extend
- To add a client: write `XClientAdapter : ClientAdapterBase` in `Adapters/`, add the kind constant
  **and** `ClientKinds.All` entry (`src/Astra.Core/Clients/ClientContracts.cs:4`), then the registry
  list entry (`ClientAdapterRegistry.BuiltIn`, `Adapters/ClientAdapterRegistry.cs:30`). Missing the
  registry entry makes the client invisible to the UI *and* to `restore-all` (its writes stay in
  `client_config_state` forever); missing `ClientKinds.All` makes `ClientKinds.IsKnown` false and
  the server reject the kind (`ClientService.Require`). Add an `Install/ClientInstallCatalog` spec
  for the install card, `ClientKinds.ProtocolOf` (`:79`) for the wire protocol, and `WithModelList`
  (`:45`) if the config lists the bound provider's models.
- To add a client option: a `ClientOptionDescriptor` in `ClientOptions.For` (scope `config` or
  `gateway`), its `ConfigKeyPaths` entry when scope is `config` (otherwise unsetting leaves the
  written key behind), the consumer — the adapter's `PlanEnable`/`PlanDisable`, or the gateway
  reading `route.Client.ExtraJson` for scope `gateway` — plus zh/en `clients.option.<key>[.hint]`
  i18n keys and the docs table. Missing `ConfigKeyPaths` makes "unset option"/"disable" leave the
  value in the client config.
- To add a config file format: a `ConfigFileFormat` member, its `ConfigEditorOps.Create` branch
  (`Config/ConfigEditorOps.cs:26`), the editor in `Editing/`, the `ClientAdapterBase.FormatOfPath`
  mapping (`:81`), and `ConfigValueCodec.IsJsonValued` if values are exchanged as JSON.
- To add an import source: an `IImportSource` reader in `Import/` plus an entry in
  `ImportSourceRegistry.CreateDefault` (`Import/ImportSupport.cs:128`).
- To add a new key to an existing adapter: extend `PlanEnable` **and** `PlanDisable`/`ManagedEnvKeys`;
  a key only written on enable is never restored.

## Invariants and traps
- Adapters plan, the applier writes — a plan must not mutate files or state; `BuildPlan` simulates
  through the same `ConfigEditorOps` the applier uses so preview and result agree
  (`Config/IClientAdapter.cs:3`, `Config/ClientAdapterBase.cs:47`).
- Restore never overwrites a value the user changed: the current value is compared against
  `AppliedValueJson` and drifted keys come back as `Drifted` unless `force` (`Config/ClientConfigApplier.cs:152`).
- Original values are recorded only on the first insert per `(kind, file, keyPath)`; re-applies
  update only `AppliedValueJson`/`AppliedAt` (`UpsertState`, `:501`). Overwriting the original on a
  re-apply would destroy the user's pre-Astra value permanently.
- A file that did not exist before Astra's first write is deleted again on restore once it holds no
  key/value content (`HasNoUserContent`, `:319`), so restoring an untouched client leaves no stub.
- `Table` changes read as `"true"`/null, so content the client adds inside a container Astra owns
  never counts as drift (`Config/ConfigEditorOps.cs:108`, `:156`); the exception is a key the client
  itself rewrites, declared by `IsClientManagedKey` (Codex `model`, `Adapters/CodexClientAdapter.cs:145`).
- Plan and state values are never raw file text: JSON/YAML carry the value's JSON text, TOML/.env/
  shell JSON-quote the raw source (`ConfigValueCodec`, `Config/ApplyReports.cs:14`). TOML table
  changes carry a JSON object of raw TOML item texts (`Config/ConfigEditorOps.cs:79`).
- After a clean restore the applier puts the original bytes back wholesale if the document is
  semantically equal — but only when the file has no drift and existed originally (`ReconcileToOriginal`,
  `:265`, `:198`); a drifted file keeps our splice formatting.
- `PluginClientAdapter` refuses a file outside the user's home, under `ASTRA_HOME`, or in a
  credentials/start-up path (`Guarded`/`GuardedFiles`, `Adapters/PluginClientAdapter.cs:201`).
- `ConfigEditorOps` and the editors are immutable — each mutation returns a new instance and the
  `*Ops` field must be reassigned (`Config/ConfigEditorOps.cs:61`).
- `ClientEnvironment.Real` must never be constructed in a test, and adapters must go through `Env`
  for every read/write (`Config/ClientEnvironment.cs:5`).
- AOT: everything this assembly serializes (the backup manifest) is registered in
  `Config/ClientJsonContext.cs:12`; `Config/ClientTomlContext.cs` backs the Tomlyn serializer used for
  TOML canonicalisation.
- Concurrency: nothing in this module locks; the server serialises all client file changes
  (`ClientService._writes`, `src/Astra.Server/Api/ClientService.cs:44`).

## Tests
`tests/Astra.Clients.Tests/` (xunit, `Fixtures\**\*` copied to the output by the csproj). Naming is
per subject: `AdapterTests.cs`, `ExtraClientAdapterTests.cs`, `NewClientAdapterTests.cs`,
`MuseCodeClientAdapterTests.cs`, `PluginClientAdapterTests.cs`, `ClientConfigApplierTests.cs`,
`Editing/StrictJsonTests.cs`,
`ClientOptionsTests.cs`, `ClientInstallTests.cs`, `CodexModelCatalogTests.cs`,
`ImportReaderTests.cs`, and `Editing/` per editor. Every fixture builds a `TestHome`
(`tests/Astra.Clients.Tests/TestHome.cs:6`) — a temp directory used as the fake home with a
dictionary-backed environment — and an `InMemoryClientConfigStateStore`
(`tests/Astra.Clients.Tests/InMemoryClientConfigStateStore.cs:6`); no test may touch a real home.
Run one file: `dotnet test tests/Astra.Clients.Tests --filter FullyQualifiedName~ClientConfigApplierTests`
(after `dotnet build`), all: `dotnet test tests/Astra.Clients.Tests`.

## Related flows
- [client-config-takeover](../flows/client-config-takeover.md)
- [admin-api-request](../flows/admin-api-request.md)
very fixture builds a `TestHome`
(`tests/Astra.Clients.Tests/TestHome.cs:6`) — a temp directory used as the fake home with a
dictionary-backed environment — and an `InMemoryClientConfigStateStore`
(`tests/Astra.Clients.Tests/InMemoryClientConfigStateStore.cs:6`); no test may touch a real home.
Run one file: `dotnet test tests/Astra.Clients.Tests --filter FullyQualifiedName~ClientConfigApplierTests`
(after `dotnet build`), all: `dotnet test tests/Astra.Clients.Tests`.

## Related flows
- [client-config-takeover](../flows/client-config-takeover.md)
- [admin-api-request](../flows/admin-api-request.md)
