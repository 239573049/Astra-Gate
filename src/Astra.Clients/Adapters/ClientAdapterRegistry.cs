using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>The six client adapters in <see cref="ClientKinds.All"/> order.</summary>
public sealed class ClientAdapterRegistry
{
    public ClientAdapterRegistry(IReadOnlyList<IClientAdapter> adapters) => All = adapters;

    /// <summary>Builds the default registry for a machine environment.</summary>
    public static ClientAdapterRegistry CreateDefault(ClientEnvironment env, IClientConfigStateStore store) => new(
    [
        new CodexClientAdapter(env, store),
        new ClaudeCodeClientAdapter(env, store),
        new GeminiCliClientAdapter(env, store),
        new OpenCodeClientAdapter(env, store),
        new ClaudeDesktopClientAdapter(env, store),
        new GrokBuildClientAdapter(env, store),
    ]);

    public IReadOnlyList<IClientAdapter> All { get; }

    public IClientAdapter? Get(string kind) => All.FirstOrDefault(a => string.Equals(a.Kind, kind, StringComparison.Ordinal));

    public IClientAdapter Require(string kind) =>
        Get(kind) ?? throw new KeyNotFoundException($"Unknown client kind '{kind}'.");
}
