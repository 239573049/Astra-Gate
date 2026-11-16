namespace Astra.Api.Contracts;

/// <summary>One key of a search API as the admin API shows it: a mask, never the key.</summary>
/// <param name="Masked">First and last characters only.</param>
/// <param name="Resting">Why searches ask this key last now (<c>turned away</c>, <c>out of credit</c>, <c>rate limited</c>, <c>used up</c>); null when they do not.</param>
/// <param name="RestUntil">When that rest ends.</param>
public sealed record SearchKeyDto(string Id, string Masked, bool Enabled, string? Resting, DateTimeOffset? RestUntil);

/// <summary>A search API in the user's list (drag order = the order searches ask them in).</summary>
/// <param name="Vendor">A built-in vendor id, or a plugin's <c>plugin:id</c>.</param>
/// <param name="Pick"><c>turns</c> (each search the next key) or <c>order</c> (the first until it is refused).</param>
/// <param name="Url">The address when it is the user's own (SearXNG, a self-hosted Firecrawl).</param>
/// <param name="KeyOptional">The API also answers without a key.</param>
/// <param name="NeedsUrl">The user must give its address.</param>
/// <param name="Plugin">A plugin gives this API.</param>
/// <param name="Available">False for a plugin API whose plugin is not on (kept in the list, skipped by searches).</param>
/// <param name="CanReadQuota">The API reports what each key has used (Tavily).</param>
public sealed record SearchApiDto(
    string Vendor, string Name, bool Enabled, string Pick, string? Url, string? KeysUrl, bool KeyOptional, bool NeedsUrl, bool Plugin, bool Available,
    bool CanReadQuota, List<SearchKeyDto> Keys);

/// <summary>A search API that can be added to the list.</summary>
public sealed record SearchVendorDto(string Vendor, string Name, string? KeysUrl, bool KeyOptional, bool NeedsUrl, bool Plugin);

/// <summary>What one key of a search API has used of its limit. Unknown (<paramref name="Known"/> false) is never 0 used.</summary>
public sealed record SearchKeyQuotaDto(string KeyId, bool Known, bool Unlimited, long Used, long Limit, string? Of, string? Plan, long PaygoLeft, bool UsedUp, string? Error);

public sealed record WebSearchSettingsDto(bool OnBehalf, string Mode, int ResultCount, List<SearchApiDto> Apis, List<SearchVendorDto> Available);

public sealed record WebFetchSettingsDto(int MaxBytes, int TimeoutSeconds, int MaxChars, bool AllowPrivate);

public sealed record ImageToolSettingsDto(string Model, string Size);

/// <summary>Astra's built-in tools settings.</summary>
/// <param name="McpEndpoint">The address of the built-in MCP server (<c>/mcp/astra</c>), valid once a token is bound to it.</param>
public sealed record ToolsSettingsDto(WebSearchSettingsDto Search, WebFetchSettingsDto Fetch, ImageToolSettingsDto Image, string McpEndpoint);

/// <summary>A tool of the built-in server.</summary>
/// <param name="Name">The name an MCP client calls it by (the aggregate view prefixes <c>astra__</c>).</param>
/// <param name="Source"><c>builtin</c> or <c>plugin</c>.</param>
/// <param name="Available">False when it is not offered now (no search API on, no image model set).</param>
/// <param name="Reason">Why it is not available.</param>
public sealed record ToolInfoDto(string Name, string Title, string? Description, string Source, bool Available, string? Reason);

/// <summary>The outcome of "test" on a search API.</summary>
public sealed record SearchTestDto(bool Ok, string? Source, int Pages, long ElapsedMs, string? Error, string? FirstTitle);

/// <summary>A block of a tool's answer: text, or an image as base64.</summary>
public sealed record ToolContentDto(string Type, string? Text, string? MimeType, string? Data);

/// <summary>What running a tool answered (<c>POST /api/tools/run</c>).</summary>
public sealed record ToolRunDto(bool IsError, List<ToolContentDto> Content);
