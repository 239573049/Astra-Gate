using Astra.Core.Routing;

namespace Astra.Core.Tests;

/// <summary>What smart-routing settings must look like to be saved, and what switching them on needs on top (plan §5.7).</summary>
public class SmartRoutingValidatorTests
{
    private static SmartRoutingSettings Settings(Action<SmartRoutingSettings>? change = null)
    {
        var s = new SmartRoutingSettings();
        change?.Invoke(s);
        return s;
    }

    private static string[] Codes(IEnumerable<RoutingProblem> problems) => problems.Select(p => p.Code).ToArray();

    [Fact]
    public void The_Defaults_Are_A_Valid_Shape_That_Cannot_Yet_Be_Switched_On()
    {
        var s = Settings();

        Assert.Empty(SmartRoutingValidator.Check(s));
        Assert.False(s.Enabled);
        Assert.Equal(["light", "standard", "heavy"], s.Grades.Select(g => g.Id));
        Assert.Equal(RoutingModes.Session, s.Rules.Mode);
        Assert.Equal("standard", s.Rules.FallbackGrade);
        // No key and no grade has a model yet.
        var missing = SmartRoutingValidator.CheckComplete(s);
        Assert.Contains(SmartRoutingValidator.Codes.JudgeKeyMissing, Codes(missing));
        Assert.Equal(3, missing.Count(p => p.Code == SmartRoutingValidator.Codes.GradeModelMissing));
    }

    [Fact]
    public void A_Complete_Setup_Passes_Both_Checks()
    {
        var s = Settings(x =>
        {
            x.Judge.ApiKeyProtected = "protected";
            foreach (var grade in x.Grades) grade.Model = "some-model";
        });

        Assert.Empty(SmartRoutingValidator.Check(s));
        Assert.Empty(SmartRoutingValidator.CheckComplete(s));
    }

    [Fact]
    public void The_Settings_Round_Trip_Through_Storage_Without_Losing_Anything()
    {
        var s = Settings(x =>
        {
            x.Enabled = true;
            x.VirtualModel = "smart-auto";
            x.Judge.Protocol = JudgeProtocols.OpenAI;
            x.Judge.BaseUrl = "https://openrouter.ai/api";
            x.Judge.ApiKeyProtected = "enc";
            x.Judge.Model = "m";
            x.Judge.Instructions = "pick";
            x.Grades[1].Model = "mid";
            x.Rules.Mode = RoutingModes.Turn;
            x.Rules.MinConfidence = 0.45;
        });

        var copy = s.Clone();

        Assert.Equal(Json.Serialize(s), Json.Serialize(copy));
        Assert.True(copy.Enabled);
        Assert.Equal("smart-auto", copy.VirtualModel);
        Assert.Equal("https://openrouter.ai/api", copy.Judge.EffectiveBaseUrl);
        Assert.Equal("mid", copy.Grades[1].Model);
        Assert.Equal(0.45, copy.Rules.MinConfidence);
        // Storage is snake_case and keeps no computed property.
        var json = Json.Serialize(s);
        Assert.Contains("\"api_key_protected\":\"enc\"", json);
        Assert.Contains("\"min_confidence\":0.45", json);
        Assert.DoesNotContain("effective_base_url", json);
    }

    [Fact]
    public void A_Stored_Blob_Without_Grades_Or_Rules_Reads_Back_With_The_Defaults()
    {
        var s = Json.Deserialize<SmartRoutingSettings>("""{"enabled":false,"virtual_model":"x-auto"}""")!;

        Assert.Equal("x-auto", s.VirtualModel);
        Assert.Equal(3, s.Grades.Count);
        Assert.Equal(RoutingModes.Session, s.Rules.Mode);
        Assert.Equal("https://api.typesafe.ai", s.Judge.EffectiveBaseUrl);
    }

    [Theory]
    [InlineData("light", true)]
    [InlineData("g-2", true)]
    [InlineData("a_b", true)]
    [InlineData("", false)]
    [InlineData("Light", false)]
    [InlineData("has space", false)]
    [InlineData("档位", false)]
    [InlineData("0123456789012345678901234567890123", false)] // 34 characters
    public void A_Grade_Id_Is_Short_Lowercase_And_Plain(string id, bool valid) =>
        Assert.Equal(valid, SmartRoutingValidator.IsValidGradeId(id));

    [Theory]
    [InlineData("astra-auto", true)]
    [InlineData("Smart.Auto_1", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData("-auto", false)]
    [InlineData("has space", false)]
    [InlineData("a/b", false)] // would break Gemini's models/{id}:generateContent path
    [InlineData("a:b", false)]
    [InlineData("名字", false)]
    public void A_Virtual_Model_Id_Is_Safe_In_A_Url_Path_And_In_Client_Configs(string id, bool valid) =>
        Assert.Equal(valid, SmartRoutingValidator.IsValidVirtualModel(id));

    [Theory]
    [InlineData("https://api.typesafe.ai", true)]
    [InlineData("http://127.0.0.1:4000", true)]
    [InlineData("https://openrouter.ai/api/v1", true)]
    [InlineData("ftp://x.test", false)]
    [InlineData("api.typesafe.ai", false)]
    [InlineData("https://user:pass@x.test", false)] // credentials belong in the key
    [InlineData("https://x.test/?key=1", false)]
    [InlineData("https://x.test/#frag", false)]
    [InlineData("", false)]
    public void A_Judge_Address_Is_A_Plain_Http_Address(string url, bool valid) =>
        Assert.Equal(valid, SmartRoutingValidator.IsValidBaseUrl(url));

    [Theory]
    [InlineData("sk-or-v1-abcdef0123456789", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData("sk bad", false)]
    [InlineData("sk-bad\nkey", false)]
    [InlineData("sk-\tkey", false)]
    [InlineData("sk-密钥", false)] // a pasted non-ASCII character would break the header
    public void An_Api_Key_Is_Visible_Ascii_Only(string key, bool valid) =>
        Assert.Equal(valid, SmartRoutingValidator.IsValidApiKey(key));

    [Fact]
    public void An_Api_Key_Has_A_Length_Limit()
    {
        Assert.True(SmartRoutingValidator.IsValidApiKey(new string('k', 512)));
        Assert.False(SmartRoutingValidator.IsValidApiKey(new string('k', 513)));
    }

    [Fact]
    public void Grades_Are_Two_To_Six_With_Unique_Ids_And_A_Description_Each()
    {
        Assert.Contains(SmartRoutingValidator.Codes.GradesCount, Codes(SmartRoutingValidator.Check(Settings(s =>
        {
            s.Grades.RemoveRange(1, 2);
            s.Rules.FallbackGrade = "light";
        }))));
        Assert.Contains(SmartRoutingValidator.Codes.GradesCount, Codes(SmartRoutingValidator.Check(Settings(s =>
            s.Grades.AddRange(Enumerable.Range(0, 4).Select(i => new RoutingGrade { Id = "extra" + i, Description = "d" }))))));

        var duplicate = SmartRoutingValidator.Check(Settings(s => s.Grades[1].Id = "light"));
        Assert.Contains(SmartRoutingValidator.Codes.GradeIdDuplicate, Codes(duplicate));
        Assert.Equal("grades[1].id", duplicate.First(p => p.Code == SmartRoutingValidator.Codes.GradeIdDuplicate).Field);

        var noWords = SmartRoutingValidator.Check(Settings(s => s.Grades[2].Description = "  "));
        Assert.Equal(["grades[2].description"], noWords.Where(p => p.Code == SmartRoutingValidator.Codes.GradeDescriptionMissing).Select(p => p.Field));

        Assert.Contains(SmartRoutingValidator.Codes.GradeIdInvalid, Codes(SmartRoutingValidator.Check(Settings(s => s.Grades[0].Id = "Not Valid"))));
    }

    [Fact]
    public void The_Fallback_Grade_Must_Be_One_Of_The_Grades()
    {
        var problems = SmartRoutingValidator.Check(Settings(s => s.Rules.FallbackGrade = "nope"));
        var problem = Assert.Single(problems);
        Assert.Equal(SmartRoutingValidator.Codes.FallbackGradeUnknown, problem.Code);
        Assert.Equal("rules.fallbackGrade", problem.Field);
    }

    [Theory]
    [InlineData(199, false)]
    [InlineData(200, true)]
    [InlineData(15000, true)]
    [InlineData(15001, false)]
    public void The_Judge_Timeout_Is_Bounded(int ms, bool valid) =>
        Assert.Equal(valid, SmartRoutingValidator.Check(Settings(s => s.Judge.TimeoutMs = ms)).Count == 0);

    [Theory]
    [InlineData(-0.1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(1.01, false)]
    [InlineData(double.NaN, false)]
    public void The_Least_Confidence_Is_A_Fraction(double value, bool valid) =>
        Assert.Equal(valid, SmartRoutingValidator.Check(Settings(s => s.Rules.MinConfidence = value)).Count == 0);

    [Fact]
    public void The_Other_Rules_Are_Bounded_Too()
    {
        Assert.Contains(SmartRoutingValidator.Codes.OutOfRange, Codes(SmartRoutingValidator.Check(Settings(s => s.Rules.SessionTtlMinutes = 0))));
        Assert.Contains(SmartRoutingValidator.Codes.OutOfRange, Codes(SmartRoutingValidator.Check(Settings(s => s.Rules.SessionTtlMinutes = 1441))));
        Assert.Contains(SmartRoutingValidator.Codes.OutOfRange, Codes(SmartRoutingValidator.Check(Settings(s => s.Rules.ContextTurns = 6))));
        Assert.Contains(SmartRoutingValidator.Codes.OutOfRange, Codes(SmartRoutingValidator.Check(Settings(s => s.Rules.MaxChars = 499))));
        Assert.Contains(SmartRoutingValidator.Codes.OutOfRange, Codes(SmartRoutingValidator.Check(Settings(s => s.Rules.MaxChars = 20001))));
        Assert.Empty(SmartRoutingValidator.Check(Settings(s =>
        {
            s.Rules.SessionTtlMinutes = 1440;
            s.Rules.ContextTurns = 0;
            s.Rules.MaxChars = 500;
        })));
    }

    [Fact]
    public void Mode_And_Protocol_Must_Be_Known_Values()
    {
        Assert.Contains(SmartRoutingValidator.Codes.ModeInvalid, Codes(SmartRoutingValidator.Check(Settings(s => s.Rules.Mode = "sometimes"))));
        Assert.Contains(SmartRoutingValidator.Codes.JudgeProtocolInvalid, Codes(SmartRoutingValidator.Check(Settings(s => s.Judge.Protocol = "grpc"))));
        foreach (var mode in RoutingModes.All) Assert.Empty(SmartRoutingValidator.Check(Settings(s => s.Rules.Mode = mode)));
        foreach (var protocol in JudgeProtocols.All) Assert.Empty(SmartRoutingValidator.Check(Settings(s => s.Judge.Protocol = protocol)));
    }

    [Fact]
    public void Every_Problem_Names_The_Field_The_Console_Shows_It_On()
    {
        var problems = SmartRoutingValidator.Check(Settings(s =>
        {
            s.VirtualModel = "no good";
            s.Judge.BaseUrl = "ftp://x";
            s.Judge.Instructions = new string('x', 2001);
            s.Rules.ContextTurns = 99;
            s.Grades[0].Description = new string('d', 401);
            s.Grades[1].Name = new string('n', 41);
        }));

        Assert.All(problems, p => Assert.False(string.IsNullOrEmpty(p.Field)));
        var fields = problems.Select(p => p.Field).ToHashSet();
        Assert.Contains("virtualModel", fields);
        Assert.Contains("judge.baseUrl", fields);
        Assert.Contains("judge.instructions", fields);
        Assert.Contains("rules.contextTurns", fields);
        Assert.Contains("grades[0].description", fields);
        Assert.Contains("grades[1].name", fields);
    }

    [Fact]
    public void Every_Code_Has_A_Distinct_Value()
    {
        var codes = typeof(SmartRoutingValidator.Codes).GetFields().Select(f => (string)f.GetRawConstantValue()!).ToArray();
        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[a-z_]+$", c)); // the console keys its words by them: smartRouting.problem.<code>
    }

    [Fact]
    public void Each_Dialect_Has_Its_Own_Defaults_And_A_Url_The_Validator_Accepts()
    {
        foreach (var protocol in JudgeProtocols.All)
            Assert.True(SmartRoutingValidator.IsValidBaseUrl(JudgeProtocols.DefaultBaseUrl(protocol)), protocol);
        Assert.Equal("jev-latest", JudgeProtocols.DefaultModel(JudgeProtocols.TypeSafe));
        Assert.Equal("", JudgeProtocols.DefaultModel(JudgeProtocols.OpenAI)); // no stable default: the user chooses
        Assert.NotEqual(JudgeProtocols.DefaultBaseUrl(JudgeProtocols.TypeSafe), JudgeProtocols.DefaultBaseUrl(JudgeProtocols.OpenAI));
    }
}
