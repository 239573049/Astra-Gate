using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

    /// <summary>
    /// Match budget for the built-in rules. They are ours and linear (or at worst bounded) in the input
    /// length, but one megabyte still costs a few hundred milliseconds per rule on a slow core, and the
    /// short budget that guards the untrusted custom patterns (see <see cref="Compile"/>) failed whole
    /// requests there: a 1 MB body took 545 ms in <c>builtin.credential-assignment</c> on a 2-core GitHub
    /// runner, and the credential category alone costs 895 ms on 8 MB on an M-series Mac (where every rule
    /// together needs 47 ms per MB). 5 s is ~10 MB on the slowest machine measured — a request that big is
    /// rare, and a hit is still an error rather than a pass.
    /// </summary>
    private const int BuiltinTimeoutMs = 5000;

    // These delimiter-driven patterns need a linear-time engine: repeated incomplete JWT / PEM
    // prefixes otherwise retry the entire remaining input at every prefix. Lookaround-free only.
    private static Regex Linear(string pattern, bool ignoreCase = false) => new(
        pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant |
        (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromMilliseconds(BuiltinTimeoutMs));

    [GeneratedRegex(@"(?<![\w.+\-])[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}(?![\w\-])", RegexOptions.CultureInvariant, BuiltinTimeoutMs)]
    private static partial Regex EmailRegex();

    private static readonly Regex EmailCandidate = Linear(@"(?<email>[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,})(?:[^\w\-]|$)");

    // Keep the original email lookarounds without quadratic retries on '%' runs. The linear
    // candidate consumes the right boundary; resume at the email end so that boundary is reusable.
    // What an intranet host needs somewhere in the text: one vectorized pass instead of a regex scan.
    private static readonly SearchValues<string> IntranetHints = SearchValues.Create(
        ["10.", "192.168.", "172.", ".internal", ".local", ".lan", ".corp"], StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<(int Index, int Length)> Matches(PrivacyRule rule, string text)
    {
        if (ReferenceEquals(rule.Regex, IntranetHostRegex()) && text.AsSpan().IndexOfAny(IntranetHints) < 0) yield break;

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
    public PrivacyApplyResult Apply(string text, EffectivePrivacyPolicy policy)
    {
        var result = new PrivacyApplyResult { Text = text };
        // Most text matches nothing: one allocation-free pass per rule decides that.
        if (!AnyRuleMatches(text, StateFor(policy))) return result;
        result.Text = ApplyInto(text, policy, result);
        result.Blocked = result.Hits.Exists(h => h.Action == PrivacyActions.Block);
        return result;
    }

    // Runs every rule over text in order (each rule sees the previous rules' placeholders), adding hits and
    // redactions to into, whose counters number the placeholders: one JSON body shares them across its strings
    // so placeholders stay unique (each string would otherwise restart at #1). Returns text itself when nothing
    // was redacted.
    private static string ApplyInto(string text, EffectivePrivacyPolicy policy, PrivacyApplyResult into)
    {
        foreach (var rule in policy.Rules)
        {
            var action = policy.ActionFor(rule.Id, rule.Category);
            if (action == PrivacyActions.Off) continue;

            var input = text;
            StringBuilder? rewritten = null;
            List<string>? samples = null;
            var count = 0;
            var cursor = 0;
            foreach (var (index, length) in Matches(rule, input))
            {
                count++;
                if (policy.RecordSamples && length > 0 && (samples ??= []).Count < MaxSamples)
                {
                    var sample = policy.RecordFullSamples
                        ? FullSample(input.AsSpan(index, length))
                        : MaskSample(input.AsSpan(index, length));
                    if (!samples.Contains(sample)) samples.Add(sample);
                }
                if (action != PrivacyActions.Redact) continue;

                rewritten ??= new StringBuilder(input.Length);
                rewritten.Append(input, cursor, index - cursor);
                var placeholder = PlaceholderFor(rule.Category, into.CounterFor(rule.Category));
                rewritten.Append(placeholder);
                into.Redactions.Add(new PrivacyRedaction
                {
                    Placeholder = placeholder,
                    Original = input.Substring(index, length),
                    Category = rule.Category,
                    RuleId = rule.Id,
                });
                cursor = index + length;
            }
            if (count > 0) AddHit(into.Hits, rule, policy, count, samples);
            if (rewritten is not null)
                text = rewritten.Append(input, cursor, input.Length - cursor).ToString();
        }
        return text;
    }

    /// <summary>
    /// Whether any active rule matches anywhere in <paramref name="text"/> — a necessary condition for any hit,
    /// so text that fails it needs no further work. Allocation-free: span-based IsMatch only.
    /// </summary>
    private static bool AnyRuleMatches(ReadOnlySpan<char> text, PolicyState state)
    {
        foreach (var (regex, hint) in state.Active)
        {
            var matched = hint switch
            {
                RuleHint.Intranet => text.IndexOfAny(IntranetHints) >= 0 && regex.IsMatch(text),
                // Any address Matches accepts is an EmailCandidate match; the linear candidate is the cheap check.
                RuleHint.Email => text.Contains('@') && EmailCandidate.IsMatch(text),
                RuleHint.Scheme => text.IndexOf("://", StringComparison.Ordinal) >= 0 && regex.IsMatch(text),
                RuleHint.Assignment => text.IndexOfAny(':', '=') >= 0 && HasCredentialKeyword(text) && regex.IsMatch(text),
                RuleHint.Telegram => text.IndexOf(":AA", StringComparison.Ordinal) >= 0 && regex.IsMatch(text),
                RuleHint.Stripe => (text.IndexOf("_live_", StringComparison.Ordinal) >= 0 || text.IndexOf("whsec_", StringComparison.Ordinal) >= 0) && regex.IsMatch(text),
                RuleHint.Dash => text.Contains('-') && regex.IsMatch(text),
                RuleHint.Phone => text.Contains('+') ? regex.IsMatch(text) : DigitRunMatches(text, regex, 11),
                RuleHint.Card => DigitRunMatches(text, regex, 13),
                RuleHint.NationalId => DigitRunMatches(text, regex, 17),
                _ => regex.IsMatch(text),
            };
            if (matched) return true;
        }
        return false;
    }

    /// <summary>
    /// A literal some built-in rules cannot match without: a vectorized search for it decides most text before
    /// the regex runs (the pattern is not touched, so what matches is unchanged).
    /// </summary>
    private enum RuleHint { None, Intranet, Email, Scheme, Assignment, Telegram, Stripe, Dash, Phone, Card, NationalId }

    // Every keyword of builtin.credential-assignment contains one of these. The regex is IgnoreCase, whose case
    // table also folds 'ſ' into 's' and the Kelvin sign into 'k' (and dotted / dotless i): text with any of those
    // goes to the regex.
    private static readonly SearchValues<string> CredentialKeywords = SearchValues.Create(
        ["pass", "pwd", "secret", "token", "api", "access"], StringComparison.OrdinalIgnoreCase);

    private static readonly SearchValues<char> CaseFoldOutliers = SearchValues.Create("\u017F\u212A\u0130\u0131");

    private static bool HasCredentialKeyword(ReadOnlySpan<char> text) =>
        text.IndexOfAny(CredentialKeywords) >= 0 || text.IndexOfAny(CaseFoldOutliers) >= 0;

    private static readonly SearchValues<char> AsciiDigits = SearchValues.Create("0123456789");

    /// <summary>
    /// For rules whose every match is a whole run of digits bounded by non-digits — card numbers, ID numbers,
    /// mainland phone numbers (+ the one trailing check character): runs shorter than <paramref name="minRun"/>
    /// cannot match, and a longer one is tried in place, with one character of context each side so the
    /// <c>(?&lt;!\d)</c> / <c>(?!\d)</c> guards see what they would see in the whole text. The full-text scan of
    /// these backtracking patterns allocates and walks every digit; this touches only the candidate runs. \d also
    /// takes other scripts' digits: text with any of those is matched whole, as before.
    /// </summary>
    private static bool DigitRunMatches(ReadOnlySpan<char> text, Regex regex, int minRun)
    {
        var offset = 0;
        while (true)
        {
            var start = text[offset..].IndexOfAny(AsciiDigits);
            if (start < 0) return HasNonAsciiDigit(text) && regex.IsMatch(text);
            start += offset;
            var run = text[start..].IndexOfAnyExcept(AsciiDigits);
            var end = run < 0 ? text.Length : start + run;
            if (end - start >= minRun)
            {
                var from = Math.Max(0, start - 1);
                if (regex.IsMatch(text[from..Math.Min(text.Length, end + 2)])) return true;
            }
            if (end == text.Length) return HasNonAsciiDigit(text) && regex.IsMatch(text);
            offset = end;
        }
    }

    private static bool HasNonAsciiDigit(ReadOnlySpan<char> text)
    {
        if (Ascii.IsValid(text)) return false;
        foreach (var c in text)
            if (c > '\u007F' && char.IsDigit(c)) return true;
        return false;
    }

    private static RuleHint HintFor(Regex regex) =>
        ReferenceEquals(regex, IntranetHostRegex()) ? RuleHint.Intranet
        : ReferenceEquals(regex, EmailRegex()) ? RuleHint.Email
        : ReferenceEquals(regex, ConnStringRegex()) ? RuleHint.Scheme
        : ReferenceEquals(regex, CredentialAssignmentRegex()) ? RuleHint.Assignment
        : ReferenceEquals(regex, TelegramBotTokenRegex()) ? RuleHint.Telegram
        : ReferenceEquals(regex, StripeKeyRegex()) ? RuleHint.Stripe
        : ReferenceEquals(regex, UsSsnRegex()) ? RuleHint.Dash
        : ReferenceEquals(regex, PhoneRegex()) ? RuleHint.Phone
        : ReferenceEquals(regex, CreditCardRegex()) ? RuleHint.Card
        : ReferenceEquals(regex, CnIdRegex()) ? RuleHint.NationalId
        : RuleHint.None;

    /// <summary>
    /// JSON-aware processing: inspects string values except protocol-identified opaque media, reasoning
    /// payloads and the ids the upstream pairs verbatim (tool calls, items, server-side state; see IsOpaque).
    /// Text, URLs, tool arguments/results and unknown fields remain inspected.
    /// Leaves structure and non-string values untouched. Returns null when the input is not valid JSON
    /// (callers then fall back to plain-text <see cref="Apply"/>).
    /// </summary>
    public PrivacyApplyResult? ApplyJson(string json, EffectivePrivacyPolicy policy)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException)
        {
            return null;
        }
        using (document)
        {
            var source = document.RootElement;
            if (source.ValueKind == JsonValueKind.Null) return null;

            var merged = new PrivacyApplyResult { Text = json };
            var rewrites = Scan(source, policy, merged);
            merged.Blocked = merged.Hits.Exists(h => h.Action == PrivacyActions.Block);
            if (rewrites is null) return merged;
            // Serialized while the document — which the unchanged nodes still read from — is alive.
            JsonNode? root = source.ValueKind switch
            {
                JsonValueKind.Object => JsonObject.Create(source),
                JsonValueKind.Array => JsonArray.Create(source),
                _ => JsonValue.Create(source),
            };
            foreach (var (path, text) in rewrites) root = Rewrite(root, path, text);
            merged.Text = root!.ToJsonString(Json.Wire);
            return merged;
        }
    }

    /// <summary>
    /// <see cref="ApplyJson(string, EffectivePrivacyPolicy)"/> over a parsed body, without serializing it:
    /// scans <paramref name="source"/> and, when <paramref name="rewrite"/> is set and nothing is to be blocked,
    /// writes the redacted strings into <paramref name="target"/> — the node tree over that same element, which
    /// keeps the original values everywhere else. <see cref="PrivacyApplyResult.Text"/> is not set; the result
    /// says whether the tree changed (<see cref="PrivacyApplyResult.Redactions"/>).
    /// </summary>
    public PrivacyApplyResult ApplyJson(JsonElement source, JsonObject target, EffectivePrivacyPolicy policy, bool rewrite)
    {
        var merged = new PrivacyApplyResult();
        var rewrites = Scan(source, policy, merged);
        merged.Blocked = merged.Hits.Exists(h => h.Action == PrivacyActions.Block);
        if (rewrites is not null && rewrite && !merged.Blocked)
            foreach (var (path, text) in rewrites) Rewrite(target, path, text);
        return merged;
    }

    // Walks the element tree — no JsonNode is built, no string materialized unless a rule may match it — and
    // returns the strings to replace (null when none).
    private List<(PathStep[] Path, string Text)>? Scan(JsonElement source, EffectivePrivacyPolicy policy, PrivacyApplyResult merged)
    {
        var walk = new JsonWalk(policy, StateFor(policy), merged);
        try
        {
            walk.Walk(source, JsonScope.Root);
            return walk.Rewrites;
        }
        finally
        {
            walk.Dispose();
        }
    }

    private static JsonNode? Rewrite(JsonNode? root, PathStep[] path, string text)
    {
        if (path.Length == 0) return JsonValue.Create(text);
        var node = root!;
        for (var i = 0; i < path.Length - 1; i++)
            node = path[i].Index < 0 ? node.AsObject()[path[i].Property.Name]! : node.AsArray()[path[i].Index]!;
        var last = path[^1];
        if (last.Index < 0) node.AsObject()[last.Property.Name] = JsonValue.Create(text);
        else node.AsArray()[last.Index] = JsonValue.Create(text);
        return root;
    }

    /// <summary>One step from a container to a child: an object property, or (Index ≥ 0) an array item.</summary>
    private readonly record struct PathStep(JsonProperty Property, int Index);

    /// <summary>The per-policy state: active rules, and digests of strings known to match none of them.</summary>
    private sealed class PolicyState
    {
        // Bounds the memory of the clean set (~100 bytes an entry); full → start over.
        private const int MaxCleanEntries = 16_384;

        private readonly ConcurrentDictionary<Digest, byte> _clean = new();
        private int _cleanCount;

        public PolicyState(EffectivePrivacyPolicy policy) =>
            Active = policy.Rules.Where(r => policy.ActionFor(r.Id, r.Category) != PrivacyActions.Off)
                .Select(r => (r.Regex, HintFor(r.Regex))).ToArray();

        public (Regex Regex, RuleHint Hint)[] Active { get; }

        public bool IsClean(in Digest digest) => _clean.ContainsKey(digest);

        public void MarkClean(in Digest digest)
        {
            if (!_clean.TryAdd(digest, 0) || Interlocked.Increment(ref _cleanCount) <= MaxCleanEntries) return;
            _clean.Clear();
            Interlocked.Exchange(ref _cleanCount, 0);
        }
    }

    /// <summary>SHA-256 of a string's raw UTF-8: collision-resistant, so a cached "clean" can never cover a secret.</summary>
    private readonly record struct Digest(ulong A, ulong B, ulong C, ulong D)
    {
        public static Digest Of(ReadOnlySpan<byte> utf8)
        {
            Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(utf8, hash);
            var words = MemoryMarshal.Cast<byte, ulong>(hash);
            return new Digest(words[0], words[1], words[2], words[3]);
        }
    }

    /// <summary>
    /// Strings at least this long (raw UTF-8 bytes) are remembered as clean per policy. Agents resend their whole
    /// history every turn, so most of a body is the system prompt and turns already scanned last time: hashing
    /// (GB/s) replaces running every rule again (tens of MB/s). Only clean strings are kept, as digests — never
    /// text, never a match.
    /// </summary>
    private const int CleanCacheMinBytes = 256;

    private readonly ConditionalWeakTable<EffectivePrivacyPolicy, PolicyState> _states = new();

    private PolicyState StateFor(EffectivePrivacyPolicy policy) => _states.GetValue(policy, static p => new PolicyState(p));

    private sealed class JsonWalk(EffectivePrivacyPolicy policy, PolicyState state, PrivacyApplyResult merged) : IDisposable
    {
        private readonly List<PathStep> _path = [];
        private char[]? _chars;

        public List<(PathStep[] Path, string Text)>? Rewrites { get; private set; }

        public void Walk(JsonElement element, JsonScope scope)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    Visit(element);
                    break;
                case JsonValueKind.Object:
                {
                    // Skips are decided from the original discriminators (the element tree is never rewritten).
                    var type = KnownType(element);
                    foreach (var property in element.EnumerateObject())
                    {
                        var key = KnownKey(property);
                        if (IsOpaque(scope, type, element, key, property.Value)) continue;
                        _path.Add(new PathStep(property, -1));
                        Walk(property.Value, ChildScope(scope, type, key, property.Value));
                        _path.RemoveAt(_path.Count - 1);
                    }
                    break;
                }
                case JsonValueKind.Array:
                {
                    var index = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        _path.Add(new PathStep(default, index++));
                        Walk(item, scope);
                        _path.RemoveAt(_path.Count - 1);
                    }
                    break;
                }
            }
        }

        private void Visit(JsonElement element)
        {
            var raw = JsonMarshal.GetRawUtf8Value(element); // quoted, still escaped
            var content = raw[1..^1];
            Digest? digest = null;
            if (content.Length >= CleanCacheMinBytes)
            {
                var d = Digest.Of(content);
                if (state.IsClean(d)) return;
                digest = d;
            }

            // Unescaped into a reused buffer: a string becomes a string only when some rule matches it.
            if (_chars is null || _chars.Length < content.Length)
            {
                if (_chars is not null) ArrayPool<char>.Shared.Return(_chars);
                _chars = ArrayPool<char>.Shared.Rent(Math.Max(content.Length, 256));
            }
            int length;
            if (content.IndexOf((byte)'\\') < 0)
            {
                length = Encoding.UTF8.GetChars(content, _chars);
            }
            else
            {
                var reader = new Utf8JsonReader(raw);
                reader.Read();
                length = reader.CopyString(_chars);
            }
            var chars = _chars.AsSpan(0, length);
            if (!AnyRuleMatches(chars, state))
            {
                if (digest is { } clean) state.MarkClean(clean);
                return;
            }

            var text = new string(chars);
            var result = ApplyInto(text, policy, merged);
            if (!ReferenceEquals(result, text)) (Rewrites ??= []).Add((_path.ToArray(), result));
        }

        public void Dispose()
        {
            if (_chars is not null) ArrayPool<char>.Shared.Return(_chars);
            _chars = null;
        }
    }

    private enum JsonScope { Root, Content, ToolContent, Call, Source, InlineData, ImageUrl, Audio, File, Ordinary }

    // The "type" discriminators the scope rules compare against (any other type behaves like none).
    private static readonly string[] KnownTypes =
    [
        "tool_result", "function_call_output", "image", "document", "image_url", "input_image", "input_audio", "file",
        "input_file", "thinking", "redacted_thinking", "reasoning", "base64",
    ];

    // The property names the scope rules look at (any other name is ordinary).
    private static readonly string[] KnownKeys =
    [
        "messages", "input", "system", "contents", "systemInstruction", "system_instruction", "content", "parts", "output",
        "source", "image_url", "input_audio", "file", "inlineData", "inline_data", "data", "url", "file_data", "signature",
        "encrypted_content", "thoughtSignature", "thought_signature", "id", "tool_use_id", "tool_call_id", "call_id",
        "previous_response_id", "tool_calls", "functionCall", "functionResponse", "function_call", "function_response",
        "approval_request_id", "conversation", "container", "cachedContent", "cached_content",
    ];

    private static readonly byte[][] KnownKeysUtf8 = KnownKeys.Select(Encoding.UTF8.GetBytes).ToArray();

    private static string? KnownType(JsonElement obj)
    {
        if (!obj.TryGetProperty("type"u8, out var type) || type.ValueKind != JsonValueKind.String) return null;
        foreach (var known in KnownTypes)
            if (type.ValueEquals(known)) return known;
        return null;
    }

    private static string? KnownKey(JsonProperty property)
    {
        var raw = JsonMarshal.GetRawUtf8PropertyName(property);
        if (raw.IndexOf((byte)'\\') >= 0)
        {
            var name = property.Name;
            return Array.Find(KnownKeys, k => k == name);
        }
        for (var i = 0; i < KnownKeysUtf8.Length; i++)
            if (raw.SequenceEqual(KnownKeysUtf8[i])) return KnownKeys[i];
        return null;
    }

    private static JsonScope ChildScope(JsonScope scope, string? type, string? key, JsonElement child) => (scope, key) switch
    {
        (JsonScope.Root, "messages" or "input" or "system" or "contents" or "systemInstruction" or "system_instruction") => JsonScope.Content,
        (JsonScope.Content, "content") when type == "tool_result" && child.ValueKind == JsonValueKind.Array => JsonScope.ToolContent,
        (JsonScope.Content, "content" or "parts") when child.ValueKind == JsonValueKind.Array => JsonScope.Content,
        (JsonScope.Content, "tool_calls") when child.ValueKind == JsonValueKind.Array => JsonScope.Call,
        (JsonScope.Content, "functionCall" or "functionResponse" or "function_call" or "function_response")
            when child.ValueKind == JsonValueKind.Object => JsonScope.Call,
        // Responses' {"conversation":{"id":…}} and Anthropic's {"container":{"id":…}}: like a call, only the id is opaque.
        (JsonScope.Root, "conversation" or "container") when child.ValueKind == JsonValueKind.Object => JsonScope.Call,
        (JsonScope.Content, "output") when type == "function_call_output" && child.ValueKind == JsonValueKind.Array => JsonScope.ToolContent,
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

    private static bool IsOpaque(JsonScope scope, string? type, JsonElement obj, string? key, JsonElement value)
    {
        // Arbitrary tool JSON stays inspected even when it contains fields called data/signature.
        // Skip only opaque protocol slots, never captions, filenames, URLs or whole media objects.
        if (key is null || value.ValueKind != JsonValueKind.String) return false;
        return (scope, key) switch
        {
            (JsonScope.Source, "data") => type == "base64" && IsBinaryMedia(StringProperty(obj, "media_type")),
            (JsonScope.InlineData, "data") => IsBinaryMedia(StringValue(
                obj.TryGetProperty("mimeType"u8, out var mime) ? mime : obj.TryGetProperty("mime_type"u8, out var mime2) ? mime2 : default)),
            (JsonScope.Audio, "data") => true,
            (JsonScope.ImageUrl, "url") => IsBinaryDataUrl(value),
            (JsonScope.File, "file_data") => IsBinaryDataUrl(value),
            (JsonScope.Content or JsonScope.ToolContent, "image_url") when type == "input_image" => IsBinaryDataUrl(value),
            (JsonScope.Content or JsonScope.ToolContent, "file_data") when type == "input_file" => IsBinaryDataUrl(value),
            (JsonScope.Content, "signature") => type == "thinking",
            (JsonScope.Content, "data") => type == "redacted_thinking",
            (JsonScope.Content, "encrypted_content") => type == "reasoning",
            (JsonScope.Content, "thoughtSignature" or "thought_signature") => true,
            // Tool-call / item ids are pairing keys the upstream issued and checks verbatim: rewriting one (an 11-digit run
            // read as a phone number, a custom long-token rule) breaks every later turn of the conversation (issue #10):
            // Anthropic tool_use / server_tool_use / mcp_tool_use ids and every *_tool_result's tool_use_id, Chat
            // tool_calls[].id and tool_call_id, Responses item ids (fc_/rs_/msg_…), call_id and approval_request_id, Gemini
            // functionCall / functionResponse ids. Arguments, tool results and text stay inspected.
            (JsonScope.Content, "id" or "tool_use_id" or "tool_call_id" or "call_id" or "approval_request_id") => true,
            (JsonScope.Call, "id") => true,
            // Server-side state the request continues: Responses previous_response_id / conversation, Anthropic container,
            // Gemini cachedContent.
            (JsonScope.Root, "previous_response_id" or "conversation" or "container" or "cachedContent" or "cached_content") => true,
            _ => false,
        };
    }

    private static string? StringProperty(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) ? StringValue(value) : null;

    private static string? StringValue(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsBinaryMedia(ReadOnlySpan<char> media) =>
        media.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
        media.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
        media.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
        media.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) ||
        media.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase);

    private static bool IsBinaryMedia(string? media) => media is not null && IsBinaryMedia(media.AsSpan());

    // A data URL's header is in its first 136 characters (the ';' must come before 128): decoding at most
    // 512 bytes covers that, so a multi-megabyte base64 payload is never turned into a string to be skipped.
    private static bool IsBinaryDataUrl(JsonElement value)
    {
        var content = JsonMarshal.GetRawUtf8Value(value)[1..^1];
        var head = content[..Math.Min(content.Length, 512)];
        if (head.IndexOf((byte)'\\') >= 0) return IsBinaryDataUrl(value.GetString()!);
        Span<char> chars = stackalloc char[512];
        var length = Encoding.UTF8.GetChars(head, chars);
        return IsBinaryDataUrl(chars[..length]);
    }

    private static bool IsBinaryDataUrl(ReadOnlySpan<char> text)
    {
        if (!text.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
        var separator = text[5..].IndexOf(';');
        if (separator >= 0) separator += 5;
        return separator is > 5 and < 128 && IsBinaryMedia(text[5..separator]) &&
            text[separator..].StartsWith(";base64,", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>Keeps at most <see cref="MaxSamples"/> distinct previews per rule.</summary>
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
    private const int MaxFullSampleChars = 256;

    /// <summary>The matched value as logged when full samples are on: unmasked, newlines shown as ⏎, capped in length.</summary>
    private static string FullSample(ReadOnlySpan<char> value)
    {
        var text = value.Length > MaxFullSampleChars ? value[..MaxFullSampleChars] : value;
        var sb = new StringBuilder(text.Length + 1);
        foreach (var c in text)
        {
            if (c == '\r') continue;
            sb.Append(c == '\n' ? '\u23ce' : c);
        }
        if (value.Length > MaxFullSampleChars) sb.Append('…');
        return sb.ToString();
    }

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

    [GeneratedRegex(@"(?<![A-Za-z0-9])sk-ant-[A-Za-z0-9_\-]{20,}", RegexOptions.CultureInvariant, BuiltinTimeoutMs)]
    private static partial Regex SkAntRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])sk-[A-Za-z0-9_\-]{20,}", RegexOptions.CultureInvariant, BuiltinTimeoutMs)]
    private static partial Regex SkOpenAiRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])AIza[0-9A-Za-z_\-]{35}", RegexOptions.CultureInvariant, BuiltinTimeoutMs)]
    private static partial Regex GoogleKeyRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])astra-[a-z0-9\-]{1,32}-[A-Za-z0-9]{20,}", RegexOptions.CultureInvariant, BuiltinTimeoutMs)]
    private static partial Regex AstraLocalKeyRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:xai|gsk)_[A-Za-z0-9]{20,}", RegexOptions.CultureInvariant, BuiltinTimeoutMs)]
    private static partial Regex VendorKeysRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})", RegexOptions.CultureInvariant, BuiltinTimeoutMs)]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])glpat-[A-Za-z0-9_\-]{16,}", RegexOptions.CultureInvariant, BuiltinTimeoutMs)]
    private static partial Regex GitLabTokenRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])xox[baprs]-[A-Za-z0-9\-]{10,}", RegexOptions.CultureInvariant, BuiltinTimeoutMs)]
    private static partial Regex SlackTokenRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])hf_[A-Za-z0-9]{20,}", RegexOptions.CultureInvariant, BuiltinTimeoutMs)]
    private static partial Regex HuggingFaceTokenRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:(?:sk|rk)_live|whsec)_[A-Za-z0-9]{16,}", RegexOptions.CultureInvariant, BuiltinTimeoutMs)]
    private static partial Regex StripeKeyRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])\d{8,10}:AA[A-Za-z0-9_\-]{33}(?![A-Za-z0-9])", RegexOptions.CultureInvariant, BuiltinTimeoutMs)]
    private static partial Regex TelegramBotTokenRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:AKIA|ASIA)[0-9A-Z]{16}(?![0-9A-Z])", RegexOptions.CultureInvariant, BuiltinTimeoutMs)]
    private static partial Regex AwsAccessKeyRegex();

    [GeneratedRegex(@"(?:(?<![A-Za-z_\d])1[3-9]\d{9}(?![A-Za-z_\d])|(?<!\d)\+[0-9]{1,3}[\s\-]?[0-9]{6,14}(?!\d))", RegexOptions.CultureInvariant, BuiltinTimeoutMs)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"(?:(?<![\w.])(?:10\.\d{1,3}\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3})(?![\w.])|(?<![\w.\-])[A-Za-z0-9][A-Za-z0-9\-]*(?:\.[A-Za-z0-9][A-Za-z0-9\-]*)*\.(?:internal|local|lan|corp)(?![\w\-]))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, BuiltinTimeoutMs)]
    private static partial Regex IntranetHostRegex();

    [GeneratedRegex(@"(?<!\d)(?:4\d{12}(?:\d{3})?|5[1-5]\d{14}|3[47]\d{13}|6(?:011|5\d{2})\d{12}|62\d{14,17})(?!\d)", RegexOptions.CultureInvariant, BuiltinTimeoutMs)]
    private static partial Regex CreditCardRegex();

    [GeneratedRegex(@"(?<!\d)[1-9]\d{5}(?:19|20)\d{2}(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])\d{3}[\dXx](?!\d)", RegexOptions.CultureInvariant, BuiltinTimeoutMs)]
    private static partial Regex CnIdRegex();

    [GeneratedRegex(@"(?<!\d)(?!000|666|9\d{2})\d{3}-(?!00)\d{2}-(?!0000)\d{4}(?!\d)", RegexOptions.CultureInvariant, BuiltinTimeoutMs)]
    private static partial Regex UsSsnRegex();

    [GeneratedRegex(@"(?:postgres(?:ql)?|mysql|mongodb(?:\+srv)?|redis|amqps?)://[^\s:@/]+:[^\s@]+@[^\s]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, BuiltinTimeoutMs)]
    private static partial Regex ConnStringRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:password|passwd|pwd|pass|secret|token|api[_\-]?key|access[_\-]?key)[""']?\s*[:=]\s*[""']?(?!\[REDACTED:)[^\s""']{6,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, BuiltinTimeoutMs)]
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
