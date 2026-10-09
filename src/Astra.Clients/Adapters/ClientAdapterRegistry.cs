using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>The client adapters in <see cref="ClientKinds.All"/> order.</summary>
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
        new PiClientAdapter(env, store),
        new HermesAgentClientAdapter(env, store),
        new MiniMaxCodeClientAdapter(env, store),
        new CopilotCliClientAdapter(env, store),
        new VsCodeCopilotClientAdapter(env, store),
        new CrushClientAdapter(env, store),
        new QwenCodeClientAdapter(env, store),
        new DroidClientAdapter(env, store),
        new KimiCodeClientAdapter(env, store),
        new ZedClientAdapter(env, store),
        new VsCodeCopilotClientAdapter(env, store, VsCodeFlavour.Insiders),
        new VsCodeCopilotClientAdapter(env, store, VsCodeFlavour.VsCodium),
        new OmpClientAdapter(env, store),
        new OpenCodeClientAdapter(env, store, OpenCodeFlavour.MiMoCode),
        new DeepSeekHarnessClientAdapter(env, store),
        new WorkBuddyClientAdapter(env, store),
        new MuseCodeClientAdapter(env, store),
        new NextCoWorkClientAdapter(env, store),
    ]);

    public IReadOnlyList<IClientAdapter> All { get; }

    public IClientAdapter? Get(string kind) => All.FirstOrDefault(a => string.Equals(a.Kind, kind, StringComparison.Ordinal));

    public IClientAdapter Require(string kind) =>
        Get(kind) ?? throw new KeyNotFoundException($"Unknown client kind '{kind}'.");
}
