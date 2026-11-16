using System.Text.Json.Nodes;

namespace Astra.Core.Clients;

/// <summary>
/// A named snapshot of the whole client side (a "profile", magpie's <c>save</c> / <c>use</c>): for each client whether Astra
/// is enabled for it, its providers in routing order, the chosen model, the token and the extras (options, protocol
/// overrides, Claude Code's slots, the model shortlist). Applying one goes through the regular enable / disable path, so the
/// config-restore invariants hold. Stored in the <c>settings</c> table under <see cref="ClientProfiles.SettingsKey"/>.
/// </summary>
public sealed class ClientProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ClientProfileEntry> Clients { get; set; } = [];
}

/// <summary>One client inside a <see cref="ClientProfile"/>.</summary>
public sealed class ClientProfileEntry
{
    public string Kind { get; set; } = "";
    public bool Enabled { get; set; }
    public string? TokenId { get; set; }
    public string? Model { get; set; }

    /// <summary>The client's providers in routing order.</summary>
    public List<ClientProfileBinding> Bindings { get; set; } = [];

    /// <summary>The client's extras without what Astra regenerates on every write (the model list, the plugin descriptor).</summary>
    public JsonObject? Extras { get; set; }
}

/// <summary>A provider (and the pinned subscription account, if any) of a <see cref="ClientProfileEntry"/>.</summary>
public sealed class ClientProfileBinding
{
    public string ProviderId { get; set; } = "";
    public string? AccountId { get; set; }
}

/// <summary>The stored document: the profiles and which one was applied last.</summary>
public sealed class ClientProfiles
{
    /// <summary>The key in the <c>settings</c> table (a document of its own; a handful of whole-document edits, no migration).</summary>
    public const string SettingsKey = "client_profiles";

    public List<ClientProfile> Profiles { get; set; } = [];

    /// <summary>The profile applied (or saved) last; the console shows it as active until the clients no longer match it.</summary>
    public string? ActiveId { get; set; }
}
