using System.Text.Json;
using Astra.Clients.Config;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Mcp;
using Astra.Data;
using Astra.Server.Hosting;

namespace Astra.Server.Api;

/// <summary>
/// The user's own MCP servers (issue #34), a local stdio command or a remote address: one definition, written into each chosen client's
/// own MCP list through <see cref="ClientMcpServerApplier"/>. Astra never starts the command and never proxies the remote address.
/// The environment and headers (often API keys) are stored protected and go only into the client files, never back out through the API.
/// </summary>
public sealed class LocalMcpService(AstraDatabase db, ISecretProtector secrets, ClientEnvironment env, AstraPaths paths)
{
    private readonly ClientMcpServerApplier _applier = new(env, db.ClientConfigState, new ClientConfigApplier(db.ClientConfigState, env, paths.ClientBackupsDir));
    private readonly SemaphoreSlim _writes = new(1, 1);

    public async Task<List<LocalMcpServerDto>> ListAsync(CancellationToken ct) =>
        (await db.McpLocal.ListAsync(ct)).Select(s => ToDto(s, null)).ToList();

    public async Task<LocalMcpServerDto> CreateAsync(LocalMcpServerInputDto input, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var server = new LocalMcpServer { Id = Ulid.NewUlid(), CreatedAt = now };
        await using var _ = await LockAsync(ct);
        if (await db.McpLocal.GetByNameAsync(input.Name?.Trim() ?? "", ct) is not null) throw new AdminApiException(409, "A local MCP server with this name exists");
        Fill(server, input, existing: null);
        await db.McpLocal.InsertAsync(server, ct);
        return ToDto(server, Sync(server, force: false, previousName: null));
    }

    public async Task<LocalMcpServerDto> UpdateAsync(string id, LocalMcpServerInputDto input, bool force, CancellationToken ct)
    {
        await using var _ = await LockAsync(ct);
        var server = await db.McpLocal.GetAsync(id, ct) ?? throw new AdminApiException(404, "Local MCP server not found");
        var previousName = server.Name;
        if (input.Name?.Trim() is { } name && name != previousName && await db.McpLocal.GetByNameAsync(name, ct) is not null)
            throw new AdminApiException(409, "A local MCP server with this name exists");
        Fill(server, input, existing: server);
        await db.McpLocal.UpdateAsync(server, ct);
        return ToDto(server, Sync(server, force, previousName));
    }

    /// <summary>Writes the server into the wanted clients again (after a drift or a conflict the user settled).</summary>
    public async Task<LocalMcpServerDto> ApplyAsync(string id, bool force, CancellationToken ct)
    {
        await using var _ = await LockAsync(ct);
        var server = await db.McpLocal.GetAsync(id, ct) ?? throw new AdminApiException(404, "Local MCP server not found");
        return ToDto(server, Sync(server, force, null));
    }

    /// <summary>Takes the server out of every client (a changed entry stays unless <paramref name="force"/>), then deletes it.</summary>
    public async Task DeleteAsync(string id, bool force, CancellationToken ct)
    {
        await using var _ = await LockAsync(ct);
        var server = await db.McpLocal.GetAsync(id, ct) ?? throw new AdminApiException(404, "Local MCP server not found");
        var left = new List<string>();
        foreach (var kind in ClientMcpServerApplier.SupportedKinds)
        {
            if (_applier.Inspect(kind, server.Name).State is ClientMcpState.Missing or ClientMcpState.Conflict) continue;
            var report = _applier.Remove(kind, server.Name, force);
            if (report.Keys.Any(k => k.Outcome == RestoreKeyOutcome.Drifted)) left.Add(kind);
        }
        if (left.Count > 0) throw new AdminApiException(409, $"The entry in {string.Join(", ", left)} was changed since Astra wrote it; remove it by hand or delete with force");
        await db.McpLocal.DeleteAsync(id, ct);
    }

    // ---------------------------------------------------------------- sync

    private Dictionary<string, string?> Sync(LocalMcpServer server, bool force, string? previousName)
    {
        var errors = new Dictionary<string, string?>();
        var wanted = Clients(server);
        foreach (var kind in ClientMcpServerApplier.SupportedKinds)
        {
            try
            {
                // a rename takes the old entry out first
                if (previousName is not null && previousName != server.Name && _applier.Inspect(kind, previousName).State is ClientMcpState.Applied or ClientMcpState.Drifted)
                    _applier.Remove(kind, previousName, force);
                if (server.Enabled && wanted.Contains(kind)) _applier.Apply(kind, Spec(server), force);
                else if (_applier.Inspect(kind, server.Name).State is ClientMcpState.Applied or ClientMcpState.Drifted) _applier.Remove(kind, server.Name, force);
            }
            catch (ClientMcpException e)
            {
                errors[kind] = e.Message;
            }
            catch (Astra.Clients.Editing.EditorException e)
            {
                errors[kind] = e.Message;
            }
        }
        return errors;
    }

    private LocalMcpSpec Spec(LocalMcpServer s) => s.Transport == LocalMcpTransports.Http
        ? new(s.Name, "", [], new Dictionary<string, string>(), s.Url, Secret(s.HeadersEnc))
        : new(s.Name, s.Command, Args(s), Secret(s.EnvEnc));

    private static List<string> Args(LocalMcpServer s) => JsonSerializer.Deserialize(s.ArgsJson, ContractsJsonContext.Default.ListString) ?? [];

    private static HashSet<string> Clients(LocalMcpServer s) =>
        (JsonSerializer.Deserialize(s.ClientsJson, ContractsJsonContext.Default.ListString) ?? []).ToHashSet(StringComparer.Ordinal);

    private Dictionary<string, string> Secret(string? enc) => enc is null
        ? []
        : JsonSerializer.Deserialize(secrets.Unprotect(enc), ContractsJsonContext.Default.DictionaryStringString) ?? [];

    private string? Protect(Dictionary<string, string> values) =>
        values.Count == 0 ? null : secrets.Protect(JsonSerializer.Serialize(values, ContractsJsonContext.Default.DictionaryStringString));

    private void Fill(LocalMcpServer s, LocalMcpServerInputDto input, LocalMcpServer? existing)
    {
        var name = input.Name?.Trim() ?? "";
        if (!LocalMcpServer.ValidName(name)) throw new AdminApiException(400, "Name must be 1-32 characters of a-z, 0-9, _ or -, and not \"astra\"");
        var transport = input.Transport?.Trim().ToLowerInvariant() ?? existing?.Transport ?? LocalMcpTransports.Stdio;
        if (transport is not (LocalMcpTransports.Stdio or LocalMcpTransports.Http)) throw new AdminApiException(400, "Transport must be stdio or http");
        var clients = (input.Clients ?? (existing is null ? [] : [.. Clients(existing)])).Distinct().ToList();
        if (clients.Find(c => !ClientMcpServerApplier.Supports(c)) is { } bad) throw new AdminApiException(400, $"{bad} has no MCP server list Astra writes");
        static bool BadName(string k) => k.Length == 0 || k.Contains('=') || k.Contains('\0') || k.Contains('\n');
        if (input.Env is { } e && e.Keys.Any(BadName)) throw new AdminApiException(400, "Environment variable names are not valid");
        if (input.Headers is { } h && h.Keys.Any(k => BadName(k) || k.Contains(':'))) throw new AdminApiException(400, "Header names are not valid");

        if (transport == LocalMcpTransports.Http)
        {
            var url = input.Url?.Trim() ?? existing?.Url ?? "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
                throw new AdminApiException(400, "Url must be an absolute http(s) address without credentials");
            s.Url = uri.ToString();
            if (input.Headers is { } headers) s.HeadersEnc = Protect(headers);
            s.Command = "";
            s.ArgsJson = "[]";
            s.EnvEnc = null;
        }
        else
        {
            var command = input.Command?.Trim() ?? existing?.Command ?? "";
            if (command.Length == 0) throw new AdminApiException(400, "Command is required");
            if (command.Contains('\n') || (input.Args ?? []).Any(a => a.Contains('\0'))) throw new AdminApiException(400, "Command or arguments are not valid");
            s.Command = command;
            s.ArgsJson = JsonSerializer.Serialize(input.Args ?? (existing is null ? [] : Args(existing)), ContractsJsonContext.Default.ListString);
            if (input.Env is { } env) s.EnvEnc = Protect(env);
            s.Url = "";
            s.HeadersEnc = null;
        }
        s.Name = name;
        s.Transport = transport;
        s.Enabled = input.Enabled ?? existing?.Enabled ?? true;
        s.ClientsJson = JsonSerializer.Serialize(clients, ContractsJsonContext.Default.ListString);
        s.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private LocalMcpServerDto ToDto(LocalMcpServer s, Dictionary<string, string?>? errors)
    {
        var wanted = Clients(s);
        var placements = ClientMcpServerApplier.SupportedKinds.Select(kind =>
        {
            var status = _applier.Inspect(kind, s.Name);
            return new LocalMcpPlacementDto(kind, s.Enabled && wanted.Contains(kind), status.State.ToString().ToLowerInvariant(), status.File,
                errors is not null && errors.TryGetValue(kind, out var err) ? err : status.Error);
        }).ToList();
        return new LocalMcpServerDto(s.Id, s.Name, s.Transport, s.Command, Args(s), [.. Secret(s.EnvEnc).Keys.Order(StringComparer.Ordinal)], s.Url,
            [.. Secret(s.HeadersEnc).Keys.Order(StringComparer.Ordinal)], s.Enabled, [.. wanted.Order(StringComparer.Ordinal)], placements);
    }

    // ---------------------------------------------------------------- import

    /// <summary>The entries already in the clients' MCP lists that Astra did not write.</summary>
    public async Task<List<LocalMcpCandidateDto>> CandidatesAsync(CancellationToken ct)
    {
        var taken = (await db.McpLocal.ListAsync(ct)).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var list = new List<LocalMcpCandidateDto>();
        foreach (var kind in ClientMcpServerApplier.SupportedKinds)
        {
            IReadOnlyList<FoundMcpEntry> found;
            try
            {
                found = _applier.Unmanaged(kind);
            }
            catch (Astra.Clients.Editing.EditorException)
            {
                continue; // a file the client cannot parse either: nothing to offer
            }
            foreach (var e in found)
            {
                var ok = e.Spec is not null && LocalMcpServer.ValidName(e.Name);
                var reason = e.Unsupported ?? (ok ? null : "the name cannot be managed (a-z, 0-9, _ or - only)");
                var transport = e.Spec?.IsRemote == true ? LocalMcpTransports.Http : LocalMcpTransports.Stdio;
                var summary = e.Spec is null ? "" : e.Spec.IsRemote ? e.Spec.Url! : string.Join(' ', [e.Spec.Command, .. e.Spec.Args]);
                list.Add(new LocalMcpCandidateDto(kind, e.Name, transport, summary, ok, reason, taken.Contains(e.Name)));
            }
        }
        return list;
    }

    /// <summary>
    /// Takes over existing entries: a definition is created from the first client's entry of a name and every client whose entry says
    /// the same is adopted (the entry is rewritten as Astra's own, with the user's original kept so removing it puts it back).
    /// </summary>
    public async Task<LocalMcpImportResultDto> ImportAsync(LocalMcpImportDto input, CancellationToken ct)
    {
        await using var _ = await LockAsync(ct);
        var imported = new List<LocalMcpServerDto>();
        var skipped = new List<LocalMcpSkippedDto>();
        foreach (var group in (input.Items ?? []).GroupBy(i => i.Name, StringComparer.Ordinal))
        {
            var entries = new List<(string Kind, LocalMcpSpec Spec)>();
            foreach (var item in group)
            {
                if (!ClientMcpServerApplier.Supports(item.Kind)) { skipped.Add(new(item.Kind, item.Name, "unsupported client")); continue; }
                var found = _applier.Unmanaged(item.Kind).FirstOrDefault(e => e.Name == item.Name);
                if (found?.Spec is null || !LocalMcpServer.ValidName(item.Name)) { skipped.Add(new(item.Kind, item.Name, found?.Unsupported ?? "not an importable entry")); continue; }
                entries.Add((item.Kind, found.Spec));
            }
            if (entries.Count == 0) continue;
            if (await db.McpLocal.GetByNameAsync(group.Key, ct) is not null)
            {
                skipped.AddRange(entries.Select(e => new LocalMcpSkippedDto(e.Kind, group.Key, "a server with this name exists in Astra")));
                continue;
            }
            var first = entries[0].Spec;
            var same = entries.Where(e => Same(e.Spec, first)).ToList();
            skipped.AddRange(entries.Except(same).Select(e => new LocalMcpSkippedDto(e.Kind, group.Key, "differs from the entry in another client")));
            var server = new LocalMcpServer
            {
                Id = Ulid.NewUlid(), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Name = group.Key, Enabled = true,
                Transport = first.IsRemote ? LocalMcpTransports.Http : LocalMcpTransports.Stdio,
                Command = first.Command, Url = first.Url ?? "",
                ArgsJson = JsonSerializer.Serialize(first.Args.ToList(), ContractsJsonContext.Default.ListString),
                EnvEnc = first.IsRemote ? null : Protect(first.Env.ToDictionary(p => p.Key, p => p.Value)),
                HeadersEnc = first.IsRemote && first.Headers is { Count: > 0 } ? Protect(first.Headers.ToDictionary(p => p.Key, p => p.Value)) : null,
                ClientsJson = JsonSerializer.Serialize(same.Select(e => e.Kind).ToList(), ContractsJsonContext.Default.ListString),
            };
            var errors = new Dictionary<string, string?>();
            var adopted = new List<string>();
            foreach (var (kind, spec) in same)
            {
                try
                {
                    _applier.Adopt(kind, spec);
                    adopted.Add(kind);
                }
                catch (Exception e) when (e is ClientMcpException or Astra.Clients.Editing.EditorException)
                {
                    errors[kind] = e.Message;
                    skipped.Add(new(kind, group.Key, e.Message));
                }
            }
            if (adopted.Count == 0) continue;
            server.ClientsJson = JsonSerializer.Serialize(adopted, ContractsJsonContext.Default.ListString);
            await db.McpLocal.InsertAsync(server, ct);
            imported.Add(ToDto(server, errors));
        }
        return new LocalMcpImportResultDto(imported, skipped);
    }

    private static bool Same(LocalMcpSpec a, LocalMcpSpec b) =>
        a.IsRemote == b.IsRemote && a.Url == b.Url && a.Command == b.Command && a.Args.SequenceEqual(b.Args)
        && Eq(a.Env, b.Env) && Eq(a.Headers ?? new Dictionary<string, string>(), b.Headers ?? new Dictionary<string, string>());

    private static bool Eq(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && v == p.Value);

    private async Task<IAsyncDisposable> LockAsync(CancellationToken ct)
    {
        await _writes.WaitAsync(ct);
        return new Releaser(_writes);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
