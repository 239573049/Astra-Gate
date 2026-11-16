namespace Astra.Api.Contracts;

// The Claude bridge and the window warm-up (linked update 36). Both are admin-only surfaces; none of them carries a credential.

/// <summary>How the <c>claude</c> program is run by the bridge.</summary>
/// <param name="BinaryPath">The program to run; null = look for one.</param>
/// <param name="MaxRuns">How many <c>claude</c> processes may run at once (1..64).</param>
/// <param name="IdleMinutes">How long a finished run waits for its conversation's next turn (1..120).</param>
public sealed record ClaudeBridgeSettingsDto(string? BinaryPath, int MaxRuns, int IdleMinutes);

/// <summary>PUT body for the bridge settings; omitted fields keep their value, <c>binaryPath</c> "" clears it.</summary>
public sealed record ClaudeBridgeSettingsPatch(string? BinaryPath, int? MaxRuns, int? IdleMinutes);

/// <summary>One <c>claude</c> process of the bridge.</summary>
/// <param name="State"><c>busy</c> (answering), <c>idle</c> (waiting for its conversation's next turn) or <c>parked</c> (waiting for the caller's tool results).</param>
public sealed record ClaudeRunDto(string Id, int? Pid, string Model, string State, DateTimeOffset Since, string ConfigDir);

/// <summary>The <c>claude</c> program found on this machine, the bridge settings and the live runs.</summary>
/// <param name="Source"><c>settings</c>, <c>path</c> or <c>installer</c>.</param>
/// <param name="ClaudeProviderId">The Claude subscription provider bridge accounts are added to; null when none exists yet.</param>
/// <param name="ClientPolicy">Which clients that provider serves (<c>claude-code-only</c> | <c>any</c>): a bridge account answers every client, but the provider's policy is checked first.</param>
public sealed record ClaudeBridgeStatusDto(
    bool Found, string? Path, string? Version, string? Source, ClaudeBridgeSettingsDto Settings, List<ClaudeRunDto> Runs,
    string? ClaudeProviderId, string? ClientPolicy);

/// <summary>Who <c>claude auth status</c> says is signed in, in one config directory.</summary>
/// <param name="Known">False when the program could not say (not found, an old one, no answer): <paramref name="Error"/> says why.</param>
public sealed record ClaudeSignInDto(bool Known, bool LoggedIn, string? Email, string? Plan, string? Method, string? Organization, string? Error);

/// <summary>POST body of "add a Claude bridge account".</summary>
/// <param name="ProviderId">The Claude subscription provider; null = the one Astra has.</param>
/// <param name="ConfigDir">The <c>claude</c> sign-in's config directory (<c>CLAUDE_CONFIG_DIR</c>); null or empty = the program's default one.</param>
/// <param name="HttpProxy">The account's own proxy; an http:// or https:// one (the program takes no SOCKS proxy).</param>
public sealed record ClaudeBridgeAccountRequest(string? ProviderId, string? DisplayName, string? ConfigDir, string? HttpProxy);

/// <summary>PATCH body of a bridge account's sign-in directory; <c>configDir</c> "" = the default one.</summary>
public sealed record ClaudeBridgeAccountPatch(string? ConfigDir);

/// <summary>A <c>claude auth login</c> that was started for an account.</summary>
/// <param name="Url">The sign-in page the program printed (it also opens the browser on a desktop); null until it printed one.</param>
/// <param name="State"><c>waiting</c> | <c>done</c> | <c>failed</c>.</param>
public sealed record ClaudeBridgeLoginDto(string? Url, string State, string? Message);

// ---- warm-up

/// <summary>Global warm-up settings.</summary>
/// <param name="Mode"><c>daily</c> | <c>auto</c> | <c>off</c>.</param>
/// <param name="Times">Local times of day, "HH:mm".</param>
/// <param name="Weekdays">0 = Sunday … 6 = Saturday; empty = every day.</param>
/// <param name="TimeZone">The zone the times are in (the usage time zone setting, else the machine's).</param>
public sealed record WarmupSettingsDto(bool Enabled, string Mode, List<string> Times, List<int> Weekdays, bool Claude, bool Codex, string TimeZone);

/// <summary>PUT body for the warm-up settings; omitted fields keep their value.</summary>
public sealed record WarmupSettingsPatch(bool? Enabled, string? Mode, List<string>? Times, List<int>? Weekdays, bool? Claude, bool? Codex);

/// <summary>One account's warm-up: its own setting and what happened last.</summary>
/// <param name="Family"><c>claude</c> or <c>codex</c>.</param>
/// <param name="Mode"><c>inherit</c> (the global setting), <c>off</c>, <c>daily</c> or <c>auto</c>.</param>
/// <param name="Times">The account's own times (<c>daily</c>); null = the global ones.</param>
/// <param name="LastResult"><c>ok</c>, <c>skipped</c> or <c>failed</c>; null when none ran yet.</param>
/// <param name="LastMessage">Why it was skipped or failed.</param>
/// <param name="NextAt">When the next one is due (<c>daily</c>), if the account is warmed at all.</param>
/// <param name="Warmed">Whether the global switch, the family and the account's own setting add up to this account being warmed.</param>
public sealed record WarmupAccountDto(
    string ProviderId, string ProviderName, string AccountId, string AccountName, string Family, string Mode, List<string>? Times, List<int>? Weekdays,
    DateTimeOffset? LastAt, string? LastResult, string? LastMessage, DateTimeOffset? NextAt, bool Warmed);

/// <summary>PATCH body for an account's warm-up; omitted fields keep their value, <c>times</c> [] = follow the global times.</summary>
public sealed record WarmupAccountPatch(string? Mode, List<string>? Times, List<int>? Weekdays);

/// <summary>The outcome of one warm-up (the schedule's or "warm up now").</summary>
public sealed record WarmupRunDto(string AccountId, string Result, string? Message, DateTimeOffset At);
