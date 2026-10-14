using Astra.Core;

namespace Astra.Clients.Config;

/// <summary>
/// A client whose own UI can change a model's protocol inside the entries Astra writes (NextCoWork's per-model
/// <c>protocolOverride</c>). Such a change is the user's choice: the server records it with the client's
/// <c>protocolOverrides</c> before planning, so a rewrite keeps it instead of putting the native protocol back.
/// </summary>
public interface IClientProtocolChoices
{
    /// <summary>The models whose protocol in the client's config differs from what Astra last wrote, with that protocol.</summary>
    IReadOnlyDictionary<string, ApiProtocol> ChangedProtocolChoices();
}
