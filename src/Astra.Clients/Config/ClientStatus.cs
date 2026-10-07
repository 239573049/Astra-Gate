using System.Text;

namespace Astra.Clients.Config;

/// <summary>How a client selects its AI provider.</summary>
public enum ClientMode
{
    /// <summary>The client has a single active provider; enabling rewires it to Astra.</summary>
    Switch,

    /// <summary>The client keeps its own providers; Astra is added as an additional one.</summary>
    Coexist,
}

/// <summary>Whether the adapter can actually configure the client today.</summary>
public enum ClientAvailability
{
    Available,

    /// <summary>The config format could not be verified; the UI must not offer enable.</summary>
    ComingSoon,
}

/// <summary>Result of looking for a client installation.</summary>
public sealed record ClientDetection(bool Detected, string? Version, string? Detail)
{
    public static ClientDetection NotFound(string detail) => new(false, null, detail);

    public static ClientDetection Found(string? version, string? detail) => new(true, version, detail);
}

/// <summary>Enablement state of one client, as shown in the UI status card.</summary>
public sealed class ClientStatus
{
    public required string ClientKind { get; init; }
    public required ClientDetection Detection { get; init; }
    public required bool Enabled { get; init; }

    /// <summary>Keys Astra has taken over whose current value no longer matches what we wrote.</summary>
    public required IReadOnlyList<string> DriftedKeys { get; init; }

    /// <summary>Tracked keys that are still applied (e.g. the kept Codex provider table).</summary>
    public required IReadOnlyList<string> AppliedKeys { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }
}
