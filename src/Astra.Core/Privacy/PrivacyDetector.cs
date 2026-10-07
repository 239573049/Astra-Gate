using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Text.Json;

namespace Astra.Core.Privacy;

/// <summary>
/// The privacy guard's detection engine: built-in rule sets plus user-defined regexes.
/// Pure and local — no network, no model. Originals of redacted values exist only in the
/// <see cref="PrivacyRedaction"/> list the caller keeps for the lifetime of one request.
/// </summary>
public sealed partial class PrivacyDetector
{
    public sealed record BuiltinRule(string Id, string Category, string Description, Regex Regex);

    /// <summary>Built-in rules in evaluation order (most specific first). Placeholder text is inert to later rules.</summary>
    public static IReadOnlyList<BuiltinRule> BuiltInRules { get; } =
    [
        new("builtin.private-key", PrivacyCategories.PrivateKey, "PEM 私钥块",
            Compile(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----", ignoreCase: true)),
        new("builtin.jwt", PrivacyCategories.Jwt, "JWT（三段式）",
            Compile(@"eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}")),
        new("builtin.sk-ant", PrivacyCategories.ApiKey, "Anthropic API Key（sk-ant-…）",
            Compile(@"(?<![A-Za-z0-9])sk-ant-[A-Za-z0-9_\-]{20,}")),
        new("builtin.sk-openai", PrivacyCategories.ApiKey, "OpenAI 风格 API Key（sk-…）",
            Compile(@"(?<![A-Za-z0-9])sk-[A-Za-z0-9_\-]{20,}")),
        new("builtin.google-key", PrivacyCategories.ApiKey, "Google API Key（AIza…）",
            Compile(@"(?<![A-Za-z0-9])AIza[0-9A-Za-z_\-]{35}")),
        new("builtin.astra-local", PrivacyCategories.ApiKey, "Astra 本地客户端密钥（astra-…）",
            Compile(@"(?<![A-Za-z0-9])astra-[a-z0-9\-]{1,32}-[A-Za-z0-9]{20,}")),
        new("builtin.vendor-keys", PrivacyCategories.ApiKey, "xAI / Groq 风格 API Key（xai_… gsk_…）",
            Compile(@"(?<![A-Za-z0-9])(?:xai|gsk)_[A-Za-z0-9]{20,}")),
        new("builtin.github-token", PrivacyCategories.ApiKey, "GitHub Token（ghp_/gho_/github_pat_…）",
            Compile(@"(?<![A-Za-z0-9])(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})")),
        new("builtin.gitlab-token", PrivacyCategories.ApiKey, "GitLab Token（glpat-…）",
            Compile(@"(?<![A-Za-z0-9])glpat-[A-Za-z0-9_\-]{16,}")),
        new("builtin.slack-token", PrivacyCategories.ApiKey, "Slack Token（xox…-…）",
            Compile(@"(?<![A-Za-z0-9])xox[baprs]-[A-Za-z0-9\-]{10,}")),
        new("builtin.huggingface-token", PrivacyCategories.ApiKey, "Hugging Face Token（hf_…）",
            Compile(@"(?<![A-Za-z0-9])hf_[A-Za-z0-9]{20,}")),
        new("builtin.stripe-key", PrivacyCategories.ApiKey, "Stripe Key（sk_live_/rk_live_/whsec_…）",
            Compile(@"(?<![A-Za-z0-9])(?:(?:sk|rk)_live|whsec)_[A-Za-z0-9]{16,}")),
        new("builtin.telegram-bot", PrivacyCategories.ApiKey, "Telegram Bot Token",
            Compile(@"(?<![A-Za-z0-9])\d{8,10}:AA[A-Za-z0-9_\-]{33}(?![A-Za-z0-9])")),
        new("builtin.aws-access-key", PrivacyCategories.AwsKey, "AWS Access Key（AKIA/ASIA…）",
            Compile(@"(?<![A-Za-z0-9])(?:AKIA|ASIA)[0-9A-Z]{16}(?![0-9A-Z])")),
        new("builtin.email", PrivacyCategories.Email, "邮箱地址",
            Compile(@"(?<![\w.+\-])[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}(?![\w\-])")),
        new("builtin.phone", PrivacyCategories.Phone, "手机号（中国大陆 / 国际）",
            Compile(@"(?:(?<!\d)1[3-9]\d{9}(?!\d)|(?<!\d)\+[0-9]{1,3}[\s\-]?[0-9]{6,14}(?!\d))")),
        new("builtin.intranet-host", PrivacyCategories.Intranet, "内网 IP / 内部主机名",
            Compile(@"(?:(?<![\w.])(?:10\.\d{1,3}\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3})(?![\w.])|(?<![\w.\-])[A-Za-z0-9][A-Za-z0-9\-]*(?:\.[A-Za-z0-9][A-Za-z0-9\-]*)*\.(?:internal|local|lan|corp)(?![\w\-]))",
                ignoreCase: true)),
        new("builtin.credit-card", PrivacyCategories.CreditCard, "银行卡 / 信用卡号（Visa 万事达 运通 银联）",
            Compile(@"(?<!\d)(?:4\d{12}(?:\d{3})?|5[1-5]\d{14}|3[47]\d{13}|6(?:011|5\d{2})\d{12}|62\d{14,17})(?!\d)")),
        new("builtin.cn-id", PrivacyCategories.NationalId, "居民身份证号（中国大陆）",
            Compile(@"(?<!\d)[1-9]\d{5}(?:19|20)\d{2}(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])\d{3}[\dXx](?!\d)")),
        new("builtin.us-ssn", PrivacyCategories.NationalId, "社会安全号（美国 SSN）",
            Compile(@"(?<!\d)(?!000|666|9\d{2})\d{3}-(?!00)\d{2}-(?!0000)\d{4}(?!\d)")),
        new("builtin.conn-string", PrivacyCategories.Credential, "数据库连接串（postgres/mysql/mongo/redis）",
            Compile(@"(?:postgres(?:ql)?|mysql|mongodb(?:\+srv)?|redis|amqps?)://[^\s:@/]+:[^\s@]+@[^\s]+", ignoreCase: true)),
        new("builtin.credential-assignment", PrivacyCategories.Credential, "明文凭据赋值（password= / token= / api_key=）",
            Compile(@"(?<![A-Za-z0-9])(?:password|passwd|pwd|pass|secret|token|api[_\-]?key|access[_\-]?key)[""']?\s*[:=]\s*[""']?(?!\[REDACTED:)[^\s""']{6,}", ignoreCase: true)),
    ];

    /// <summary>Compiles a user-supplied pattern with a match timeout; throws ArgumentException when invalid.</summary>
    public static Regex Compile(string pattern, bool ignoreCase = false) => new(
        pattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant |
        (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None),
        TimeSpan.FromMilliseconds(500));

    public static string PlaceholderFor(string category, int ordinal) => $"[REDACTED:{category}#{ordinal}]";

    public static bool LooksLikePlaceholder(string text) =>
        text.StartsWith("[REDACTED:", StringComparison.Ordinal) && text.EndsWith("]", StringComparison.Ordinal);

    /// <summary>Detects matches without changing anything (used for warnings and dry run).</summary>
    public List<PrivacyHit> Scan(string text, EffectivePrivacyPolicy policy)
    {
        var hits = new List<PrivacyHit>();
        foreach (var rule in policy.Rules)
        {
            var count = rule.Regex.Matches(text).Count;
            if (count > 0) AddHit(hits, rule, policy, count);
        }
        return hits;
    }

    /// <summary>Detects and applies the configured actions. Placeholder originals are returned, never stored.</summary>
    public PrivacyApplyResult Apply(string text, EffectivePrivacyPolicy policy)
    {
        var result = new PrivacyApplyResult { Text = text };
        foreach (var rule in policy.Rules)
        {
            var action = policy.ActionFor(rule.Id, rule.Category);
            if (action == PrivacyActions.Off) continue;

            var matches = rule.Regex.Matches(result.Text);
            if (matches.Count == 0) continue;

            AddHit(result.Hits, rule, policy, matches.Count, CollectSamples(matches, policy));
            if (action != PrivacyActions.Redact) continue;

            var evaluator = new MatchEvaluator(_ =>
            {
                var placeholder = PlaceholderFor(rule.Category, result.CounterFor(rule.Category));
                result.Redactions.Add(new PrivacyRedaction
                {
                    Placeholder = placeholder,
                    Original = _.Value,
                    Category = rule.Category,
                    RuleId = rule.Id,
                });
                return placeholder;
            });
            result.Text = rule.Regex.Replace(result.Text, evaluator);
        }

        result.Blocked = result.Hits.Exists(h => h.Action == PrivacyActions.Block);
        return result;
    }

    /// <summary>
    /// JSON-aware processing: applies the policy to every string value (objects, arrays, nested),
    /// leaving structure and non-string values untouched. Returns null when the input is not valid JSON
    /// (callers then fall back to plain-text <see cref="Apply"/>).
    /// </summary>
    public PrivacyApplyResult? ApplyJson(string json, EffectivePrivacyPolicy policy)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json,
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
        }
        catch (JsonException)
        {
            return null;
        }
        if (root is null) return null;

        var merged = new PrivacyApplyResult { Text = json };
        Walk(root, policy, merged);
        merged.Text = root.ToJsonString(Json.Wire);
        merged.Blocked = merged.Hits.Exists(h => h.Action == PrivacyActions.Block);
        return merged;
    }

    private void Walk(JsonNode node, EffectivePrivacyPolicy policy, PrivacyApplyResult merged)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var s):
            {
                var r = Apply(s, policy);
                if (r.Text != s) value.ReplaceWith(r.Text);
                Merge(merged, r);
                break;
            }
            case JsonObject obj:
            {
                foreach (var property in obj) Walk(property.Value!, policy, merged);
                break;
            }
            case JsonArray array:
            {
                foreach (var item in array) Walk(item!, policy, merged);
                break;
            }
        }
    }

    /// <remarks><paramref name="samples"/> defaults to null so older call sites (no samples) stay valid.</remarks>
    private static void AddHit(List<PrivacyHit> hits, PrivacyRule rule, EffectivePrivacyPolicy policy, int count, List<string>? samples = null)
    {
        var action = policy.ActionFor(rule.Id, rule.Category);
        var existing = hits.Find(h => h.RuleId == rule.Id && h.Action == action);
        if (existing is not null)
        {
            existing.Count += count;
            MergeSamples(existing.Samples, samples);
        }
        else
        {
            var attached = new List<string>();
            MergeSamples(attached, samples);
            hits.Add(new PrivacyHit { RuleId = rule.Id, Category = rule.Category, Action = action, Count = count, Samples = attached });
        }
    }

    private static void Merge(PrivacyApplyResult into, PrivacyApplyResult from)
    {
        foreach (var hit in from.Hits)
        {
            var existing = into.Hits.Find(h => h.RuleId == hit.RuleId && h.Action == hit.Action);
            if (existing is not null)
            {
                existing.Count += hit.Count;
                MergeSamples(existing.Samples, hit.Samples);
            }
            else into.Hits.Add(new PrivacyHit
            {
                RuleId = hit.RuleId,
                Category = hit.Category,
                Action = hit.Action,
                Count = hit.Count,
                Samples = hit.Samples,
            });
        }
        into.Redactions.AddRange(from.Redactions);
    }

    /// <summary>Keeps at most <see cref="MaxSamples"/> distinct masked previews per rule.</summary>
    private static void MergeSamples(List<string> into, List<string>? samples)
    {
        if (samples is null) return;
        foreach (var sample in samples)
        {
            if (into.Count >= MaxSamples) return;
            if (!into.Contains(sample)) into.Add(sample);
        }
    }

    private const int MaxSamples = 3;

    /// <summary>Up to <see cref="MaxSamples"/> distinct matches, each masked — originals are never recorded.</summary>
    private static List<string>? CollectSamples(MatchCollection matches, EffectivePrivacyPolicy policy)
    {
        if (!policy.RecordSamples) return null;
        var samples = new List<string>();
        foreach (Match m in matches)
        {
            if (m.Value.Length == 0) continue;
            var masked = MaskSample(m.Value);
            if (!samples.Contains(masked)) samples.Add(masked);
            if (samples.Count >= MaxSamples) break;
        }
        return samples;
    }

    /// <summary>Masks one matched value for the log: keeps a short head/tail, hides the middle, caps length.</summary>
    public static string MaskSample(string value)
    {
        if (string.IsNullOrEmpty(value)) return "••••";
        var v = value.Replace("\r", "").Replace("\n", "⏎");
        if (v.Length > 64) v = v[..64];
        return v.Length switch
        {
            <= 4 => "••••••",
            <= 12 => string.Concat(v.AsSpan(0, 2), "••••", v.AsSpan(v.Length - 1, 1)),
            _ => string.Concat(v.AsSpan(0, 4), "••••", v.AsSpan(v.Length - 2, 2)),
        };
    }
}

/// <summary>Result of applying a policy to one piece of text / one JSON body.</summary>
public sealed class PrivacyApplyResult
{
    public string Text { get; set; } = "";
    public List<PrivacyHit> Hits { get; } = [];
    public List<PrivacyRedaction> Redactions { get; } = [];

    /// <summary>True when any hit's action was "block" (callers reject the request).</summary>
    public bool Blocked { get; set; }

    private readonly Dictionary<string, int> _counters = new();

    public int CounterFor(string category)
    {
        var next = _counters.TryGetValue(category, out var current) ? current + 1 : 1;
        _counters[category] = next;
        return next;
    }
}
