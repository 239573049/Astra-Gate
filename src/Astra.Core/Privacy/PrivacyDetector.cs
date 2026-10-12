using System.Text;
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
            Linear(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----", ignoreCase: true)),
        new("builtin.jwt", PrivacyCategories.Jwt, "JWT（三段式）",
            Linear(@"eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}")),
        new("builtin.sk-ant", PrivacyCategories.ApiKey, "Anthropic API Key（sk-ant-…）",
            SkAntRegex()),
        new("builtin.sk-openai", PrivacyCategories.ApiKey, "OpenAI 风格 API Key（sk-…）",
            SkOpenAiRegex()),
        new("builtin.google-key", PrivacyCategories.ApiKey, "Google API Key（AIza…）",
            GoogleKeyRegex()),
        new("builtin.astra-local", PrivacyCategories.ApiKey, "Astra 本地客户端密钥（astra-…）",
            AstraLocalKeyRegex()),
        new("builtin.vendor-keys", PrivacyCategories.ApiKey, "xAI / Groq 风格 API Key（xai_… gsk_…）",
            VendorKeysRegex()),
        new("builtin.github-token", PrivacyCategories.ApiKey, "GitHub Token（ghp_/gho_/github_pat_…）",
            GitHubTokenRegex()),
        new("builtin.gitlab-token", PrivacyCategories.ApiKey, "GitLab Token（glpat-…）",
            GitLabTokenRegex()),
        new("builtin.slack-token", PrivacyCategories.ApiKey, "Slack Token（xox…-…）",
            SlackTokenRegex()),
        new("builtin.huggingface-token", PrivacyCategories.ApiKey, "Hugging Face Token（hf_…）",
            HuggingFaceTokenRegex()),
        new("builtin.stripe-key", PrivacyCategories.ApiKey, "Stripe Key（sk_live_/rk_live_/whsec_…）",
            StripeKeyRegex()),
        new("builtin.telegram-bot", PrivacyCategories.ApiKey, "Telegram Bot Token",
            TelegramBotTokenRegex()),
        new("builtin.aws-access-key", PrivacyCategories.AwsKey, "AWS Access Key（AKIA/ASIA…）",
            AwsAccessKeyRegex()),
        new("builtin.email", PrivacyCategories.Email, "邮箱地址",
            EmailRegex()),
        new("builtin.phone", PrivacyCategories.Phone, "手机号（中国大陆 / 国际）",
            PhoneRegex()),
        new("builtin.intranet-host", PrivacyCategories.Intranet, "内网 IP / 内部主机名",
            IntranetHostRegex()),
        new("builtin.credit-card", PrivacyCategories.CreditCard, "银行卡 / 信用卡号（Visa 万事达 运通 银联）",
            CreditCardRegex()),
        new("builtin.cn-id", PrivacyCategories.NationalId, "居民身份证号（中国大陆）",
            CnIdRegex()),
        new("builtin.us-ssn", PrivacyCategories.NationalId, "社会安全号（美国 SSN）",
            UsSsnRegex()),
        new("builtin.conn-string", PrivacyCategories.Credential, "数据库连接串（postgres/mysql/mongo/redis）",
            ConnStringRegex()),
        new("builtin.credential-assignment", PrivacyCategories.Credential, "明文凭据赋值（password= / token= / api_key=）",
            CredentialAssignmentRegex()),
    ];

    // These delimiter-driven patterns need a linear-time engine: repeated incomplete JWT / PEM
    // prefixes otherwise retry the entire remaining input at every prefix. Lookaround-free only.
    private static Regex Linear(string pattern, bool ignoreCase = false) => new(
        pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant |
        (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromMilliseconds(500));

    [GeneratedRegex(@"(?<![\w.+\-])[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}(?![\w\-])", RegexOptions.CultureInvariant, 500)]
    private static partial Regex EmailRegex();

    private static readonly Regex EmailCandidate = Linear(@"(?<email>[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,})(?:[^\w\-]|$)");

    // Keep the original email lookarounds without quadratic retries on '%' runs. The linear
    // candidate consumes the right boundary; resume at the email end so that boundary is reusable.
    private static IEnumerable<(int Index, int Length)> Matches(PrivacyRule rule, string text)
    {
        if (ReferenceEquals(rule.Regex, IntranetHostRegex()) &&
            !text.Contains("10.", StringComparison.Ordinal) &&
            !text.Contains("192.168.", StringComparison.Ordinal) &&
            !text.Contains("172.", StringComparison.Ordinal) &&
            !text.Contains(".internal", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains(".local", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains(".lan", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains(".corp", StringComparison.OrdinalIgnoreCase)) yield break;

        if (ReferenceEquals(rule.Regex, EmailRegex()))
        {
            for (var match = EmailCandidate.Match(text); match.Success;)
            {
                var email = match.Groups["email"];
                var start = email.Index;
                var end = start + email.Length;
                if (start > 0 && IsEmailLeftBoundary(text[start - 1]))
                {
                    // '%' belongs to the local part but not the original negative lookbehind.
                    var percent = text.IndexOf('%', start, text.IndexOf('@', start) - start);
                    start = percent < 0 ? end : percent + 1;
                }
                var accepted = start < end && text[start] != '@';
                if (accepted) yield return (start, end - start);
                // A rejected local part must not hide a second address starting after its '@'.
                match = EmailCandidate.Match(text, accepted ? end : text.IndexOf('@', email.Index) + 1);
            }
            yield break;
        }
        for (var match = rule.Regex.Match(text); match.Success; match = match.NextMatch())
            yield return (match.Index, match.Length);
    }

    private static bool IsEmailLeftBoundary(char c) => c is '.' or '+' or '-' ||
        char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.UppercaseLetter
            or System.Globalization.UnicodeCategory.LowercaseLetter or System.Globalization.UnicodeCategory.TitlecaseLetter
            or System.Globalization.UnicodeCategory.ModifierLetter or System.Globalization.UnicodeCategory.OtherLetter
            or System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.DecimalDigitNumber
            or System.Globalization.UnicodeCategory.ConnectorPunctuation;

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
            if (policy.ActionFor(rule.Id, rule.Category) == PrivacyActions.Off) continue;
            var count = Matches(rule, text).Count();
            if (count > 0) AddHit(hits, rule, policy, count);
        }
        return hits;
    }

    /// <summary>Detects and applies the configured actions. Placeholder originals are returned, never stored.</summary>
    public PrivacyApplyResult Apply(string text, EffectivePrivacyPolicy policy) => Apply(text, policy, null);

    // numbering: the result whose counters number the placeholders. ApplyJson passes its merged result so
    // placeholders stay unique across the body's strings (each would otherwise restart at #1).
    private PrivacyApplyResult Apply(string text, EffectivePrivacyPolicy policy, PrivacyApplyResult? numbering)
    {
        var result = new PrivacyApplyResult { Text = text };
        numbering ??= result;
        foreach (var rule in policy.Rules)
        {
            var action = policy.ActionFor(rule.Id, rule.Category);
            if (action == PrivacyActions.Off) continue;

            var input = result.Text;
            StringBuilder? rewritten = null;
            List<string>? samples = policy.RecordSamples ? [] : null;
            var count = 0;
            var cursor = 0;
            foreach (var (index, length) in Matches(rule, input))
            {
                count++;
                if (samples is { Count: < MaxSamples } && length > 0)
                {
                    var masked = MaskSample(input.AsSpan(index, length));
                    if (!samples.Contains(masked)) samples.Add(masked);
                }
                if (action != PrivacyActions.Redact) continue;

                rewritten ??= new StringBuilder(input.Length);
                rewritten.Append(input, cursor, index - cursor);
                var placeholder = PlaceholderFor(rule.Category, numbering.CounterFor(rule.Category));
                rewritten.Append(placeholder);
                result.Redactions.Add(new PrivacyRedaction
                {
                    Placeholder = placeholder,
                    Original = input.Substring(index, length),
                    Category = rule.Category,
                    RuleId = rule.Id,
                });
                cursor = index + length;
            }
            if (count > 0) AddHit(result.Hits, rule, policy, count, samples);
            if (rewritten is not null)
                result.Text = rewritten.Append(input, cursor, input.Length - cursor).ToString();
        }

        result.Blocked = result.Hits.Exists(h => h.Action == PrivacyActions.Block);
        return result;
    }

    /// <summary>
    /// JSON-aware processing: inspects string values except protocol-identified opaque media and
    /// reasoning payloads. Text, URLs, tool arguments/results and unknown fields remain inspected.
    /// Leaves structure and non-string values untouched. Returns null when the input is not valid JSON
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
        root = Walk(root, policy, merged, JsonScope.Root);
        if (merged.Redactions.Count > 0) merged.Text = root!.ToJsonString(Json.Wire);
        merged.Blocked = merged.Hits.Exists(h => h.Action == PrivacyActions.Block);
        return merged;
    }

    private enum JsonScope { Root, Content, ToolContent, Source, InlineData, ImageUrl, Audio, File, Ordinary }

    private JsonNode? Walk(JsonNode? node, EffectivePrivacyPolicy policy, PrivacyApplyResult merged, JsonScope scope)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var s):
            {
                var r = Apply(s, policy, merged);
                Merge(merged, r);
                return r.Text == s ? node : JsonValue.Create(r.Text);
            }
            case JsonObject obj:
            {
                // Compute skips from original discriminators before custom rules can rewrite them.
                var type = StringValue(obj["type"]);
                var properties = obj.Select(p => (p.Key, p.Value,
                    Skip: IsOpaque(scope, type, obj, p.Key, p.Value))).ToArray();
                foreach (var (key, child, skip) in properties)
                {
                    if (skip) continue;
                    var childScope = ChildScope(scope, type, key, child);
                    var rewritten = Walk(child, policy, merged, childScope);
                    if (!ReferenceEquals(child, rewritten)) obj[key] = rewritten;
                }
                break;
            }
            case JsonArray array:
            {
                for (var i = 0; i < array.Count; i++)
                {
                    var child = array[i];
                    var rewritten = Walk(child, policy, merged, scope);
                    if (!ReferenceEquals(child, rewritten)) array[i] = rewritten;
                }
                break;
            }
        }
        return node;
    }

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    private static JsonScope ChildScope(JsonScope scope, string? type, string key, JsonNode? child) => (scope, key) switch
    {
        (JsonScope.Root, "messages" or "input" or "system" or "contents" or "systemInstruction" or "system_instruction") => JsonScope.Content,
        (JsonScope.Content, "content") when type == "tool_result" && child is JsonArray => JsonScope.ToolContent,
        (JsonScope.Content, "content" or "parts") when child is JsonArray => JsonScope.Content,
        (JsonScope.Content, "output") when type == "function_call_output" && child is JsonArray => JsonScope.ToolContent,
        (JsonScope.ToolContent, "source") when type is "image" or "document" => JsonScope.Source,
        (JsonScope.ToolContent, "image_url") when type is "image_url" or "input_image" => JsonScope.ImageUrl,
        (JsonScope.ToolContent, "input_audio") when type == "input_audio" => JsonScope.Audio,
        (JsonScope.ToolContent, "file") when type == "file" => JsonScope.File,
        (JsonScope.Content, "source") when type is "image" or "document" => JsonScope.Source,
        (JsonScope.Content, "inlineData" or "inline_data") => JsonScope.InlineData,
        (JsonScope.Content, "image_url") when type is "image_url" or "input_image" => JsonScope.ImageUrl,
        (JsonScope.Content, "input_audio") when type == "input_audio" => JsonScope.Audio,
        (JsonScope.Content, "file") when type == "file" => JsonScope.File,
        _ => JsonScope.Ordinary,
    };

    private static bool IsOpaque(JsonScope scope, string? type, JsonObject obj, string key, JsonNode? value)
    {
        // Arbitrary tool JSON stays inspected even when it contains fields called data/signature.
        // Skip only opaque protocol slots, never captions, filenames, URLs or whole media objects.
        if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text)) return false;
        return (scope, key) switch
        {
            (JsonScope.Source, "data") => type == "base64" && IsBinaryMedia(StringValue(obj["media_type"])),
            (JsonScope.InlineData, "data") => IsBinaryMedia(StringValue(obj["mimeType"] ?? obj["mime_type"])),
            (JsonScope.Audio, "data") => true,
            (JsonScope.ImageUrl, "url") => IsBinaryDataUrl(text),
            (JsonScope.File, "file_data") => IsBinaryDataUrl(text),
            (JsonScope.Content or JsonScope.ToolContent, "image_url") when type == "input_image" => IsBinaryDataUrl(text),
            (JsonScope.Content or JsonScope.ToolContent, "file_data") when type == "input_file" => IsBinaryDataUrl(text),
            (JsonScope.Content, "signature") => type == "thinking",
            (JsonScope.Content, "data") => type == "redacted_thinking",
            (JsonScope.Content, "encrypted_content") => type == "reasoning",
            (JsonScope.Content, "thoughtSignature" or "thought_signature") => true,
            _ => false,
        };
    }

    private static bool IsBinaryMedia(string? media) => media is not null &&
        (media.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
         media.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
         media.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
         media.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) ||
         media.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase));

    private static bool IsBinaryDataUrl(string text)
    {
        if (!text.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
        var separator = text.IndexOf(';', 5);
        return separator is > 5 and < 128 && IsBinaryMedia(text[5..separator]) &&
            text.AsSpan(separator).StartsWith(";base64,", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>Masks one matched value for the log: keeps a short head/tail, hides the middle, caps length.</summary>
    public static string MaskSample(string value) => MaskSample(value.AsSpan());

    private static string MaskSample(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty) return "••••";
        Span<char> preview = stackalloc char[64];
        var length = 0;
        foreach (var c in value)
        {
            if (c == '\r') continue;
            preview[length++] = c == '\n' ? '\u23ce' : c;
            if (length == preview.Length) break;
        }
        var v = preview[..length];
        return length switch
        {
            <= 4 => "••••••",
            <= 12 => string.Concat(v[..2], "••••", v[^1..]),
            _ => string.Concat(v[..4], "••••", v[^2..]),
        };
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9])sk-ant-[A-Za-z0-9_\-]{20,}", RegexOptions.CultureInvariant, 500)]
    private static partial Regex SkAntRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])sk-[A-Za-z0-9_\-]{20,}", RegexOptions.CultureInvariant, 500)]
    private static partial Regex SkOpenAiRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])AIza[0-9A-Za-z_\-]{35}", RegexOptions.CultureInvariant, 500)]
    private static partial Regex GoogleKeyRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])astra-[a-z0-9\-]{1,32}-[A-Za-z0-9]{20,}", RegexOptions.CultureInvariant, 500)]
    private static partial Regex AstraLocalKeyRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:xai|gsk)_[A-Za-z0-9]{20,}", RegexOptions.CultureInvariant, 500)]
    private static partial Regex VendorKeysRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})", RegexOptions.CultureInvariant, 500)]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])glpat-[A-Za-z0-9_\-]{16,}", RegexOptions.CultureInvariant, 500)]
    private static partial Regex GitLabTokenRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])xox[baprs]-[A-Za-z0-9\-]{10,}", RegexOptions.CultureInvariant, 500)]
    private static partial Regex SlackTokenRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])hf_[A-Za-z0-9]{20,}", RegexOptions.CultureInvariant, 500)]
    private static partial Regex HuggingFaceTokenRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:(?:sk|rk)_live|whsec)_[A-Za-z0-9]{16,}", RegexOptions.CultureInvariant, 500)]
    private static partial Regex StripeKeyRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])\d{8,10}:AA[A-Za-z0-9_\-]{33}(?![A-Za-z0-9])", RegexOptions.CultureInvariant, 500)]
    private static partial Regex TelegramBotTokenRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:AKIA|ASIA)[0-9A-Z]{16}(?![0-9A-Z])", RegexOptions.CultureInvariant, 500)]
    private static partial Regex AwsAccessKeyRegex();

    [GeneratedRegex(@"(?:(?<!\d)1[3-9]\d{9}(?!\d)|(?<!\d)\+[0-9]{1,3}[\s\-]?[0-9]{6,14}(?!\d))", RegexOptions.CultureInvariant, 500)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"(?:(?<![\w.])(?:10\.\d{1,3}\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3})(?![\w.])|(?<![\w.\-])[A-Za-z0-9][A-Za-z0-9\-]*(?:\.[A-Za-z0-9][A-Za-z0-9\-]*)*\.(?:internal|local|lan|corp)(?![\w\-]))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 500)]
    private static partial Regex IntranetHostRegex();

    [GeneratedRegex(@"(?<!\d)(?:4\d{12}(?:\d{3})?|5[1-5]\d{14}|3[47]\d{13}|6(?:011|5\d{2})\d{12}|62\d{14,17})(?!\d)", RegexOptions.CultureInvariant, 500)]
    private static partial Regex CreditCardRegex();

    [GeneratedRegex(@"(?<!\d)[1-9]\d{5}(?:19|20)\d{2}(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])\d{3}[\dXx](?!\d)", RegexOptions.CultureInvariant, 500)]
    private static partial Regex CnIdRegex();

    [GeneratedRegex(@"(?<!\d)(?!000|666|9\d{2})\d{3}-(?!00)\d{2}-(?!0000)\d{4}(?!\d)", RegexOptions.CultureInvariant, 500)]
    private static partial Regex UsSsnRegex();

    [GeneratedRegex(@"(?:postgres(?:ql)?|mysql|mongodb(?:\+srv)?|redis|amqps?)://[^\s:@/]+:[^\s@]+@[^\s]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 500)]
    private static partial Regex ConnStringRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:password|passwd|pwd|pass|secret|token|api[_\-]?key|access[_\-]?key)[""']?\s*[:=]\s*[""']?(?!\[REDACTED:)[^\s""']{6,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 500)]
    private static partial Regex CredentialAssignmentRegex();
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
