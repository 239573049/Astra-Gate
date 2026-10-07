using Astra.Core.Clients;

namespace Astra.Clients.Tests;

/// <summary>In-memory <see cref="IClientConfigStateStore"/> for tests (the production one uses Dapper).</summary>
public sealed class InMemoryClientConfigStateStore : IClientConfigStateStore
{
    private readonly Dictionary<(string Kind, string File, string Key), ClientConfigStateEntry> _entries = new();

    public IReadOnlyList<ClientConfigStateEntry> List(string clientKind) =>
        _entries.Values.Where(e => e.ClientKind == clientKind).OrderBy(e => e.FilePath, StringComparer.Ordinal)
            .ThenBy(e => e.KeyPath, StringComparer.Ordinal).ToList();

    public IReadOnlyList<ClientConfigStateEntry> ListAll() => _entries.Values.ToList();

    public ClientConfigStateEntry? Get(string clientKind, string filePath, string keyPath) =>
        _entries.TryGetValue((clientKind, filePath, keyPath), out var entry) ? entry : null;

    public void Upsert(ClientConfigStateEntry entry) =>
        _entries[(entry.ClientKind, entry.FilePath, entry.KeyPath)] = entry;

    public void Delete(string clientKind, string filePath, string keyPath) =>
        _entries.Remove((clientKind, filePath, keyPath));

    public void DeleteAll(string clientKind)
    {
        foreach (var key in _entries.Keys.Where(k => k.Kind == clientKind).ToList()) _entries.Remove(key);
    }
}
