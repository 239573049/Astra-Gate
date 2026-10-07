using Astra.Core;
using Astra.Core.Privacy;
using Astra.Data;

namespace Astra.Gateway.Pipeline;

/// <summary>What the guard decided for one client request body.</summary>
/// <param name="Applied">False when the guard is disabled — body passes through untouched.</param>
/// <param name="DryRun">True when configured to detect-and-record only.</param>
/// <param name="Blocked">A "block" action matched; the request must be rejected.</param>
/// <param name="Body">Body to forward upstream (redacted when applied and not blocked).</param>
/// <param name="Hits">Aggregated rule hits.</param>
/// <param name="RestoreMap">Placeholder → original; empty unless redactions are to be restored.</param>
public sealed record PrivacyGuardResult(
    bool Applied,
    bool DryRun,
    bool Blocked,
    string Body,
    IReadOnlyList<PrivacyHit> Hits,
    IReadOnlyDictionary<string, string> RestoreMap)
{
    public static readonly PrivacyGuardResult PassThrough = new(false, false, false, "", [], new Dictionary<string, string>());
}

/// <summary>
/// Cached privacy-guard policy (settings key <c>privacy</c>) and the request-body inspection entry
/// point. M4/M3 gateway endpoints call <see cref="Inspect"/> before dispatching upstream and feed
/// <see cref="PrivacyGuardResult.RestoreMap"/> into a <see cref="ChunkRestorer"/> for the response.
/// </summary>
public sealed class PrivacyGuardService(AstraDatabase db)
{
    private volatile PrivacySettings _settings = new();
    private PrivacyDetector? _detector;

    public PrivacySettings Settings => _settings;
    private PrivacyDetector Detector => _detector ??= new PrivacyDetector();

    public async Task LoadAsync(CancellationToken ct = default) =>
        _settings = await db.Settings.GetAsync<PrivacySettings>(PrivacySettings.StorageKey, ct) ?? new PrivacySettings();

    /// <summary>Validates custom rule patterns, persists and activates the new settings.</summary>
    /// <exception cref="ArgumentException">A custom pattern is not a valid regex.</exception>
    public async Task<PrivacySettings> UpdateAsync(PrivacySettings next, CancellationToken ct = default)
    {
        foreach (var rule in next.CustomRules.Where(r => r.Enabled))
        {
            try
            {
                PrivacyDetector.Compile(rule.Pattern);
            }
            catch (ArgumentException e)
            {
                throw new ArgumentException($"自定义规则「{rule.Name}」不是有效的正则表达式：{e.Message}", e);
            }
        }
        await db.Settings.SetAsync(PrivacySettings.StorageKey, next, ct);
        _settings = next;
        return next;
    }

    /// <summary>Builds the policy for one client (used by the inspect path and the dry-run API).</summary>
    public EffectivePrivacyPolicy EffectiveFor(string? clientKind, bool enabledOverride, bool dryRunOverride) =>
        EffectivePrivacyPolicy.Build(_settings, clientKind, enabledOverride, dryRunOverride);

    public EffectivePrivacyPolicy EffectiveFor(string? clientKind) => EffectivePrivacyPolicy.Build(_settings, clientKind);

    /// <summary>Inspects a client request body (JSON or plain text) against the effective policy.</summary>
    public PrivacyGuardResult Inspect(string requestBody, string? clientKind)
    {
        var settings = _settings;
        if (!settings.Enabled) return PrivacyGuardResult.PassThrough with { Body = requestBody };

        var policy = EffectiveFor(clientKind);
        var applied = Detector.ApplyJson(requestBody, policy) ?? Detector.Apply(requestBody, policy);

        // Nothing matched: forward the original bytes untouched.
        if (applied.Hits.Count == 0)
            return new PrivacyGuardResult(true, policy.DryRun, false, requestBody, applied.Hits,
                new Dictionary<string, string>());

        if (policy.DryRun)
            return new PrivacyGuardResult(true, true, false, requestBody, applied.Hits,
                new Dictionary<string, string>());

        if (applied.Blocked)
            return new PrivacyGuardResult(true, false, true, requestBody, applied.Hits,
                new Dictionary<string, string>());

        var restore = policy.Restore && settings.RestoreResponses
            ? applied.Redactions.ToDictionary(r => r.Placeholder, r => r.Original)
            : new Dictionary<string, string>();
        return new PrivacyGuardResult(true, false, false, applied.Text, applied.Hits, restore);
    }

    /// <summary>Plain-text restore for non-streaming responses (streaming uses <see cref="ChunkRestorer"/>).</summary>
    public static string Restore(string text, IReadOnlyDictionary<string, string> restoreMap)
    {
        if (restoreMap.Count == 0) return text;
        return restoreMap.Aggregate(text, (current, kv) => current.Replace(kv.Key, kv.Value));
    }

    /// <summary>Serializes the guard outcome for <c>requests.privacy_json</c> (originals are never included).</summary>
    public static string ReportJson(bool dryRun, bool blocked, IReadOnlyList<PrivacyHit> hits, int redactionCount) =>
        Json.Serialize(new PrivacyReport { DryRun = dryRun, Blocked = blocked, Redactions = redactionCount, Hits = [.. hits] });
}
