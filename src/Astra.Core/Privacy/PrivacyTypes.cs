namespace Astra.Core.Privacy;

/// <summary>Actions the privacy guard can take for one detection category.</summary>
public static class PrivacyActions
{
    public const string Off = "off";
    public const string Warn = "warn";
    public const string Block = "block";
    public const string Redact = "redact";

    public static readonly IReadOnlyList<string> All = [Off, Warn, Block, Redact];
}

/// <summary>Built-in detection categories (rule sets). Custom rules use <see cref="Custom"/>.</summary>
public static class PrivacyCategories
{
    public const string ApiKey = "api_key";
    public const string AwsKey = "aws_key";
    public const string Jwt = "jwt";
    public const string PrivateKey = "private_key";
    public const string Email = "email";
    public const string Phone = "phone";
    public const string Intranet = "intranet";
    public const string CreditCard = "credit_card";
    public const string NationalId = "national_id";
    public const string Credential = "credential";
    public const string Custom = "custom";

    public static readonly IReadOnlyList<string> BuiltIn =
        [ApiKey, AwsKey, Jwt, PrivateKey, Email, Phone, Intranet, CreditCard, NationalId, Credential];
}

/// <summary>One rule match (aggregated per rule per request).</summary>
public sealed class PrivacyHit
{
    public string RuleId { get; set; } = "";
    public string Category { get; set; } = "";

    /// <summary>Action that was (or would be) taken: warn | block | redact.</summary>
    public string Action { get; set; } = PrivacyActions.Warn;

    public int Count { get; set; }

    /// <summary>Masked previews of the matched values (e.g. "john••••om"); originals are never kept. Empty when sample recording is off.</summary>
    public List<string> Samples { get; set; } = [];
}

/// <summary>One redaction: the placeholder that replaced the original for the lifetime of the request.</summary>
public sealed class PrivacyRedaction
{
    public string Placeholder { get; set; } = "";
    public string Original { get; set; } = "";
    public string Category { get; set; } = "";
    public string RuleId { get; set; } = "";
}

/// <summary>A user-defined detection rule (stored in <see cref="PrivacySettings.CustomRules"/>).</summary>
public sealed class PrivacyCustomRule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Pattern { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Privacy guard configuration, persisted in <c>settings</c> under <see cref="StorageKey"/>.
/// Actions resolve per category: <see cref="CategoryActions"/> → client default → <see cref="DefaultAction"/>.
/// </summary>
public sealed class PrivacySettings
{
    public const string StorageKey = "privacy";

    public bool Enabled { get; set; }

    /// <summary>Detect-and-record only: requests pass through untouched.</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>Replace placeholders in responses with the original values (in memory, per request).</summary>
    public bool RestoreResponses { get; set; } = true;

    /// <summary>Store masked samples of matched values in the per-request report. Originals are never stored either way.</summary>
    public bool RecordSamples { get; set; } = true;

    public string DefaultAction { get; set; } = PrivacyActions.Warn;

    /// <summary>Category (or custom rule id) → action override.</summary>
    public Dictionary<string, string> CategoryActions { get; set; } = new();

    /// <summary>Client kind → default action for that client's requests.</summary>
    public Dictionary<string, string> ClientDefaults { get; set; } = new();

    public List<PrivacyCustomRule> CustomRules { get; set; } = [];

    public PrivacySettings Clone() => Json.Deserialize<PrivacySettings>(Json.Serialize(this))!;
}

/// <summary>A materialized rule ready to run (built-in or custom).</summary>
public sealed class PrivacyRule
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required System.Text.RegularExpressions.Regex Regex { get; init; }
}

/// <summary>
/// The policy for one request: settings resolved against the client kind, with all regexes compiled.
/// Built-in categories whose action is "off" are dropped.
/// </summary>
public sealed class EffectivePrivacyPolicy
{
    public bool Enabled { get; init; }
    public bool DryRun { get; init; }
    public bool Restore { get; init; }

    /// <summary>Whether masked match samples are attached to hits.</summary>
    public bool RecordSamples { get; init; }

    public string DefaultAction { get; init; }
    public IReadOnlyList<PrivacyRule> Rules { get; set; } = [];

    private readonly Dictionary<string, string> _categoryActions;
    private readonly string? _clientDefault;

    public EffectivePrivacyPolicy(
        Dictionary<string, string> categoryActions, string? clientDefault, string defaultAction,
        bool enabled, bool dryRun, bool restore, bool recordSamples = true)
    {
        _categoryActions = categoryActions;
        _clientDefault = clientDefault;
        DefaultAction = defaultAction;
        Enabled = enabled;
        DryRun = dryRun;
        Restore = restore;
        RecordSamples = recordSamples;
    }

    /// <summary>Resolves the action for one rule (custom rules resolve by their own id first).</summary>
    public string ActionFor(string ruleId, string category) =>
        _categoryActions.TryGetValue(ruleId, out var byRule) ? byRule
        : _categoryActions.TryGetValue(category, out var byCategory) ? byCategory
        : _clientDefault ?? DefaultAction;

    /// <summary>Compiles the effective rule set; throws <see cref="ArgumentException"/> on an invalid custom pattern.</summary>
    public static EffectivePrivacyPolicy Build(
        PrivacySettings settings, string? clientKind, bool? enabledOverride = null, bool? dryRunOverride = null)
    {
        var clientDefault = clientKind is not null &&
                            settings.ClientDefaults.TryGetValue(clientKind, out var cd) ? cd : null;
        var policy = new EffectivePrivacyPolicy(
            settings.CategoryActions, clientDefault, settings.DefaultAction,
            enabledOverride ?? settings.Enabled, dryRunOverride ?? settings.DryRun, settings.RestoreResponses,
            settings.RecordSamples);

        var rules = new List<PrivacyRule>();
        foreach (var rule in PrivacyDetector.BuiltInRules)
        {
            if (policy.ActionFor(rule.Id, rule.Category) == PrivacyActions.Off) continue;
            rules.Add(new PrivacyRule { Id = rule.Id, Category = rule.Category, Regex = rule.Regex });
        }
        foreach (var custom in settings.CustomRules)
        {
            if (!custom.Enabled || policy.ActionFor(custom.Id, PrivacyCategories.Custom) == PrivacyActions.Off) continue;
            rules.Add(new PrivacyRule
            {
                Id = custom.Id,
                Category = PrivacyCategories.Custom,
                Regex = PrivacyDetector.Compile(custom.Pattern),
            });
        }
        policy.Rules = rules;
        return policy;
    }
}

/// <summary>What the guard did (or would do) with one request, as stored in <c>requests.privacy_json</c>.</summary>
public sealed class PrivacyReport
{
    public bool DryRun { get; set; }
    public bool Blocked { get; set; }
    public int Redactions { get; set; }
    public List<PrivacyHit> Hits { get; set; } = [];
}
