using System.Text.Json.Nodes;

namespace Astra.Data.Backup;

/// <summary>What a SQLite table is to backup and sync.</summary>
public enum TableClass
{
    /// <summary>What the user configured: part of a backup and of sync (through a <see cref="TableSpec"/> or a special section handler).</summary>
    Config,

    /// <summary>Usage totals: only in a backup that asks for them, never in sync.</summary>
    Usage,

    /// <summary>Logs, traces, deliveries and other history. Never exported.</summary>
    Data,

    /// <summary>This machine's own bookkeeping (client file ledger, library placements, one-time codes). Never exported.</summary>
    MachineLocal,

    /// <summary>Schema bookkeeping.</summary>
    Internal,
}

/// <summary>The sections a backup is divided into: what a restore or a sync can be limited to.</summary>
public static class BackupSections
{
    public const string Settings = "settings";
    public const string Prices = "prices";
    public const string Providers = "providers";
    public const string Signins = "signins";
    public const string Tokens = "tokens";
    public const string Routing = "routing";
    public const string Privacy = "privacy";
    public const string Tools = "tools";
    public const string Profiles = "profiles";
    public const string Mcp = "mcp";
    public const string Alerts = "alerts";
    public const string Plugins = "plugins";
    public const string Usage = "usage";
    public const string Clients = "clients";
    public const string Library = "library";

    /// <summary>Apply order: what other sections point at comes first (providers before tokens before clients).</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Settings, Prices, Providers, Signins, Tokens, Routing, Privacy, Tools, Profiles, Mcp, Alerts, Plugins, Usage, Clients, Library,
    ];

    /// <summary>The sections a plain backup and a sync cover without being asked (usage is opt-in; sign-ins are opt-in for sync).</summary>
    public static readonly IReadOnlyList<string> Default = [.. All.Where(s => s != Usage)];

    public static bool IsKnown(string id) => All.Contains(id);
}

/// <summary>
/// One SQLite table's way into a backup. A <see cref="Parent"/>-less spec makes one document per row; a spec with a parent
/// makes the rows an array inside the parent's document (<c>provider_models</c> inside the provider). Every column of the table
/// is exported (<c>SELECT *</c>) except <see cref="Skip"/>, so a column a later migration adds travels without anyone listing it.
/// </summary>
/// <param name="Name">The spec's name: the key of a child array inside its parent's document.</param>
/// <param name="Table">The table.</param>
/// <param name="Section">Which <see cref="BackupSections"/> the table's documents belong to.</param>
/// <param name="Key">Columns that identify a row (a child's include the parent column).</param>
/// <param name="Secret">Columns holding a DataProtection value: exported decrypted under <c>$secrets</c>, only when keys are included.</param>
/// <param name="WithSecret">Columns derived from a secret (a token's hash and prefix): travel only with the secret.</param>
/// <param name="Skip">Columns that are this machine's own or runtime state (autoincrement ids, cooldowns, last status).</param>
/// <param name="Where">SQL condition restricting the rows (the table is shared by two specs).</param>
/// <param name="Parent">The table of the parent document, for a child spec.</param>
/// <param name="ParentColumns">For a child: the child's columns holding the parent's key, in the parent's key order.</param>
/// <param name="RequiresSecret">A row without its secret is useless (an OAuth token set): it is left out of a backup without keys.</param>
/// <param name="UpdateOnly">Rows that do not exist here are not inserted (a plugin whose package is not installed).</param>
public sealed record TableSpec(
    string Name,
    string Table,
    string Section,
    string[] Key,
    string[]? Secret = null,
    string[]? WithSecret = null,
    string[]? Skip = null,
    string? Where = null,
    string? Parent = null,
    string[]? ParentColumns = null,
    bool RequiresSecret = false,
    bool UpdateOnly = false)
{
    public bool IsChild => Parent is not null;

    /// <summary>Columns of <see cref="Secret"/> and <see cref="WithSecret"/> together.</summary>
    public IEnumerable<string> SecretBound => (Secret ?? []).Concat(WithSecret ?? []);
}

/// <summary>
/// The classification of everything in the database: which tables are configuration, which are history, which are this machine's own.
/// <c>BackupCatalogTests</c> fails when a migration adds a table that is not listed here, so nothing is silently left out of a
/// backup (or silently put into one). Per-key and per-field rules for the <c>settings</c> table are here as well.
/// </summary>
public static class BackupCatalog
{
    public static readonly IReadOnlyDictionary<string, (TableClass Class, string Why)> Tables = new Dictionary<string, (TableClass, string)>(StringComparer.Ordinal)
    {
        ["schema_version"] = (TableClass.Internal, "migration bookkeeping"),
        ["settings"] = (TableClass.Config, "settings documents, per key (see SettingsKeys)"),
        ["models"] = (TableClass.Config, "model edits (seed rows nobody changed are not exported)"),
        ["model_prices"] = (TableClass.Config, "price edits, inside the model's document"),
        ["providers"] = (TableClass.Config, "providers"),
        ["provider_accounts"] = (TableClass.Config, "API keys (inside the provider) and sign-in accounts (signins)"),
        ["provider_models"] = (TableClass.Config, "inside the provider's document"),
        ["clients"] = (TableClass.Config, "restored through ClientService (the enable flow), never row by row"),
        ["client_bindings"] = (TableClass.Config, "restored through ClientService (the enable flow), never row by row"),
        ["client_config_state"] = (TableClass.MachineLocal, "the ledger of the files Astra wrote on this machine"),
        ["requests"] = (TableClass.Data, "request log"),
        ["request_usage_items"] = (TableClass.Data, "request log"),
        ["request_attempts"] = (TableClass.Data, "request log"),
        ["trace_spans"] = (TableClass.Data, "traces"),
        ["tokens"] = (TableClass.Config, "gateway tokens"),
        ["token_usage_totals"] = (TableClass.Usage, "usage history"),
        ["token_usage_daily"] = (TableClass.Usage, "usage history"),
        ["provider_account_usage_totals"] = (TableClass.Usage, "usage history"),
        ["claude_direct_requests"] = (TableClass.Data, "request history of the direct Claude mode"),
        ["plugins"] = (TableClass.Config, "installed plugins (only those whose package is installed here are updated)"),
        ["plugin_provider_prefs"] = (TableClass.Config, "inside the plugin's document"),
        ["webhooks"] = (TableClass.Config, "webhooks"),
        ["alert_rules"] = (TableClass.Config, "alert rules"),
        ["alert_rule_webhooks"] = (TableClass.Config, "inside the rule's document"),
        ["alert_states"] = (TableClass.Data, "firing state, recomputed by the engine"),
        ["webhook_deliveries"] = (TableClass.Data, "delivery history"),
        ["token_invites"] = (TableClass.MachineLocal, "one-time join codes"),
        ["mcp_servers"] = (TableClass.Config, "MCP upstream servers"),
        ["mcp_oauth"] = (TableClass.Config, "their OAuth tokens (with keys only)"),
        ["mcp_calls"] = (TableClass.Data, "MCP call history"),
        ["mcp_local_servers"] = (TableClass.Config, "the user's own MCP servers"),
        ["library_skills"] = (TableClass.Config, "the Skill library (rows + files, restored through SkillLibrary)"),
        ["library_placements"] = (TableClass.MachineLocal, "paths of the links Astra made on this machine"),
        ["library_projects"] = (TableClass.MachineLocal, "absolute project directories of this machine"),
        ["library_writes"] = (TableClass.MachineLocal, "ledger of the files / blocks the library wrote on this machine"),
        ["library_router"] = (TableClass.MachineLocal, "which clients have the skill router link on this machine"),
        ["library_items"] = (TableClass.Config, "instructions / agents / commands / hooks / env of the library (rows only; the client files are written again by the library's own apply)"),
        ["plugin_kv"] = (TableClass.Config, "v2 plugin data"),
        ["plugin_secrets"] = (TableClass.Config, "v2 plugin secrets (with keys only)"),
        ["plugin_grants"] = (TableClass.Config, "v2 plugin permission grants"),
        ["plugin_activity"] = (TableClass.Data, "plugin call history"),
        ["plugin_successor_dismissals"] = (TableClass.Config, "dismissed plugin successor hints"),
        ["routing_groups"] = (TableClass.Config, "routing groups"),
    };

    private static readonly string[] AccountRuntime = ["cooldown_until_utc", "last_error"];

    /// <summary>The tables that have a spec or a special handler; every <see cref="TableClass.Config"/> table must be among them or a child of one.</summary>
    public static readonly IReadOnlyList<TableSpec> Specs =
    [
        new("models", "models", BackupSections.Prices, ["id"], Where: "source = 'user' OR user_modified_fields_json <> '[]'"),
        new("model_prices", "model_prices", BackupSections.Prices, ["model_id", "price_key"], Skip: ["id"], Where: "source = 'user' OR user_modified = 1", Parent: "models", ParentColumns: ["model_id"]),

        new("providers", "providers", BackupSections.Providers, ["id"], Secret: ["api_key_enc"], Skip: ["quota_json", "quota_checked_at_utc"]),
        new("provider_models", "provider_models", BackupSections.Providers, ["provider_id", "model_id"], Skip: ["id"], Parent: "providers", ParentColumns: ["provider_id"]),
        new("keys", "provider_accounts", BackupSections.Providers, ["id"], Secret: ["access_token_enc", "refresh_token_enc"],
            Skip: AccountRuntime, Where: "kind = 'api_key'", Parent: "providers", ParentColumns: ["provider_id"]),

        new("signins", "provider_accounts", BackupSections.Signins, ["id"], Secret: ["access_token_enc", "refresh_token_enc"],
            Skip: AccountRuntime, Where: "kind <> 'api_key'"),

        new("tokens", "tokens", BackupSections.Tokens, ["id"], Secret: ["key_enc"], WithSecret: ["key_hash", "key_prefix"], Skip: ["last_used_at"]),

        new("routing_groups", "routing_groups", BackupSections.Routing, ["id"]),

        new("mcp_servers", "mcp_servers", BackupSections.Mcp, ["id"], Secret: ["headers_enc"],
            Skip: ["status", "status_message", "protocol_version", "checked_at"]),
        new("mcp_oauth", "mcp_oauth", BackupSections.Mcp, ["server_id"], Secret: ["tokens_enc"], Parent: "mcp_servers", ParentColumns: ["server_id"], RequiresSecret: true),
        new("mcp_local_servers", "mcp_local_servers", BackupSections.Mcp, ["id"], Secret: ["env_enc", "headers_enc"], Skip: ["projects_json"]),
        new("library_items", "library_items", BackupSections.Library, ["id"]),

        new("webhooks", "webhooks", BackupSections.Alerts, ["id"], Secret: ["secret_enc", "headers_enc"]),
        new("alert_rules", "alert_rules", BackupSections.Alerts, ["id"]),
        new("alert_rule_webhooks", "alert_rule_webhooks", BackupSections.Alerts, ["rule_id", "webhook_id"], Parent: "alert_rules", ParentColumns: ["rule_id"]),

        new("plugins", "plugins", BackupSections.Plugins, ["spec"], Where: "COALESCE(source, '') <> 'path'", UpdateOnly: true),
        new("plugin_provider_prefs", "plugin_provider_prefs", BackupSections.Plugins, ["provider_id"], Parent: "plugins", ParentColumns: ["spec"]),
        new("plugin_kv", "plugin_kv", BackupSections.Plugins, ["plugin_id", "key"]),
        new("plugin_secrets", "plugin_secrets", BackupSections.Plugins, ["plugin_id", "name"], Secret: ["value_protected"], RequiresSecret: true),
        new("plugin_grants", "plugin_grants", BackupSections.Plugins, ["plugin_id"]),
        new("plugin_successor_dismissals", "plugin_successor_dismissals", BackupSections.Plugins, ["spec"]),

        new("token_usage_totals", "token_usage_totals", BackupSections.Usage, ["token_id"]),
        new("token_usage_daily", "token_usage_daily", BackupSections.Usage, ["token_id", "day"]),
        new("provider_account_usage_totals", "provider_account_usage_totals", BackupSections.Usage, ["provider_id", "account_id"]),
    ];

    /// <summary>Tables a special handler owns (settings, clients, library), besides the spec tables and their children.</summary>
    public static readonly IReadOnlySet<string> HandledSpecially = new HashSet<string>(StringComparer.Ordinal)
    {
        "settings", "clients", "client_bindings", "library_skills",
    };

    /// <summary>Top-level specs of a section (the ones that make a document of their own), in apply order.</summary>
    public static IEnumerable<TableSpec> TopLevel(string section) => Specs.Where(s => s.Section == section && !s.IsChild);

    public static IEnumerable<TableSpec> ChildrenOf(TableSpec parent) => Specs.Where(s => s.Parent == parent.Table && s.Section == parent.Section);

    /// <summary>
    /// Which section a <c>settings</c> key belongs to, or null for a key that stays on this machine. A key not listed is
    /// treated as configuration of the <c>settings</c> section; <c>BackupCatalogTests</c> lists every key the source names so
    /// that a new one is a decision, not an accident.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string?> SettingsKeys = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["app"] = BackupSections.Settings,
        ["request_compression"] = BackupSections.Settings,
        ["client_headers"] = BackupSections.Settings,
        ["quota"] = BackupSections.Settings,
        ["smart_routing"] = BackupSections.Routing,
        ["privacy"] = BackupSections.Privacy,
        ["tools"] = BackupSections.Tools,
        ["client_profiles"] = BackupSections.Profiles,
        ["library_market"] = BackupSections.Settings,
        // This machine's own: update checks, migration markers, the unified-gateway link, the server's own placeholder key,
        // direct-mode state and the backup / sync configuration itself.
        ["update.state"] = null,
        ["token_migration"] = null,
        ["unified_gateway"] = null,
        ["seed_version"] = null,
        ["claude_direct"] = null,
        ["privacy_placeholder_key"] = null,
        ["webdav_backup"] = null,
        [BackupSettingsKeys.Auto] = null,
        [BackupSettingsKeys.Sync] = null,
        [BackupSettingsKeys.SyncState] = null,
    };

    public static string? SectionOfSetting(string key) => SettingsKeys.TryGetValue(key, out var section) ? section : BackupSections.Settings;

    /// <summary>
    /// Fields of the <c>app</c> settings document (storage names) that describe this machine, not the user's preferences: the
    /// proxy, remote access, listening, the Claude binary, keep-awake, update source, retention and logging, exposure switches.
    /// A restore or a sync leaves this machine's values in place; the other fields travel.
    /// </summary>
    public static readonly IReadOnlyList<string> AppMachineFields =
    [
        "proxy_mode", "proxy_url", "proxy_username", "proxy_password_protected", "proxy_bypass",
        "tunnel", "claude_bridge", "prevent_sleep",
        "update_channel", "update_auto_check", "update_feed_url", "plugin_market_url",
        "debug_bodies", "trace_content", "body_retention_days", "request_retention_days",
        "require_gateway_key", "mcp_gateway", "hidden_nav", "max_request_body_mb",
    ];

    /// <summary>Whether a property name of a settings document holds a DataProtection value.</summary>
    public static bool IsSecretName(string name) =>
        name.EndsWith("_enc", StringComparison.Ordinal) || name.EndsWith("_protected", StringComparison.Ordinal);

    /// <summary>The settings value with this machine's own fields taken out (what a document carries).</summary>
    public static JsonNode StripMachineFields(string key, JsonNode value)
    {
        if (key != "app" || value is not JsonObject o) return value;
        foreach (var f in AppMachineFields) o.Remove(f);
        return o;
    }
}

/// <summary>Settings keys of the backup and sync services themselves (never exported).</summary>
public static class BackupSettingsKeys
{
    public const string Auto = "backup_auto";
    public const string Sync = "backup_sync";
    public const string SyncState = "backup_sync_state";
}
