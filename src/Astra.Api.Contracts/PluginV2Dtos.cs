using System.Text.Json.Nodes;

namespace Astra.Api.Contracts;

// Plugins v2 (design/plugin-v2) as the admin API serves them: /api/plugins/v2/*. Titles are already in the language the
// caller asked for (?locale=). Mirrors: web/src/api/types.ts (PluginV2*, Ui*), src/Astra.Desktop.Core/Plugins.

/// <summary>A v2 plugin, installed (running or not).</summary>
/// <param name="Problem">Why it isn't running: a manifest problem, an Astra version it doesn't run on, permissions not granted.</param>
/// <param name="Missing">Manifest permissions not granted yet: the console asks for them.</param>
/// <param name="Configuration">The settings schema (configuration contribution) with titles resolved; null when it has none.</param>
/// <param name="Settings">The current settings (secret ones left out — see <paramref name="SecretsSet"/>).</param>
/// <param name="Dev">Loaded from a folder: development mode.</param>
public sealed record PluginV2Dto(
    string Id, string Spec, string Version, string Name, string? Description, string? Icon, string? Publisher, string? Homepage, string? License,
    List<string> Permissions, List<string> OptionalPermissions, List<string> Granted, List<string> Missing, List<string> Sensitive,
    bool Enabled, bool Running, bool Healthy, string? Problem, string? LastError, PluginV2ContributionsDto Contributes,
    JsonObject? Configuration, JsonObject Settings, List<string> SecretsSet, Dictionary<string, long> Counters, List<string> Warnings, bool Dev,
    List<string> Replaces);

/// <summary>What the running v2 plugins add to the shells: pages, slots, menu items, commands, settings views, flows (2.1), providers and clients (2.1).</summary>
public sealed record PluginV2ContributionsDto(
    List<PluginV2PageDto> Pages, List<PluginV2SlotDto> Slots, List<PluginV2MenuDto> Menus, List<PluginV2CommandDto> Commands,
    List<PluginV2SettingsDto> Settings, List<string> Hooks, List<string> Jobs, List<PluginV2FlowDto>? Flows = null,
    List<PluginV2ProviderDto>? Providers = null, List<string>? Clients = null);

/// <summary>A ui.flows entry (design/plugin-v2 08 §4): a view the shell opens in a sheet (a provider's sign-in).</summary>
public sealed record PluginV2FlowDto(string Plugin, string Id, string View, string Title);

/// <summary>A v2 provider (08 §2): its provider row and the flow that signs an account in, if any.</summary>
public sealed record PluginV2ProviderDto(string Plugin, string Id, string Name, string ProviderId, string? SignInFlow);

public sealed record PluginV2RouteDto(string Path, string View);

/// <param name="Nav">The sidebar group: overview, gateway, resources, tools or plugins.</param>
public sealed record PluginV2PageDto(string Plugin, string Id, string Title, string? Icon, string Nav, List<PluginV2RouteDto> Routes);

public sealed record PluginV2SlotDto(string Plugin, string Slot, string View, string? Title);

public sealed record PluginV2MenuDto(string Plugin, string Menu, string Command, string Title, string? Icon);

public sealed record PluginV2CommandDto(string Plugin, string Id, string Title, string? Icon, string? Keybinding);

public sealed record PluginV2SettingsDto(string Plugin, string View);

/// <summary>GET /api/plugins/v2: the v2 plugins, what the running ones contribute, and runtime permission requests waiting.</summary>
public sealed record PluginV2ListDto(List<PluginV2Dto> Plugins, PluginV2ContributionsDto Contributions, List<PluginPermissionRequestDto> Requests);

/// <summary>A permission a plugin asked for at run time (permissions.request), waiting for the user.</summary>
public sealed record PluginPermissionRequestDto(string Id, string Plugin, string PluginName, List<string> Permissions, DateTimeOffset At);

public sealed record PluginV2ActivityDto(DateTimeOffset At, string Call, string? Target, string Outcome);

public sealed record PluginV2LogDto(DateTimeOffset At, string Level, string Text);

/// <summary>GET /api/plugins/v2/{id}: the plugin with its activity log and its own log lines.</summary>
public sealed record PluginV2DetailDto(PluginV2Dto Plugin, List<PluginV2ActivityDto> Activity, List<PluginV2LogDto> Lines);

/// <summary>POST /api/plugins/v2/{id}/grant: the permissions the user confirmed (the manifest's, or a subset of its optional ones).</summary>
public sealed record PluginV2GrantInput(List<string>? Permissions);

/// <summary>PUT /api/plugins/v2/{id}/settings: values for the settings, and secret settings (a null value clears one; leaving one out keeps it).</summary>
public sealed record PluginV2SettingsInput(JsonObject? Values, JsonObject? Secrets);

public sealed record PluginV2CommandInput(JsonNode? Args);

/// <summary>A command's result.</summary>
public sealed record PluginV2CommandResultDto(JsonNode? Result);

/// <summary>POST /api/plugins/v2/{id}/test: one test of a dev plugin, run in the sandbox (astra plugin test).</summary>
public sealed record PluginV2TestResultDto(string File, string Name, string Status, double? Ms, string? Error);

public sealed record PluginPermissionAnswerInput(bool Allow);

/// <summary>POST /api/plugins/v2/ui/sessions: open one of a plugin's views.</summary>
/// <param name="Client">The shell's context: shell, ui, components, locale, theme, width, surface, platform, timeZone, utcOffset.</param>
/// <param name="Slot">The host slot it fills, when it fills one.</param>
/// <param name="Parent">The session that opened it (a sheet or a dialog).</param>
public sealed record UiOpenInput(string? Plugin, string? View, JsonObject? Params, JsonObject? Client, JsonObject? Query, string? Slot, string? Parent);

public sealed record UiOpenDto(string Session, long Rev, JsonObject Tree, List<string> Warnings);

/// <summary>An event from the shell: an action with its args, the local state bag, and the shell's sequence number.</summary>
public sealed record UiEventInput(string? Action, JsonNode? Args, JsonObject? Local, long Seq);

/// <summary>The shell's answer to a question (confirm, prompt, a sheet's result, a picked file).</summary>
public sealed record UiAnswerInput(string? Id, JsonNode? Value);

public sealed record UiClientInput(JsonObject? Client);

/// <summary>A file the shell hands over (base64), for pickFile / FileDrop.</summary>
public sealed record UiFileInput(string? Name, string? Type, string? Base64);

/// <summary>A v1 plugin's v2 successor in the market (design/plugin-v2 07 §3).</summary>
/// <param name="Spec">The v1 plugin's spec.</param>
/// <param name="Package">What installing it fetches (the market's package, the v2 id).</param>
public sealed record PluginSuccessorDto(
    string Spec, string V1Name, string Id, string Package, string Version, string Name, string? Notes, List<string> Permissions, List<string> Sensitive);

/// <summary>POST /api/plugins/v2/upgrade: replace the v1 plugin <paramref name="Spec"/> by its v2 successor, granting <paramref name="Grant"/>.</summary>
public sealed record PluginUpgradeInput(string? Spec, List<string>? Grant);

/// <summary>POST /api/plugins/v2/successors/dismiss: don't remind about this successor version again.</summary>
public sealed record PluginSuccessorDismissInput(string? Spec, string? Version);

/// <summary>A notification a v2 plugin sent (notifications.send): the shells poll GET /api/plugins/v2/notifications?after=.</summary>
public sealed record PluginV2NotificationDto(long Id, string Plugin, string Title, string? Body, string Tone, DateTimeOffset At);
