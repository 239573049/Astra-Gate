using System.Text.Json.Nodes;

namespace Astra.Api.Contracts;

/// <summary>One installed plugin as the Plugins page shows it.</summary>
/// <param name="ShortName">The name the community's READMEs use (word-guard for @astragate-community/middleware-word-guard).</param>
/// <param name="Kinds">provider, middleware, agent, pi (<see cref="PluginKinds"/>).</param>
/// <param name="Options">The options set, or null for none.</param>
/// <param name="OptionsExample">package.json magpie.options: what the options editor starts from.</param>
/// <param name="Error">Why it can't load, when it can't.</param>
/// <param name="Middleware">Its gateway middleware: hooks, calls, µs per call, failures, or why it didn't load.</param>
/// <param name="Agent">The client it adds (a magpie agent), or why that didn't load.</param>
public sealed record PluginDto(
    string Spec, string Name, string ShortName, string? Version, bool Enabled, List<string> Kinds, string? Source,
    JsonObject? Options, JsonObject? OptionsExample, string? Icon, bool IsPath, string? Error, MiddlewareStateDto? Middleware,
    PluginAgentDto? Agent);

/// <summary>The client a plugin adds: its client kind ("plugin-&lt;id&gt;") on the Clients page, or why it didn't load.</summary>
public sealed record PluginAgentDto(string? Id, string? Kind, string? Name, string? Error);

/// <summary>GET /api/plugins: the list in the user's order (the middleware chain's order) and Bun's state.</summary>
/// <param name="Preferences">The plugin picked (spec) for each provider id more than one plugin serves.</param>
/// <param name="Off">ASTRA_PLUGINS=off: none runs, none can be installed.</param>
public sealed record PluginsDto(List<PluginDto> Plugins, Dictionary<string, string> Preferences, BunStatusDto Runtime, bool Off = false);

/// <summary>POST /api/plugins: <c>spec</c> is an npm package, a git repository or a path; <c>source</c> where it came from.</summary>
public sealed record PluginAddInput(string? Spec, string? Source);

/// <summary>POST /api/plugins/update: one plugin by spec, or every one when omitted.</summary>
public sealed record PluginUpdateInput(string? Spec);

/// <summary>PUT /api/plugins/order: the specs in the order wanted (the rest keep theirs after them).</summary>
public sealed record PluginOrderInput(List<string>? Specs);

/// <summary>POST /api/plugins/prefer: have <c>spec</c> serve <c>provider</c>, which another plugin serves too.</summary>
public sealed record PluginPreferInput(string? Spec, string? Provider);

/// <summary>A way a plugin provider signs in, by its index (what the host's calls name it by).</summary>
public sealed record PluginAuthMethodDto(int Index, string Type, string Label, string? Placeholder);

/// <summary>A plugin sign-in under way: the page to open, and either a code to paste (<see cref="PasteCode"/>) or the
/// plugin noticing on its own; <see cref="DeviceCode"/> is the code the page asks to confirm, when the instructions say one.</summary>
/// <param name="State">waiting, done or failed.</param>
public sealed record PluginSignInDto(
    string Id, string State, string? Url, string? Instructions, bool PasteCode, string? DeviceCode, string? Account, string? Error);

/// <summary>POST …/prompt: the method's answers so far; the next question comes back, null when none is left.</summary>
public sealed record PluginPromptInput(int Method, JsonObject? Inputs);

/// <summary>POST …/validate: one answer to check.</summary>
public sealed record PluginValidateInput(int Method, string? Key, string? Value);

/// <summary>POST …/sign-in: start a browser sign-in; <c>account</c> re-signs that account (its id) instead of adding one.</summary>
public sealed record PluginSignInInput(int Method, JsonObject? Inputs, string? Account);

/// <summary>POST …/api-key: sign in with a key.</summary>
public sealed record PluginApiKeyInput(int Method, JsonObject? Inputs, string? Key);

/// <summary>POST …/sign-in/{id}/code: the code the sign-in page showed.</summary>
public sealed record PluginCodeInput(string? Code);

/// <summary>One account's check-in today, as the plugin answered it.</summary>
/// <param name="Day">The Beijing day it is for (yyyy-MM-dd).</param>
/// <param name="Outcome">claimed, done, ineligible, inactive, captcha or failed.</param>
public sealed record PluginCheckinDto(string AccountId, string Account, string? Day, string? Outcome, string? Message, double? Credit, int? Streak, DateTimeOffset? At);

/// <summary>A provider's daily check-in: whether its plugin can, whether it is on, and each account's last result.</summary>
public sealed record PluginCheckinStateDto(bool Supported, bool On, List<PluginCheckinDto> Accounts);

/// <summary>POST …/checkin: switch the daily check-in on or off.</summary>
public sealed record PluginCheckinSwitch(bool On);

/// <summary>One plugin magpie has, as the import would bring it over.</summary>
/// <param name="Installed">Astra has it already (by spec or package): left as it is.</param>
public sealed record MagpiePluginDto(string Spec, bool Off, bool HasOptions, bool Installed);

/// <summary>What importing from magpie would bring: its plugins.json, and how many sign-ins its plugin-auth.json holds.</summary>
public sealed record MagpieImportPreviewDto(bool Found, string Path, List<MagpiePluginDto> Plugins, int SignIns, string? Error);

/// <summary>POST /api/plugins/import-magpie: <c>signIns</c> also copies magpie's plugin sign-ins (plaintext, as both keep them).</summary>
public sealed record MagpieImportInput(bool? SignIns);

/// <summary>
/// A plugin's gateway middleware as the plugins page shows it: hooks, calls, µs per call, failures. Mirrors
/// <c>Astra.Plugins.Middleware.MiddlewareState</c> field for field (Contracts cannot reference Astra.Plugins).
/// </summary>
/// <param name="Events">The events its onEvent wants; null is every one.</param>
/// <param name="Error">Why it didn't load.</param>
/// <param name="AvgMicros">Average µs a call, JSON in and out included.</param>
public sealed record MiddlewareStateDto(
    List<string> Hooks, List<string>? Events, string? Error, long Calls, double AvgMicros, long Failures, string? LastError);

/// <summary>Bun as the plugins page shows it. Mirrors <c>Astra.Plugins.Runtime.BunStatus</c> field for field.</summary>
/// <param name="Path">The Bun in use, null when none is at hand yet.</param>
/// <param name="Version">The pinned version Astra downloads; null when ASTRA_BUN names one.</param>
/// <param name="Downloading">Whether it is being downloaded now.</param>
/// <param name="Error">Why the last download failed.</param>
/// <param name="Target">This machine's build name; null when Bun has none.</param>
public sealed record BunStatusDto(string? Path, string? Version, bool Downloading, string? Error, string? Target);
