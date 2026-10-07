namespace Astra.Clients.Config;

/// <summary>
/// One client adapter: knows the client's config files, how to plan the enable/disable/purge changes
/// (never mutating anything), and how to inspect the current state. Execution is done by
/// <see cref="ClientConfigApplier"/> so backups, state tracking and atomic writes stay in one place.
/// </summary>
public interface IClientAdapter
{
    /// <summary>Stable client kind id (see <see cref="Astra.Core.Clients.ClientKinds"/>).</summary>
    string Kind { get; }

    ClientMode Mode { get; }

    ClientAvailability Availability { get; }

    /// <summary>Why the adapter is not available yet (when <see cref="Availability"/> is ComingSoon).</summary>
    string? UnavailableReason { get; }

    /// <summary>Looks for the client on this machine (config directory, executable on PATH).</summary>
    ClientDetection Detect();

    /// <summary>Absolute config file paths this adapter owns, honoring env overrides (CODEX_HOME etc.).</summary>
    IReadOnlyList<string> ConfigPaths();

    /// <summary>Plans the changes that point this client at the Astra gateway.</summary>
    ConfigChangePlan PlanEnable(EnableContext ctx);

    /// <summary>Plans the changes that undo an enable (restores recorded originals; keeps coexisting blocks).</summary>
    ConfigChangePlan PlanDisable();

    /// <summary>Plans the changes for "restore-all --purge": also removes blocks Astra added (e.g. the Codex provider table).</summary>
    ConfigChangePlan PlanPurge();

    /// <summary>Current enablement state, drift and warnings.</summary>
    ClientStatus Inspect();
}
