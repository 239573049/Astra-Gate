using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Privacy;
using Astra.Data;
using Astra.Gateway.Protocol;

namespace Astra.Gateway.Pipeline;

/// <summary>What the guard decided for one client request body.</summary>
/// <param name="Applied">False when the guard is disabled — body passes through untouched.</param>
/// <param name="DryRun">True when configured to detect-and-record only.</param>
/// <param name="Blocked">A "block" action matched; the request must be rejected.</param>
/// <param name="Body">Body to forward upstream (redacted when applied and not blocked); empty from
/// <see cref="PrivacyGuardService.InspectJson"/>, which redacts the parsed body in place instead.</param>
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

    /// <summary><see cref="PrivacyGuardService.InspectJson"/>: redactions were written into the parsed body.</summary>
    public bool Rewritten { get; init; }
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
public sealed class PrivacyGuardService(AstraDatabase db, ISecretProtector? protector = null)
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
    private sealed class Snapshot(PrivacySettings settings, Exception? failure = null)
    {
        /// <summary>Why the stored policy could not be read: the guard may be on, so requests are refused until it is saved again.</summary>
        public Exception? Failure { get; } = failure;

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

    /// <summary>Where the placeholder key lives: its own settings row, protected with <see cref="ISecretProtector"/>.</summary>
    private const string KeyStorageKey = "privacy_placeholder_key";

    // One detector for the service's lifetime: it keeps, per policy, which long strings are known to be clean. Its mint
    // holds the per-install key (loaded in LoadAsync; until then, or without a protector, one for this process only)
    // and what has been masked so far in this process.
    private PrivacyDetector _detector = new();

    /// <summary>The active settings as a copy; mutating it never reaches the published snapshot.</summary>
    public PrivacySettings Settings => _snapshot.Settings.Clone();

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (protector is not null) _detector = new PrivacyDetector(new PlaceholderMint(await LoadKeyAsync(ct)));
        try
        {
            var loaded = await db.Settings.GetAsync<PrivacySettings>(PrivacySettings.StorageKey, ct) ?? new PrivacySettings();
            _snapshot = new Snapshot(loaded.Clone());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The stored policy is unreadable, so whether the guard is on is unknown: fail closed (see Unavailable)
            // until a new policy is saved, instead of sending requests the guard may have been meant to mask.
            _snapshot = new Snapshot(new PrivacySettings(), e);
        }
    }

    // The placeholder key is made once per install and kept protected; one that cannot be unprotected (the data
    // protection keys were lost) is replaced — placeholders then change once, nothing else.
    private async Task<byte[]> LoadKeyAsync(CancellationToken ct)
    {
        try
        {
            if (await db.Settings.GetAsync<string>(KeyStorageKey, ct) is { Length: > 0 } stored)
                return Convert.FromBase64String(protector!.Unprotect(stored));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // fall through to a new key
        }
        var key = PlaceholderMint.NewKey();
        await db.Settings.SetAsync(KeyStorageKey, protector!.Protect(Convert.ToBase64String(key)), ct);
        return key;
    }

    /// <summary>The refusal for a request the guard could not be run on: the guard is on, so nothing goes upstream unmasked.</summary>
    private static GatewayException Unavailable(Exception cause) => new(503, "api_error",
        "Astra 隐私护栏已开启，但当前无法运行（规则读取或检测出错），为避免敏感信息未经脱敏发往上游，本次请求已被拒绝。请在 Astra 的「隐私护栏」页面检查规则。")
    {
        RecordErrorType = "privacy_guard_unavailable",
    };

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
        if (snapshot.Failure is { } failure) throw Unavailable(failure);
        if (!settings.Enabled) return PrivacyGuardResult.PassThrough with { Body = requestBody };

        EffectivePrivacyPolicy policy;
        PrivacyApplyResult applied;
        try
        {
            policy = snapshot.PolicyFor(clientKind, null, null);
            applied = _detector.ApplyJson(requestBody, policy) ?? _detector.Apply(requestBody, policy);
        }
        catch (Exception e) when (e is not (OperationCanceledException or GatewayException))
        {
            // Fail closed: a detector or policy error (a custom rule that timed out, …) never lets the body through
            // unmasked. A dry run masks nothing anyway, so it is let through and only the log is the poorer for it.
            if (settings.DryRun) return new PrivacyGuardResult(true, true, false, requestBody, [], new Dictionary<string, string>());
            throw Unavailable(e);
        }

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

        var restore = Remembered(applied, policy, settings);
        return new PrivacyGuardResult(true, false, false, applied.Text, applied.Hits, restore);
    }

    /// <summary>
    /// <see cref="Inspect"/> for a body already parsed as a JSON object: <paramref name="source"/> is scanned and,
    /// unless the outcome is a dry run or a block, redactions are written into <paramref name="body"/> — the node
    /// tree over that same element — so the body is neither serialized nor parsed again (Body stays empty).
    /// </summary>
    public PrivacyGuardResult InspectJson(JsonElement source, JsonObject body, string? clientKind)
    {
        var snapshot = _snapshot;
        var settings = snapshot.Settings;
        if (snapshot.Failure is { } failure) throw Unavailable(failure);
        if (!settings.Enabled) return PrivacyGuardResult.PassThrough;

        EffectivePrivacyPolicy policy;
        PrivacyApplyResult applied;
        try
        {
            policy = snapshot.PolicyFor(clientKind, null, null);
            applied = _detector.ApplyJson(source, body, policy, rewrite: !policy.DryRun);
        }
        catch (Exception e) when (e is not (OperationCanceledException or GatewayException))
        {
            // Fail closed (see Inspect). The tree may be half rewritten, which is one more reason to refuse.
            if (settings.DryRun) return new PrivacyGuardResult(true, true, false, "", [], new Dictionary<string, string>());
            throw Unavailable(e);
        }
        if (applied.Hits.Count == 0 || policy.DryRun || applied.Blocked)
            return new PrivacyGuardResult(true, policy.DryRun, applied.Blocked && !policy.DryRun, "", applied.Hits,
                new Dictionary<string, string>());

        var restore = Remembered(applied, policy, settings);
        return new PrivacyGuardResult(true, false, false, "", applied.Hits, restore) { Rewritten = applied.Redactions.Count > 0 };
    }

    /// <summary>
    /// What the guard would do with <paramref name="input"/> under the current policy as if it were enforcing, for the
    /// console's "试一试": the real detector, so the placeholders are the ones a real request would get (and values
    /// masked before are masked again), but nothing is remembered — a test never teaches the guard anything.
    /// </summary>
    public PrivacyApplyResult DryRun(string input, bool isJson, string? clientKind)
    {
        var policy = EffectiveFor(clientKind, enabledOverride: true, dryRunOverride: false);
        return isJson ? _detector.ApplyJson(input, policy) ?? _detector.Apply(input, policy) : _detector.Apply(input, policy);
    }

    // What the request really masked: remembered (so the value is masked everywhere it shows up from now on) and, when
    // responses are restored, the map the response path puts back.
    private Dictionary<string, string> Remembered(PrivacyApplyResult applied, EffectivePrivacyPolicy policy, PrivacySettings settings)
    {
        _detector.Mint.Remember(applied.Redactions);
        var restore = new Dictionary<string, string>();
        if (policy.Restore && settings.RestoreResponses)
            foreach (var r in applied.Redactions) restore[r.Placeholder] = r.Original;
        return restore;
    }

    /// <summary>
    /// Restores placeholders in a JSON document (a non-streaming response body, one frame's data): the originals go
    /// back into the strings they were masked in, escaped as JSON, so a value with quotes or line breaks (a PEM key)
    /// cannot break the document — and into tool-call arguments, which are a JSON text inside a string, escaped a second
    /// time. Text that is not JSON is restored as plain text. Documents holding no placeholder are returned unchanged.
    /// </summary>
    public static string RestoreJson(string json, IReadOnlyDictionary<string, string> restoreMap)
    {
        if (restoreMap.Count == 0 || !ContainsAny(json, restoreMap)) return json;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return Restore(json, restoreMap);
        }
        if (root is null) return json;
        return PrivacyStreamRestorer.RestoreTree(root, restoreMap).ToJsonString(GatewayJson.Options);
    }

    internal static bool ContainsAny(string text, IReadOnlyDictionary<string, string> restoreMap)
    {
        foreach (var key in restoreMap.Keys)
            if (text.Contains(key, StringComparison.Ordinal)) return true;
        return false;
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
