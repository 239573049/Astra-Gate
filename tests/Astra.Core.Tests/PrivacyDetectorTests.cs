using Astra.Core.Privacy;

namespace Astra.Core.Tests;

public class PrivacyDetectorTests
{
    private readonly PrivacyDetector _detector = new();

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
