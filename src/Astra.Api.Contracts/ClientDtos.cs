using System.Text.Json.Nodes;
using Astra.Core.Clients;
using Astra.Core;

namespace Astra.Api.Contracts;

/// <summary>Admin API payload: client detection.</summary>
public sealed record ClientDetectionDto(bool Installed, bool ConfigExists, string? Version, IReadOnlyList<string> ConfigPaths);

/// <summary>One provider bound to a client; the list order is the routing order (see <see cref="ClientRouting"/>).</summary>
public sealed record ClientBindingDto(string ProviderId, string? AccountId);

/// <summary>
/// ProviderId / AccountId describe the primary binding (first of <see cref="Bindings"/>). Protocols: the protocols a model of
/// this client can be configured with (<see cref="ClientProtocols.For"/>), default first; null when every model uses
/// <see cref="Protocol"/>. The user's per-model choices are <c>extras.protocolOverrides</c>.
/// </summary>
public sealed record ClientInfoDto(
    string Kind, string Name, ApiProtocol Protocol, string Availability, string? AvailabilityReason, string Mode,
    ClientDetectionDto Detection, string Status, bool Enabled, string? ProviderId, string? AccountId, string? SelectedModel,
    JsonObject Extras, DateTimeOffset? AppliedAt, IReadOnlyList<string> Warnings, bool RequiresRestart,
    bool ConfigOutdated = false, string? TokenId = null, ClientInstallDto? Install = null, IReadOnlyList<ClientBindingDto>? Bindings = null,
    List<ClientOptionDescriptor>? Options = null, IReadOnlyList<ConfigRepairDto>? Repairs = null, List<ApiProtocol>? Protocols = null);

/// <summary>A config file the client cannot read as it is (problem "trailing-comma" | "comment", first at line); POST …/repair fixes it.</summary>
public sealed record ConfigRepairDto(string File, string Problem, int Line);

/// <summary>Admin API payload: client preview.</summary>
public sealed record ClientPreviewDto(IReadOnlyList<ConfigChangeDto> Changes, IReadOnlyList<FileDiffDto> Diffs, IReadOnlyList<string> Warnings);

/// <summary>Admin API payload: config change.</summary>
public sealed record ConfigChangeDto(string File, string Format, string KeyPath, string? Before, string? After);

/// <summary>Admin API payload: client disable.</summary>
public sealed record ClientDisableDto(IReadOnlyList<string> Restored, IReadOnlyList<string> Drifted, ClientInfoDto Client);

/// <summary>Admin API payload: client backup.</summary>
public sealed record ClientBackupDto(string Id, DateTimeOffset CreatedAt, IReadOnlyList<string> Files, bool FirstWrite);

/// <summary>Outcome of rewriting the clients that use one token: rewritten kinds and kinds left alone (drifted / unreadable).</summary>
public sealed record TokenRewriteResult(IReadOnlyList<string> Rewritten, IReadOnlyList<string> Skipped);

/// <summary>Install / version state of one client, embedded in <see cref="ClientInfoDto"/>.</summary>
/// <param name="Method">"npm", "vscode-extension", "script" or "manual".</param>
/// <param name="Installed">The client's command (or VS Code extension / desktop app) was found.</param>
/// <param name="UpdateAvailable">The registry has a newer version than the installed one.</param>
/// <param name="InstallCommand">What "install" would run; null when installing is not offered.</param>
/// <param name="UpdateCommand">What "update" would run; null when updating is not offered.</param>
/// <param name="UpdateVia">"npm" (global npm package) or "self" (the client's own updater).</param>
/// <param name="Blocker">
/// Why an action is unavailable: "npm-missing", "vscode-missing", "script-missing", "external-install", or "shadowed"
/// (the latest version is already installed behind the copy that runs — another update would change nothing; see
/// <c>OtherCopies</c>).
/// </param>
/// <param name="Busy">An install / update / uninstall of this client is running.</param>
/// <param name="NeedsAdmin">The npm install / update / uninstall would fail for the current user: npm's global directories are not writable (typically root-owned).</param>
/// <param name="AdminAvailable">The npm command on offer can be run with administrator rights through the system prompt (macOS, or Linux with pkexec).</param>
/// <param name="AdminFixCommand">
/// When admin rights are needed: the one-time terminal command that gives npm's global directories back to the
/// user (<c>sudo chown -R "$(whoami)" …</c>), after which installs no longer need them.
/// </param>
/// <param name="OtherCopies">
/// Other copies of the client's command found after the one that runs (terminal PATH order), with their versions.
/// A newer one means an earlier install or update went where the terminal does not look first.
/// </param>
/// <param name="NotOnPath">The running copy was found outside the user's terminal PATH (e.g. only in ~/.npm-global/bin).</param>
/// <param name="UninstallCommand">
/// What "uninstall" would run; null when Astra will not uninstall this client: script installs and apps (the vendor's
/// instructions on <paramref name="HomepageUrl"/> apply), a copy npm did not install, a VS Code built-in extension.
/// </param>
public sealed record ClientInstallDto(
    string Method, string? Package, string HomepageUrl, bool Installed, string? Executable, string? Version,
    string? LatestVersion, DateTimeOffset? LatestCheckedAt, string? LatestError, bool UpdateAvailable,
    string? InstallCommand, string? UpdateCommand, string? UpdateVia, string? Blocker, bool Busy,
    bool NeedsAdmin = false, bool AdminAvailable = false, string? AdminFixCommand = null,
    IReadOnlyList<ClientCopyDto>? OtherCopies = null, bool NotOnPath = false, string? UninstallCommand = null);

/// <summary>One copy of a client's command, the version it reports, and whether that is newer than the copy that runs.</summary>
public sealed record ClientCopyDto(string Path, string Version, bool Newer);

/// <summary>
/// A running or finished install / update / uninstall (<see cref="Action"/>). <see cref="State"/>: "running", "succeeded",
/// "failed", "cancelled".
/// </summary>
/// <param name="Hint">
/// A known cause for the UI. Failed runs: "permission-denied" (npm -g cannot write its global directories),
/// "admin-cancelled" (the administrator prompt was dismissed), "timeout". Succeeded runs that did not take effect:
/// "shadowed" (the new version sits behind an older copy on PATH), "unchanged", "not-on-path", "still-installed" (an
/// uninstall removed the npm package, but another copy of the client is still found).
/// </param>
/// <param name="Elevated">The command ran with administrator rights (its output may only arrive when it ends; it cannot be stopped).</param>
public sealed record ClientInstallJobDto(
    string Kind, string Action, string State, string Command, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    int? ExitCode, IReadOnlyList<string> Log, string? Hint, bool Elevated = false);

/// <summary>
/// Body of POST /api/clients/{kind}/install: <c>action</c> is "install", "update" or "uninstall"; <c>elevated</c> runs
/// the npm command with administrator rights (system authorization prompt). An uninstall of a client Astra has taken
/// over first restores the client's original config (POST …/disable), then removes it.
/// </summary>
public sealed record ClientInstallRequest(string? Action, bool? Elevated = null);

/// <summary>Body of POST /api/clients/check-updates: <c>force</c> skips the freshness window.</summary>
public sealed record ClientUpdateCheckRequest(bool? Force);

/// <summary>A Claude subscription account Astra already holds; never carries a token.</summary>
public sealed record ClaudeDirectAccountDto(string Id, string Name, string? Email, string? Plan, string Status, bool Enabled, bool TelemetryEnabled);

/// <summary>Launcher selection: which stored subscription account an Astra-started Claude Code uses.</summary>
public sealed record ClaudeDirectStateDto(string Mode, string? AccountId, List<ClaudeDirectAccountDto> Accounts);

/// <summary>Updates the launch selection and the statistics consent of one account.</summary>
public sealed record ClaudeDirectSelectInput(string Mode, string? AccountId, bool? TelemetryEnabled);

/// <summary>Asks for the launch environment of the selected (or a named) account.</summary>
public sealed record ClaudeDirectPrepareInput(string? AccountId);

/// <summary>
/// What the local launcher needs to start the official client: environment variables only, handed over on
/// loopback and never persisted. <see cref="Env"/> carries the short-lived access token (not the refresh token).
/// </summary>
public sealed record ClaudeDirectLaunchDto(string AccountId, string AccountName, DateTimeOffset ExpiresAtUtc, Dictionary<string, string> Env);

/// <summary>
/// One request Claude Code reported on its direct connection (GET /api/clients/claude-code/direct/accounts/{id}/requests, newest first):
/// client telemetry, never gateway traffic. <see cref="Status"/> is the client's own outcome (<c>success</c> / <c>error</c>), verbatim;
/// <see cref="EstimatedCostNanoUsd"/> is the client's estimate in nano-USD, null when it reported none.
/// </summary>
public sealed record ClaudeDirectRequestDto(
    string Id, string ProfileId, string SessionId, string? AccountUuid, string Model, DateTimeOffset OccurredAtUtc, string Status,
    long InputTokens, long OutputTokens, long CacheReadTokens, long CacheCreationTokens, long? DurationMs, long? EstimatedCostNanoUsd);

/// <summary>accountId semantics: omitted = keep the current pin, "" = clear it, an id = pin that account.</summary>
public sealed record BindingInput(string? ProviderId, string? AccountId);

/// <summary>The client's full provider list in routing order (first = primary).</summary>
public sealed record BindingsInput(List<ClientBindingDto>? Bindings);

/// <summary>One file's unified diff in a client config preview (the wire shape of <c>Astra.Clients.Config.FileDiff</c>, which the Contracts assembly cannot reference).</summary>
public sealed record FileDiffDto(string File, string UnifiedDiff);
