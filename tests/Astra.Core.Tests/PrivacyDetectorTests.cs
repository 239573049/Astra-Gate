using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core.Privacy;

namespace Astra.Core.Tests;

public class PrivacyDetectorTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData("jwt")]
    [InlineData("pem")]
    [InlineData("email")]
    [InlineData("text")]
    public void Large_Text_Is_Fully_Scanned_Without_Timeout(string shape)
    {
        var text = shape switch
        {
            "jwt" => string.Concat(Enumerable.Repeat("eyJ", 100_000)),
            "pem" => string.Concat(Enumerable.Repeat("-----BEGIN PRIVATE KEY-----\n", 10_000)),
            "email" => new string('%', 300_000),
            _ => new string('a', 1_000_000),
        };
        text += "\ncontact alice@example.com";
        var policy = Policy();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        PrivacyApplyResult? result = null;
        var error = Record.Exception(() => result = _detector.Apply(text, policy));
        output.WriteLine($"{shape}: {clock.Elapsed.TotalMilliseconds:F2} ms; {GC.GetAllocatedBytesForCurrentThread() - allocated:N0} bytes; {error?.GetType().Name ?? "ok"}");
        Assert.Null(error);
        Assert.Contains(result!.Redactions, r => r.Original == "alice@example.com");
    }

    [Theory]
    [InlineData("jwt", "eyJabcdefgh.ijklmnop.qrstuvwx eyJabcdefgh.ijklmnop.qrstuvwx")]
    [InlineData("pem", "-----BEGIN PRIVATE KEY-----first-----BEGIN RSA PRIVATE KEY-----second-----END PRIVATE KEY-----tail-----END RSA PRIVATE KEY-----")]
    public void Linear_Rules_Preserve_Matched_Spans(string shape, string text)
    {
        var category = shape == "jwt" ? PrivacyCategories.Jwt : PrivacyCategories.PrivateKey;
        var pattern = shape == "jwt"
            ? @"eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}"
            : @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----";
        var expected = PrivacyDetector.Compile(pattern, shape == "pem").Matches(text).Select(m => m.Value);
        var policy = Policy(defaultAction: PrivacyActions.Off,
            categoryActions: new() { [category] = PrivacyActions.Redact });
        Assert.Equal(expected, _detector.Apply(text, policy).Redactions.Select(r => r.Original));
    }

    [Fact]
    public void Custom_Backtracking_Timeout_Is_Not_Silently_Ignored()
    {
        var policy = Policy(defaultAction: PrivacyActions.Off,
            categoryActions: new() { [PrivacyCategories.Custom] = PrivacyActions.Redact },
            custom: [new() { Id = "custom.expensive", Pattern = "^(a+)+$" }]);
        Assert.Throws<System.Text.RegularExpressions.RegexMatchTimeoutException>(() =>
            _detector.Apply(new string('a', 20_000) + "!", policy));
    }

    [Fact]
    public void Email_Optimized_Matches_Preserve_Legacy_Boundaries()
    {
        var legacy = PrivacyDetector.Compile(@"(?<![\w.+\-])[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}(?![\w\-])");
        var policy = Policy(defaultAction: PrivacyActions.Off,
            categoryActions: new() { [PrivacyCategories.Email] = PrivacyActions.Redact });
        var random = new Random(42);
        string[] pieces = ["a", "%", "_", ".", "-", "+", "@", "com", " ", "é", "中", "\u0301", "\n", "a@b.co", "foo%bar@example.com", "a@b.com@c.org"];
        for (var i = 0; i < 2_000; i++)
        {
            var text = string.Concat(Enumerable.Range(0, 15).Select(_ => pieces[random.Next(pieces.Length)]));
            var expected = legacy.Matches(text).Select(m => m.Value).ToArray();
            Assert.Equal(expected, _detector.Apply(text, policy).Redactions.Select(r => r.Original));
            Assert.Equal(expected.Length, _detector.Scan(text, policy).Sum(h => h.Count));
        }
    }

    [Theory]
    [InlineData("{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"PAYLOAD\"}}")]
    [InlineData("{\"type\":\"document\",\"source\":{\"type\":\"base64\",\"media_type\":\"application/pdf\",\"data\":\"PAYLOAD\"}}")]
    [InlineData("{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64,PAYLOAD\"}}")]
    [InlineData("{\"type\":\"input_image\",\"image_url\":\"data:image/png;base64,PAYLOAD\"}")]
    [InlineData("{\"type\":\"file\",\"file\":{\"file_data\":\"data:application/pdf;base64,PAYLOAD\"}}")]
    [InlineData("{\"type\":\"input_file\",\"file_data\":\"data:application/pdf;base64,PAYLOAD\"}")]
    [InlineData("{\"type\":\"input_audio\",\"input_audio\":{\"data\":\"PAYLOAD\",\"format\":\"wav\"}}")]
    [InlineData("{\"inlineData\":{\"mimeType\":\"image/jpeg\",\"data\":\"PAYLOAD\"}}")]
    [InlineData("{\"inline_data\":{\"mime_type\":\"audio/wav\",\"data\":\"PAYLOAD\"}}")]
    [InlineData("{\"type\":\"thinking\",\"thinking\":\"hello\",\"signature\":\"PAYLOAD\"}")]
    [InlineData("{\"type\":\"redacted_thinking\",\"data\":\"PAYLOAD\"}")]
    [InlineData("{\"type\":\"reasoning\",\"encrypted_content\":\"PAYLOAD\"}")]
    [InlineData("{\"thoughtSignature\":\"PAYLOAD\",\"text\":\"hello\"}")]
    public void Json_Opaque_Protocol_Payloads_Are_Not_Scanned(string block)
    {
        // Deliberately matches even Base64-alphabet data: skipping is structural, not heuristic.
        var payload = new string('A', 1_000_000) + "AKIAIOSFODNN7EXAMPLE";
        var json = "{\"messages\":[{\"content\":[" + block.Replace("PAYLOAD", payload) +
            ",{\"type\":\"text\",\"text\":\"alice@example.com\"}]}]}";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = _detector.ApplyJson(json, Policy(custom:
            [new() { Id = "custom.payload", Pattern = "A{1000}" }]))!;
        output.WriteLine($"opaque: {clock.Elapsed.TotalMilliseconds:F2} ms");
        Assert.Single(result.Redactions);
        Assert.Equal("alice@example.com", result.Redactions[0].Original);
        Assert.Contains(payload, result.Text);
    }

    [Theory]
    [InlineData("{\"type\":\"tool_use\",\"input\":{\"type\":\"thinking\",\"signature\":\"alice@example.com\"}}")]
    [InlineData("{\"type\":\"tool_result\",\"content\":\"alice@example.com\"}")]
    [InlineData("{\"type\":\"thinking\",\"thinking\":\"alice@example.com\",\"signature\":\"opaque\"}")]
    [InlineData("{\"type\":\"text\",\"data\":\"alice@example.com\"}")]
    [InlineData("{\"type\":\"image_url\",\"image_url\":{\"url\":\"https://example.com/alice@example.com\"}}")]
    [InlineData("{\"type\":\"document\",\"source\":{\"type\":\"text\",\"data\":\"alice@example.com\"}}")]
    [InlineData("{\"type\":\"document\",\"source\":{\"type\":\"base64\",\"media_type\":\"text/plain\",\"data\":\"alice@example.com\"}}")]
    [InlineData("{\"inlineData\":{\"mimeType\":\"text/plain\",\"data\":\"alice@example.com\"}}")]
    public void Json_Text_Unknown_Data_And_Tool_Arguments_Are_Still_Scanned(string block)
    {
        var result = _detector.ApplyJson("{\"messages\":[{\"content\":[" + block + "]}]}", Policy())!;
        Assert.Contains(result.Redactions, r => r.Original == "alice@example.com");
    }

    [Theory]
    [InlineData("\"alice@example.com\"")]
    [InlineData("[\"alice@example.com\",null,\"bob@example.org\"]")]
    public void Json_Root_And_Array_Strings_Are_Rewritten(string json)
    {
        var result = _detector.ApplyJson(json, Policy())!;
        Assert.DoesNotContain("@", result.Text);
        Assert.NotEmpty(result.Redactions);
    }

    [Theory]
    [InlineData("{\"contents\":[{\"parts\":[{\"inlineData\":{\"mimeType\":\"image/png\",\"data\":\"OPAQUE\"}},{\"text\":\"alice@example.com\"}]}]}")]
    [InlineData("{\"input\":[{\"type\":\"reasoning\",\"encrypted_content\":\"OPAQUE\"},{\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"alice@example.com\"}]}]}")]
    [InlineData("{\"input\":[{\"type\":\"function_call_output\",\"output\":[{\"type\":\"input_image\",\"image_url\":\"data:image/png;base64,OPAQUE\"},{\"type\":\"input_text\",\"text\":\"alice@example.com\"}]}]}")]
    public void Actual_Protocol_Paths_Skip_Only_Opaque_Values(string json)
    {
        var result = _detector.ApplyJson(json, Policy(custom:
            [new() { Id = "custom.opaque", Pattern = "OPAQUE" }]))!;
        Assert.Equal("alice@example.com", Assert.Single(result.Redactions).Original);
        Assert.Contains("OPAQUE", result.Text);
    }

    [Theory]
    [InlineData("{\"contents\":[{\"parts\":[{\"functionCall\":{\"args\":{\"thoughtSignature\":\"alice@example.com\"}}}]}]}")]
    [InlineData("{\"contents\":[{\"parts\":[{\"functionResponse\":{\"response\":{\"inlineData\":{\"mimeType\":\"image/png\",\"data\":\"alice@example.com\"}}}}]}]}")]
    [InlineData("{\"messages\":[{\"content\":[{\"type\":\"tool_result\",\"content\":{\"type\":\"thinking\",\"signature\":\"alice@example.com\"}}]}]}")]
    [InlineData("{\"messages\":[{\"content\":[{\"type\":\"tool_result\",\"content\":[{\"type\":\"thinking\",\"signature\":\"alice@example.com\"}]}]}]}")]
    [InlineData("{\"input\":[{\"type\":\"function_call_output\",\"output\":[{\"type\":\"reasoning\",\"encrypted_content\":\"alice@example.com\"}]}]}")]
    public void Arbitrary_Tool_Json_Does_Not_Inherit_Protocol_Skips(string json)
    {
        Assert.Equal("alice@example.com", Assert.Single(_detector.ApplyJson(json, Policy())!.Redactions).Original);
    }

    // Issue #10: a tool id with an 11-digit run read as a phone number was rewritten, and the upstream refused every
    // later turn because the id it issued no longer matched.
    [Theory]
    [InlineData("""{"messages":[{"role":"assistant","content":[{"type":"tool_use","id":"call_01a16801111717199920954","name":"t","input":{"q":"13800138000"}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"call_01a16801111717199920954","content":"13800138000"}]}]}""")]
    [InlineData("""{"messages":[{"role":"assistant","tool_calls":[{"id":"call_01a16801111717199920954","type":"function","function":{"name":"t","arguments":"13800138000"}}]},{"role":"tool","tool_call_id":"call_01a16801111717199920954","content":"13800138000"}]}""")]
    [InlineData("""{"previous_response_id":"resp_013800138000","input":[{"type":"function_call","id":"fc_013800138000","call_id":"call_013800138000","name":"t","arguments":"13800138000"},{"type":"function_call_output","call_id":"call_013800138000","output":"13800138000"}]}""")]
    [InlineData("""{"contents":[{"parts":[{"functionCall":{"id":"013800138000","name":"t","args":{"q":"13800138000"}}}]},{"parts":[{"functionResponse":{"id":"013800138000","name":"t","response":{"r":"13800138000"}}}]}]}""")]
    public void Tool_Call_Ids_Pass_Unchanged_While_Their_Arguments_Are_Scanned(string json)
    {
        var policy = Policy(custom: [new() { Id = "custom.ids", Pattern = "013800138000" }]);
        var result = _detector.ApplyJson(json, policy)!;
        Assert.All(result.Redactions, r => Assert.Equal("13800138000", r.Original));
        Assert.Equal(2, result.Redactions.Count);
        var ids = System.Text.RegularExpressions.Regex.Matches(json, "\"(?:id|tool_use_id|tool_call_id|call_id|previous_response_id)\":\"([^\"]+)\"");
        Assert.NotEmpty(ids);
        foreach (System.Text.RegularExpressions.Match id in ids) Assert.Contains(id.Value, result.Text);
    }

    [Theory]
    [InlineData("call_01a16801111717cf820d7a0a", false)]
    [InlineData("ref13800138000", false)]
    [InlineData("13800138000x", false)]
    [InlineData("电话13800138000", true)]
    [InlineData("phone: 13800138000.", true)]
    public void Phone_Rule_Ignores_Digit_Runs_Inside_Identifiers(string text, bool redacted)
    {
        var policy = Policy(defaultAction: PrivacyActions.Off,
            categoryActions: new() { [PrivacyCategories.Phone] = PrivacyActions.Redact });
        Assert.Equal(redacted, _detector.Apply(text, policy).Redactions.Count > 0);
    }

    [Fact]
    public void Json_Warn_And_No_Match_Preserve_Original_Formatting()
    {
        const string json = "{  \"data\": \"alice@example.com\", \"null\": null }";
        Assert.Same(json, _detector.ApplyJson(json, Policy(defaultAction: PrivacyActions.Warn))!.Text);
        Assert.Same(json, _detector.ApplyJson(json, Policy(defaultAction: PrivacyActions.Off))!.Text);
    }

    [Fact]
    public void Custom_Zero_Length_Matches_Preserve_Replacement_Semantics()
    {
        var policy = Policy(defaultAction: PrivacyActions.Off,
            categoryActions: new() { [PrivacyCategories.Custom] = PrivacyActions.Redact },
            custom: [new() { Id = "custom.empty", Pattern = "(?=a)|$" }]);
        var result = _detector.Apply("aa", policy);
        Assert.Equal("[REDACTED:custom#1]a[REDACTED:custom#2]a[REDACTED:custom#3]", result.Text);
        Assert.Equal(3, result.Hits.Single().Count);
    }

    private readonly PrivacyDetector _detector = new();

    [Theory]
    [InlineData("""{"model":"m","system":"ops alice@example.com","messages":[{"role":"user","content":[{"type":"text","text":"key AKIAIOSFODNN7EXAMPLE\nand bob@test.org"}]}]}""")]
    [InlineData("""{"messages":[{"content":"mail alice\u0040example.com, again alice@example.com"}],"n":1.50,"tags":["10.0.0.5",null,true]}""")]
    [InlineData("""{"input":[{"type":"function_call_output","output":[{"type":"input_image","image_url":"data:image/png;base64,AKIAIOSFODNN7EXAMPLE"},{"type":"input_text","text":"x@y.io"}]}]}""")]
    public void Parsed_Body_Redaction_Matches_The_Text_Path(string json)
    {
        var policy = Policy();
        var expected = _detector.ApplyJson(json, policy)!;

        using var document = JsonDocument.Parse(json);
        var tree = JsonObject.Create(document.RootElement)!;
        var actual = new PrivacyDetector().ApplyJson(document.RootElement, tree, policy, rewrite: true);

        Assert.Equal(expected.Redactions.Select(r => (r.Placeholder, r.Original)), actual.Redactions.Select(r => (r.Placeholder, r.Original)));
        Assert.Equal(expected.Hits.Select(h => (h.RuleId, h.Action, h.Count)), actual.Hits.Select(h => (h.RuleId, h.Action, h.Count)));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected.Text), tree));
    }

    [Fact]
    public void Parsed_Body_Is_Untouched_Without_Rewrite_Or_When_Blocked()
    {
        const string json = """{"messages":[{"content":"alice@example.com AKIAIOSFODNN7EXAMPLE"}]}""";
        using var document = JsonDocument.Parse(json);

        var dryTree = JsonObject.Create(document.RootElement)!;
        Assert.NotEmpty(_detector.ApplyJson(document.RootElement, dryTree, Policy(), rewrite: false).Redactions);
        Assert.Equal(json, dryTree.ToJsonString());

        var blockedTree = JsonObject.Create(document.RootElement)!;
        var blocked = _detector.ApplyJson(document.RootElement, blockedTree,
            Policy(categoryActions: new() { [PrivacyCategories.AwsKey] = PrivacyActions.Block }), rewrite: true);
        Assert.True(blocked.Blocked);
        Assert.Equal(json, blockedTree.ToJsonString());
    }

    [Theory]
    [InlineData("call 13\u0661\u0662\u0663\u0664\u0665\u0666\u0667\u0668\u0669 now", PrivacyCategories.Phone)] // \d takes other scripts' digits
    [InlineData("intl +44 7911123456", PrivacyCategories.Phone)]
    [InlineData("card 4111111111111111.", PrivacyCategories.CreditCard)]
    [InlineData("id 11010519491231002X", PrivacyCategories.NationalId)]
    [InlineData("PASSWORD = hunter2hunter2", PrivacyCategories.Credential)]
    [InlineData("db postgres://u:p4ss@db.internal/x", PrivacyCategories.Credential)]
    public void Literal_Prefilters_Never_Hide_A_Match(string text, string category)
    {
        var policy = Policy(defaultAction: PrivacyActions.Off, categoryActions: new() { [category] = PrivacyActions.Redact });
        Assert.NotEmpty(_detector.Apply(text, policy).Redactions);
        Assert.NotEmpty(_detector.ApplyJson(JsonSerializer.Serialize(new { text }), policy)!.Redactions);
    }

    [Fact]
    public void Clean_Cache_Is_Per_Policy_And_Never_Hides_A_Secret()
    {
        var detector = new PrivacyDetector();
        var filler = new string('x', 2_000);
        var withEmail = $$"""{"messages":[{"content":"{{filler}} alice@example.com"}]}""";
        var clean = $$"""{"messages":[{"content":"{{filler}} nothing here"}]}""";

        // Clean for a policy that ignores email — and remembered as clean for that policy only.
        var emailOff = Policy(categoryActions: new() { [PrivacyCategories.Email] = PrivacyActions.Off });
        Assert.Empty(detector.ApplyJson(withEmail, emailOff)!.Hits);
        Assert.Empty(detector.ApplyJson(withEmail, emailOff)!.Hits);

        var redact = Policy();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("alice@example.com", Assert.Single(detector.ApplyJson(withEmail, redact)!.Redactions).Original);
            Assert.Empty(detector.ApplyJson(clean, redact)!.Hits);
        }
    }

    private static EffectivePrivacyPolicy Policy(
        string defaultAction = PrivacyActions.Redact,
        Dictionary<string, string>? categoryActions = null,
        List<PrivacyCustomRule>? custom = null,
        string? clientKind = null) =>
        EffectivePrivacyPolicy.Build(new PrivacySettings
        {
            Enabled = true,
            DryRun = false,
            DefaultAction = defaultAction,
            CategoryActions = categoryActions ?? [],
            CustomRules = custom ?? [],
        }, clientKind);

    private const string SecretText = """
        anthropic=sk-ant-api03-AAAAAAAAAAAAAAAAAAAAAAAAAA
        google=AIzaSyA1234567890abcdefghijklmnopqrstuvwxyz
        aws=AKIAIOSFODNN7EXAMPLE
        token=eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJVadQssw5c
        mail=alice@example.com
        phone=13812345678
        -----BEGIN RSA PRIVATE KEY-----
        MIIBOgIBAAJBAK
        -----END RSA PRIVATE KEY-----
        """;

    [Fact]
    public void Detects_All_BuiltIn_Categories()
    {
        var hits = _detector.Scan(SecretText, Policy());

        var categories = hits.Select(h => h.Category).ToHashSet();
        Assert.Superset(new HashSet<string>
        {
            PrivacyCategories.ApiKey, PrivacyCategories.AwsKey, PrivacyCategories.Jwt,
            PrivacyCategories.PrivateKey, PrivacyCategories.Email, PrivacyCategories.Phone,
        }, categories);
        // Every hit with a redact default reports the redact action.
        Assert.All(hits, h => Assert.Equal(PrivacyActions.Redact, h.Action));
    }

    [Fact]
    public void Redact_Replaces_Each_Match_And_Counters_Per_Category()
    {
        var result = _detector.Apply("mail a@b.com or bob@test.org, again a@b.com", Policy());

        Assert.False(result.Blocked);
        Assert.DoesNotContain("@", result.Text);
        Assert.Equal(3, result.Redactions.Count);
        Assert.Equal(PrivacyDetector.PlaceholderFor(PrivacyCategories.Email, 1), result.Redactions[0].Placeholder);
        Assert.Equal(PrivacyDetector.PlaceholderFor(PrivacyCategories.Email, 2), result.Redactions[1].Placeholder);
        Assert.Equal("a@b.com", result.Redactions[0].Original);
        Assert.Equal("a@b.com", result.Redactions[2].Original); // same value redacted twice, distinct placeholders
        Assert.NotEqual(result.Redactions[0].Placeholder, result.Redactions[2].Placeholder);
    }

    [Fact]
    public void Warn_Records_Hits_But_Never_Modifies_Text()
    {
        var result = _detector.Apply("mail a@b.com", Policy(defaultAction: PrivacyActions.Warn));

        Assert.Single(result.Hits);
        Assert.Equal(PrivacyActions.Warn, result.Hits[0].Action);
        Assert.Equal("mail a@b.com", result.Text);
        Assert.Empty(result.Redactions);
    }

    [Fact]
    public void Block_Action_Flags_The_Request()
    {
        var policy = Policy(defaultAction: PrivacyActions.Redact,
            categoryActions: new Dictionary<string, string> { [PrivacyCategories.AwsKey] = PrivacyActions.Block });

        var withKey = _detector.Apply($"mail a@b.com key AKIAIOSFODNN7EXAMPLE", policy);
        Assert.True(withKey.Blocked);
        Assert.Contains(PrivacyActions.Block, withKey.Hits.Select(h => h.Action));

        var withoutKey = _detector.Apply("mail a@b.com", policy);
        Assert.False(withoutKey.Blocked);
    }

    [Fact]
    public void Off_Category_Is_Removed_From_The_Rule_Set()
    {
        var policy = Policy(categoryActions: new Dictionary<string, string>
        {
            [PrivacyCategories.Email] = PrivacyActions.Off,
            [PrivacyCategories.Phone] = PrivacyActions.Off,
        });

        var result = _detector.Apply("mail a@b.com phone 13812345678", policy);
        Assert.Empty(result.Hits);
        Assert.Equal("mail a@b.com phone 13812345678", result.Text);
    }

    [Fact]
    public void Client_Default_Action_Wins_Over_Global_Default()
    {
        var settings = new PrivacySettings
        {
            Enabled = true,
            DefaultAction = PrivacyActions.Redact,
            ClientDefaults = new Dictionary<string, string> { ["claude-code"] = PrivacyActions.Warn },
        };

        var forClaude = EffectivePrivacyPolicy.Build(settings, "claude-code");
        var result = _detector.Apply("mail a@b.com", forClaude);
        Assert.Equal("mail a@b.com", result.Text);
        Assert.Equal(PrivacyActions.Warn, result.Hits.Single().Action);

        var forOthers = EffectivePrivacyPolicy.Build(settings, "codex");
        var redacted = _detector.Apply("mail a@b.com", forOthers);
        Assert.NotEqual("mail a@b.com", redacted.Text);
    }

    [Fact]
    public void Custom_Rules_Run_Like_BuiltIns_And_Bad_Patterns_Are_Rejected()
    {
        var custom = new List<PrivacyCustomRule>
        {
            new() { Id = "custom.employee-id", Name = "员工编号", Pattern = @"EMP-[0-9]{6}" },
        };
        var result = _detector.Apply("employee EMP-123456 onboarded", Policy(custom: custom));

        Assert.Equal("employee [REDACTED:custom#1] onboarded", result.Text);
        Assert.Contains(result.Hits, h => h.RuleId == "custom.employee-id");

        Assert.ThrowsAny<ArgumentException>(() => Policy(custom:
        [
            new() { Id = "custom.bad", Name = "bad", Pattern = "([" },
        ]));
    }

    [Fact]
    public void ApplyJson_Rewrites_String_Values_And_Preserves_Structure()
    {
        const string json = """
            {
              "model": "gpt-5",
              "max_tokens": 100,
              "stream": true,
              "temperature": 0.3,
              "messages": [
                {"role": "user", "content": "mail a@b.com from 192.168.1.5, call 13812345678"}
              ]
            }
            """;

        var result = _detector.ApplyJson(json, Policy())!;

        Assert.NotNull(result);
        Assert.False(result.Blocked);
        Assert.True(result.Redactions.Count >= 3);

        var reparsed = System.Text.Json.JsonDocument.Parse(result.Text).RootElement;
        Assert.Equal("gpt-5", reparsed.GetProperty("model").GetString());
        Assert.Equal(100, reparsed.GetProperty("max_tokens").GetInt32());
        Assert.True(reparsed.GetProperty("stream").GetBoolean());
        Assert.Equal(0.3, reparsed.GetProperty("temperature").GetDouble());

        var content = reparsed.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.DoesNotContain("a@b.com", content);
        Assert.DoesNotContain("192.168.1.5", content);
        Assert.DoesNotContain("13812345678", content);
        Assert.Contains(PrivacyDetector.PlaceholderFor(PrivacyCategories.Email, 1), content);
        Assert.Contains(PrivacyDetector.PlaceholderFor(PrivacyCategories.Intranet, 1), content);
        Assert.Contains(PrivacyDetector.PlaceholderFor(PrivacyCategories.Phone, 1), content);
    }

    [Fact]
    public void ApplyJson_Returns_Null_For_Non_Json_Input()
    {
        Assert.Null(_detector.ApplyJson("this is not json {", Policy()));
    }

    [Fact]
    public void Decimal_Like_Numbers_Are_Not_Mistaken_For_Intranet_Addresses()
    {
        var hits = _detector.Scan("price 0.3 and version 10.5.1 released 2026-10-06", Policy());
        Assert.Empty(hits);
    }

    [Fact]
    public void Intranet_Addresses_Are_Detected()
    {
        var hits = _detector.Scan("server at 10.0.0.5 and nas.local, but not example.com", Policy());
        Assert.Contains(hits, h => h.Category == PrivacyCategories.Intranet);
        Assert.DoesNotContain(hits, h => h.Category == PrivacyCategories.Email);
    }

    [Fact]
    public void Hits_Carry_Masked_Samples_And_Originals_Are_Never_Recorded()
    {
        var result = _detector.Apply("mail john@example.com or admin@corp.example.org", Policy());

        var hit = result.Hits.Single(h => h.Category == PrivacyCategories.Email);
        Assert.Equal(2, hit.Count);
        Assert.Equal(2, hit.Samples.Count); // distinct values → distinct masks
        Assert.All(hit.Samples, s => Assert.Contains("••••", s));
        Assert.All(hit.Samples, s => Assert.DoesNotContain("@", s[..4]));
        Assert.DoesNotContain("john@example.com", hit.Samples);

        // Recording can be turned off per policy.
        var quiet = _detector.Apply("mail john@example.com", EffectivePrivacyPolicy.Build(
            new PrivacySettings { Enabled = true, DefaultAction = PrivacyActions.Warn, RecordSamples = false }, null));
        Assert.Empty(quiet.Hits.Single(h => h.Category == PrivacyCategories.Email).Samples);
    }

    [Fact]
    public void Full_Samples_Are_Opt_In_And_Keep_The_Matched_Value()
    {
        Assert.False(new PrivacySettings().RecordFullSamples);
        Assert.False(Json.Deserialize<PrivacySettings>("""{"enabled":true}""")!.RecordFullSamples);

        var full = _detector.Apply("mail john@example.com", EffectivePrivacyPolicy.Build(
            new PrivacySettings { Enabled = true, DefaultAction = PrivacyActions.Warn, RecordFullSamples = true }, null));
        Assert.Contains("john@example.com", full.Hits.Single(h => h.Category == PrivacyCategories.Email).Samples);

        // Recording off wins over full.
        var off = _detector.Apply("mail john@example.com", EffectivePrivacyPolicy.Build(
            new PrivacySettings { Enabled = true, DefaultAction = PrivacyActions.Warn, RecordSamples = false, RecordFullSamples = true }, null));
        Assert.Empty(off.Hits.Single(h => h.Category == PrivacyCategories.Email).Samples);
    }

    [Fact]
    public void Sample_Recording_Is_Enabled_By_Default_Even_For_Old_Setting_Blobs()
    {
        // Fresh settings default to on.
        Assert.True(new PrivacySettings().RecordSamples);

        // A settings blob saved before the field existed keeps the default on when loaded.
        var legacy = Json.Deserialize<PrivacySettings>("""{"enabled":true,"dry_run":true,"default_action":"warn"}""");
        Assert.NotNull(legacy);
        Assert.True(legacy!.RecordSamples);
        var policy = EffectivePrivacyPolicy.Build(legacy, null);
        Assert.True(policy.RecordSamples);
    }

    [Fact]
    public void MaskSample_Keeps_Short_Values_Fully_Hidden()
    {
        Assert.Equal("••••••", PrivacyDetector.MaskSample("abc"));
        Assert.Equal("ab••••i", PrivacyDetector.MaskSample("abcdefghi")); // 9 chars: 2 head + mask + 1 tail
        Assert.StartsWith("----", PrivacyDetector.MaskSample("-----BEGIN RSA PRIVATE KEY-----\nMIIBOgIBAAJBAK\n-----END RSA PRIVATE KEY-----"));
    }

    [Fact]
    public void Detects_Cards_National_Ids_And_Credentials()
    {
        const string text = """
            card=4111111111111111
            unionpay=6222600260001071234
            id=110101199003077758
            ssn=123-45-6789
            db=postgres://admin:s3cr3t@db.internal:5432/app
            gh=ghp_abcdefghijklmnopqrstuvwxyz012345
            """;

        var hits = _detector.Scan(text, Policy());
        var categories = hits.Select(h => h.Category).ToHashSet();
        Assert.Superset(new HashSet<string>
        {
            PrivacyCategories.CreditCard, PrivacyCategories.NationalId, PrivacyCategories.Credential, PrivacyCategories.ApiKey,
        }, categories);

        // The secret stays intact when the category is off.
        var off = Policy(categoryActions: new Dictionary<string, string>
        {
            [PrivacyCategories.CreditCard] = PrivacyActions.Off,
            [PrivacyCategories.NationalId] = PrivacyActions.Off,
            [PrivacyCategories.Credential] = PrivacyActions.Off,
        });
        Assert.Contains("4111111111111111", _detector.Apply(text, off).Text);
    }

    [Fact]
    public void Credential_Assignment_Ignores_Already_Redacted_Values()
    {
        // jwt redacts first; the leftover "token=[REDACTED:jwt#1]" must not be redacted again.
        var result = _detector.Apply("token=eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c9S-flKxwRJSMeKKF2QT4", Policy());

        Assert.Contains(result.Hits, h => h.Category == PrivacyCategories.Jwt);
        Assert.DoesNotContain(result.Hits, h => h.Category == PrivacyCategories.Credential);
        Assert.DoesNotContain("[REDACTED:credential", result.Text);
    }

    [Fact]
    public void Ordinary_Numbers_And_Version_Strings_Are_Not_Cards_Or_Ids()
    {
        var hits = _detector.Scan("order 12345678901234567890 paid, version 3.47.13, cost 4111", Policy());
        Assert.DoesNotContain(hits, h => h.Category == PrivacyCategories.CreditCard);
        Assert.DoesNotContain(hits, h => h.Category == PrivacyCategories.NationalId);
    }
}

public class ChunkRestorerTests
{
    private static PrivacyRedaction Redaction(string placeholder, string original) => new()
    {
        Placeholder = placeholder,
        Original = original,
        Category = PrivacyCategories.Email,
        RuleId = "builtin.email",
    };

    [Fact]
    public void Empty_Map_Is_A_PassThrough()
    {
        var restorer = new ChunkRestorer(Array.Empty<PrivacyRedaction>());
        Assert.Equal("hello ", restorer.Push("hello "));
        Assert.Equal("world", restorer.Push("world"));
        Assert.Equal("", restorer.Flush());
    }

    [Fact]
    public void Placeholder_Split_Across_Chunks_Is_Restored()
    {
        var restorer = new ChunkRestorer([Redaction(PrivacyDetector.PlaceholderFor(PrivacyCategories.Email, 1), "alice@example.com")]);

        var output = restorer.Push("[REDA") + restorer.Push("CTED:email") + restorer.Push("#1]") + restorer.Flush();
        Assert.Equal("alice@example.com", output);
    }

    [Fact]
    public void Multiple_Placeholders_With_Surrounding_Text()
    {
        var p1 = PrivacyDetector.PlaceholderFor(PrivacyCategories.Email, 1);
        var p2 = PrivacyDetector.PlaceholderFor(PrivacyCategories.Email, 2);
        var restorer = new ChunkRestorer([Redaction(p1, "a@b.com"), Redaction(p2, "c@d.org")]);

        var output = restorer.Push($"hi {p1},") + restorer.Push($"bye {p2}!") + restorer.Flush();
        Assert.Equal("hi a@b.com,bye c@d.org!", output);
    }

    [Fact]
    public void Unknown_Tail_Text_Passes_Through_Intact()
    {
        var restorer = new ChunkRestorer([Redaction("[REDACTED:email#1]", "a@b.com")]);

        var output = restorer.Push("hello [REDACT") + restorer.Flush();
        Assert.Equal("hello [REDACT", output);
    }

    [Fact]
    public void Complete_Round_Trip_Through_Detector()
    {
        const string original = "contact alice@example.com today";
        var detector = new PrivacyDetector();
        var applied = detector.Apply(original, EffectivePrivacyPolicy.Build(new PrivacySettings
        {
            Enabled = true,
            DefaultAction = PrivacyActions.Redact,
        }, null));

        var restorer = new ChunkRestorer(applied.Redactions);
        var restored = restorer.Push(applied.Text[..5]) + restorer.Push(applied.Text[5..]) + restorer.Flush();
        Assert.Equal(original, restored);
    }
}
