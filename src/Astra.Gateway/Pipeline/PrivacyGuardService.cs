using System.Collections.Concurrent;
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
///
/// The guard keeps one published <see cref="Snapshot"/> at a time: a settings generation and the
/// <see cref="EffectivePrivacyPolicy"/> objects materialized from it. A settings update publishes a
/// new snapshot atomically and the old one — with its cache — is simply dropped, so custom rule
/// regexes are compiled once per generation and per resolved client/override combination instead of
/// on every request. One request reads the snapshot once and uses that same generation's settings and
/// policy throughout, so an update landing mid-request is never mixed into it.
/// </summary>
public sealed class PrivacyGuardService(AstraDatabase db)
{
    /// <summary>
    /// Everything one client's policy depends on: the client's <em>resolved</em> default action and the
    /// two request overrides. Deliberately not the raw client kind — that id is untrusted and unbounded.
    /// </summary>
    private readonly record struct PolicyKey(string? ClientDefault, bool? EnabledOverride, bool? DryRunOverride);

    /// <summary>
    /// One settings generation with a thread-safe, lazily populated policy cache. Settings are
    /// owned by this snapshot. Keys are bounded by configured client defaults and override flags;
    /// unknown client ids share the same entry.
    /// </summary>
    private sealed class Snapshot(PrivacySettings settings)
    {
        private readonly ConcurrentDictionary<PolicyKey, Lazy<EffectivePrivacyPolicy>> _policies = new();

        public PrivacySettings Settings { get; } = settings;

        public EffectivePrivacyPolicy PolicyFor(string? clientKind, bool? enabledOverride, bool? dryRunOverride)
        {
            var key = new PolicyKey(ResolvedDefault(clientKind), enabledOverride, dryRunOverride);
            // Build reads the client kind only to look up its default action, which the key already pins,
            // so whichever request wins the race to materialize a key gets the policy all of them mean —
            // the Lazy keeps the compile to one, and a losing Lazy is discarded without being evaluated.
            return _policies.GetOrAdd(key, k => new Lazy<EffectivePrivacyPolicy>(
                () => EffectivePrivacyPolicy.Build(Settings, clientKind, k.EnabledOverride, k.DryRunOverride),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        private string? ResolvedDefault(string? clientKind) =>
            clientKind is not null && Settings.ClientDefaults.TryGetValue(clientKind, out var cd) ? cd : null;
    }

    private volatile Snapshot _snapshot = new(new PrivacySettings());
    private PrivacyDetector? _detector;

    /// <summary>The active settings as a copy; mutating it never reaches the published snapshot.</summary>
    public PrivacySettings Settings => _snapshot.Settings.Clone();

    private PrivacyDetector Detector => _detector ??= new PrivacyDetector();

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var loaded = await db.Settings.GetAsync<PrivacySettings>(PrivacySettings.StorageKey, ct) ?? new PrivacySettings();
        _snapshot = new Snapshot(loaded.Clone());
    }

    /// <summary>Validates custom rule patterns, persists and activates the new settings.</summary>
    /// <exception cref="ArgumentException">A custom pattern is not a valid regex.</exception>
    public async Task<PrivacySettings> UpdateAsync(PrivacySettings next, CancellationToken ct = default)
    {
        // The published snapshot owns its own copy: a caller that keeps mutating (or reuses) the instance
        // it passed in can no longer change what the guard runs or what is persisted for it.
        var published = next.Clone();
        foreach (var rule in published.CustomRules.Where(r => r.Enabled))
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
        await db.Settings.SetAsync(PrivacySettings.StorageKey, published, ct);
        _snapshot = new Snapshot(published);
        return published.Clone();
    }

    /// <summary>Builds the policy for one client (used by the inspect path and the dry-run API).</summary>
    public EffectivePrivacyPolicy EffectiveFor(string? clientKind, bool enabledOverride, bool dryRunOverride)
    {
        var snapshot = _snapshot;
        return snapshot.PolicyFor(clientKind, enabledOverride, dryRunOverride);
    }

    public EffectivePrivacyPolicy EffectiveFor(string? clientKind)
    {
        var snapshot = _snapshot;
        return snapshot.PolicyFor(clientKind, null, null);
    }

    /// <summary>Inspects a client request body (JSON or plain text) against the effective policy.</summary>
    public PrivacyGuardResult Inspect(string requestBody, string? clientKind)
    {
        var snapshot = _snapshot;
        var settings = snapshot.Settings;
        if (!settings.Enabled) return PrivacyGuardResult.PassThrough with { Body = requestBody };

        var policy = snapshot.PolicyFor(clientKind, null, null);
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
