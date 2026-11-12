namespace Astra.Api.Contracts;

/// <summary>Where an MCP server definition stands in one client (issue #34). Never carries environment or header values.</summary>
/// <param name="Kind">The client kind (<c>claude-code</c>, <c>codex</c>, <c>gemini-cli</c>, <c>qwen-code</c> or <c>opencode</c>).</param>
/// <param name="Wanted">Whether the server is meant to be in this client (enabled and the client chosen).</param>
/// <param name="State"><c>missing</c>, <c>applied</c>, <c>drifted</c> (Astra wrote it and it was changed since) or <c>conflict</c> (an entry of that name Astra did not write, or an unreadable file; never overwritten).</param>
/// <param name="File">The client file the entry lives in.</param>
/// <param name="Error">Why the last write or read failed.</param>
public sealed record LocalMcpPlacementDto(string Kind, bool Wanted, string State, string File, string? Error);

/// <summary>An MCP server definition Astra writes into the chosen clients; the client starts or reaches it, Astra never runs or proxies it.</summary>
/// <param name="Transport"><c>stdio</c> (a command) or <c>http</c> (a remote streamable-HTTP address).</param>
/// <param name="EnvNames">A stdio server's environment variable names; their values are never returned.</param>
/// <param name="HeaderNames">An http server's header names; their values are never returned.</param>
public sealed record LocalMcpServerDto(
    string Id, string Name, string Transport, string Command, List<string> Args, List<string> EnvNames, string Url, List<string> HeaderNames,
    bool Enabled, List<string> Clients, List<LocalMcpPlacementDto> Placements);

/// <summary>Create / update body of an MCP server definition.</summary>
/// <param name="Transport"><c>stdio</c> (default) or <c>http</c>.</param>
/// <param name="Env">Environment variables of a stdio server. On update, null keeps the stored ones and an empty object clears them.</param>
/// <param name="Headers">Request headers of an http server, with the same keep / clear rule as <paramref name="Env"/>.</param>
/// <param name="Clients">The client kinds to write it into.</param>
public sealed record LocalMcpServerInputDto(
    string Name, string? Transport, string? Command, List<string>? Args, Dictionary<string, string>? Env, string? Url,
    Dictionary<string, string>? Headers, bool? Enabled, List<string>? Clients);

/// <summary>An entry that already exists in a client's MCP list and was not written by Astra: a candidate for import.</summary>
/// <param name="Importable">False when Astra cannot carry it (<paramref name="Reason"/> says why); such an entry is shown only.</param>
/// <param name="Summary">The command line or address, without environment or header values.</param>
/// <param name="Taken">An Astra definition of this name already exists, so importing it is refused.</param>
public sealed record LocalMcpCandidateDto(string Kind, string Name, string Transport, string Summary, bool Importable, string? Reason, bool Taken);

/// <summary>Which existing entries to take over.</summary>
public sealed record LocalMcpImportDto(List<LocalMcpImportItemDto> Items);

/// <summary>One entry to take over: its name in the client with that kind.</summary>
public sealed record LocalMcpImportItemDto(string Kind, string Name);

/// <summary>What an import did: the definitions created, and the entries that were left alone with the reason.</summary>
public sealed record LocalMcpImportResultDto(List<LocalMcpServerDto> Imported, List<LocalMcpSkippedDto> Skipped);

/// <summary>An entry an import did not take over.</summary>
public sealed record LocalMcpSkippedDto(string Kind, string Name, string Reason);
