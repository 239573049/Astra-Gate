using Astra.Core.Privacy;

namespace Astra.Core.Tests;

/// <summary>Stable placeholders, "once masked, always masked", checksums and the finer categories (magpie parity).</summary>
public class PrivacyRulesTests
{
    private static EffectivePrivacyPolicy Policy(Action<PrivacySettings>? change = null)
    {
        var settings = new PrivacySettings { Enabled = true, DryRun = false, DefaultAction = PrivacyActions.Redact };
        change?.Invoke(settings);
        return EffectivePrivacyPolicy.Build(settings, null);
    }

    private static EffectivePrivacyPolicy With(params string[] categories) => Policy(s =>
    {
        s.DefaultAction = PrivacyActions.Off;
        foreach (var c in categories) s.CategoryActions[c] = PrivacyActions.Redact;
    });

    private static List<string> Found(string text, EffectivePrivacyPolicy policy) =>
        new PrivacyDetector().Apply(text, policy).Redactions.Select(r => r.Original).ToList();

    // --- stable placeholders -------------------------------------------------------------------------------------------

    [Fact]
    public void The_Same_Value_Gets_The_Same_Placeholder_In_Every_Request()
    {
        var detector = new PrivacyDetector();
        var policy = Policy();
        var first = detector.Apply("write to alice@corp.io about 13812345678", policy);
        var second = detector.Apply("(another request) 13812345678 and alice@corp.io, plus bob@corp.io", policy);

        var email = first.Redactions.Single(r => r.Original == "alice@corp.io").Placeholder;
        var phone = first.Redactions.Single(r => r.Original == "13812345678").Placeholder;
        Assert.Matches(@"^\{\{EMAIL_[a-z2-7]{8}\}\}$", email);
        Assert.Matches(@"^\{\{PHONE_[a-z2-7]{8}\}\}$", phone);
        Assert.Equal(email, second.Redactions.Single(r => r.Original == "alice@corp.io").Placeholder);
        Assert.Equal(phone, second.Redactions.Single(r => r.Original == "13812345678").Placeholder);
        Assert.Contains(email, second.Text);
        Assert.DoesNotContain("alice@corp.io", second.Text);
    }

    [Fact]
    public void Placeholders_Depend_On_The_Key_And_Reveal_Nothing_Of_The_Value()
    {
        var key = PlaceholderMint.NewKey();
        var a = new PlaceholderMint(key).Placeholder("email", "alice@corp.io");
        Assert.Equal(a, new PlaceholderMint(key).Placeholder("email", "alice@corp.io")); // across restarts
        Assert.NotEqual(a, new PlaceholderMint(PlaceholderMint.NewKey()).Placeholder("email", "alice@corp.io"));
        Assert.NotEqual(a, new PlaceholderMint(key).Placeholder("email", "bob@corp.io"));
        Assert.NotEqual(a, new PlaceholderMint(key).Placeholder("phone", "alice@corp.io"));
        Assert.DoesNotContain("alice", a);
    }

    [Fact]
    public void An_Old_Format_Placeholder_In_History_Is_Left_Alone()
    {
        var text = "earlier the model wrote [REDACTED:email#1] and token=[REDACTED:jwt#2] here";
        var result = new PrivacyDetector().Apply(text, Policy());
        Assert.Empty(result.Redactions);
        Assert.Equal(text, result.Text);
    }

    // --- once masked, always masked ------------------------------------------------------------------------------------

    [Fact]
    public void A_Value_Masked_Once_Is_Masked_Wherever_It_Appears_Later()
    {
        var detector = new PrivacyDetector();
        var policy = Policy(s => s.CustomRules.Add(new PrivacyCustomRule { Id = "custom.proj", Name = "project", Pattern = "Project-[A-Z]{4}" }));
        var first = detector.Apply("ship Project-ABCD tomorrow", policy);
        Assert.DoesNotContain("Project-ABCD", first.Text);
        detector.Mint.Remember(first.Redactions);

        // Later the same value shows up where no rule would find it — the rule is gone, say, or it is glued into a sentence.
        var later = detector.Apply("what is the status of Project-ABCD?", Policy());
        Assert.DoesNotContain("Project-ABCD", later.Text);
        Assert.Contains(first.Redactions[0].Placeholder, later.Text);
        Assert.Equal(first.Redactions[0].Placeholder, later.Redactions.Single().Placeholder);
    }

    [Fact]
    public void A_Remembered_Value_Is_Only_Matched_As_A_Whole_Word_And_Never_In_Dry_Run()
    {
        var detector = new PrivacyDetector();
        detector.Mint.Remember([new PrivacyRedaction { Placeholder = "{{X}}", Original = "secret-code-9", Category = "custom", RuleId = "custom.x" }]);
        var policy = Policy(s => s.CustomRules.Add(new PrivacyCustomRule { Id = "custom.x", Name = "x", Pattern = "zzzz" }));
        Assert.Equal("a secret-code-90 b", detector.Apply("a secret-code-90 b", policy).Text); // inside a longer word
        Assert.DoesNotContain("secret-code-9", detector.Apply("a secret-code-9, b", policy).Text);
        // Off for its rule: not masked.
        var off = Policy(s => s.CategoryActions["custom.x"] = PrivacyActions.Off);
        Assert.Equal("a secret-code-9 b", detector.Apply("a secret-code-9 b", off).Text);
    }

    [Fact]
    public void Memory_Is_Bounded_And_Short_Values_Are_Not_Kept()
    {
        var mint = new PlaceholderMint();
        mint.Remember([new PrivacyRedaction { Original = "ab", Category = "custom", RuleId = "r" }]);
        Assert.Equal(0, mint.KnownCount);
        for (var i = 0; i < PlaceholderMint.MaxKnown + 50; i++)
            mint.Remember([new PrivacyRedaction { Original = $"value-{i:D6}", Category = "custom", RuleId = "r" }]);
        Assert.Equal(PlaceholderMint.MaxKnown, mint.KnownCount);
        Assert.False(mint.ContainsKnown("value-000001")); // the oldest went first
        Assert.True(mint.ContainsKnown("value-004145"));
    }

    // --- checksums -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("card 4111111111111111 ok", true)] // Luhn-valid test Visa
    [InlineData("card 4111111111111112 ok", false)] // one digit off
    [InlineData("card 6222021234567890128 ok", true)] // UnionPay, Luhn-valid
    [InlineData("card 6222021234567890129 ok", false)]
    public void Cards_Need_A_Valid_Luhn_Checksum(string text, bool masked) =>
        Assert.Equal(masked, Found(text, With(PrivacyCategories.CreditCard)).Count == 1);

    [Theory]
    [InlineData("id 11010519491231002X ok", true)] // valid check digit X
    [InlineData("id 110105194912310021 ok", false)] // wrong check digit
    [InlineData("id 110105194913310025 ok", false)] // month 13
    public void Chinese_Resident_Ids_Need_The_Check_Digit(string text, bool masked) =>
        Assert.Equal(masked, Found(text, With(PrivacyCategories.NationalId)).Count == 1);

    [Theory]
    [InlineData("pay to DE89370400440532013000 now", true)]
    [InlineData("pay to GB82 WEST 1234 5698 7654 32 now", true)]
    [InlineData("pay to DE89370400440532013001 now", false)] // mod 97 fails
    [InlineData("pay to XX89370400440532013000 now", false)] // unknown country
    public void Ibans_Need_Their_Country_Length_And_Mod_97(string text, bool masked) =>
        Assert.Equal(masked, Found(text, With(PrivacyCategories.Iban)).Count == 1);

    [Fact]
    public void An_Iban_Followed_By_A_Capital_Word_Keeps_The_Word()
    {
        var found = Found("send BE68 5390 0754 7034 THEN wait", With(PrivacyCategories.Iban));
        Assert.Equal("BE68 5390 0754 7034", Assert.Single(found));
    }

    [Theory]
    [InlineData("123-45-6789", false)] // printed in examples
    [InlineData("078-05-1120", false)]
    [InlineData("219-09-9999", false)]
    [InlineData("536-22-8913", true)]
    public void Ssn_Examples_Are_Not_Masked(string ssn, bool masked) =>
        Assert.Equal(masked, Found($"ssn {ssn} here", With(PrivacyCategories.NationalId)).Count == 1);

    // --- example domains -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("a@example.com")]
    [InlineData("a@mail.example.org")]
    [InlineData("a@example.net")]
    [InlineData("a@host.test")]
    [InlineData("a@host.invalid")]
    [InlineData("a@host.example")]
    [InlineData("a@foo.localhost")]
    public void Emails_Under_Example_Domains_Are_Never_Masked(string email)
    {
        var text = $"mail {email} please";
        var result = new PrivacyDetector().Apply(text, Policy());
        Assert.Equal(text, result.Text);
        Assert.Empty(result.Redactions);
    }

    [Fact]
    public void Look_Alike_Domains_Are_Still_Masked() =>
        Assert.Contains("a@notexample.com", Found("mail a@notexample.com and a@example.com.evil.io", With(PrivacyCategories.Email)));

    // --- finer categories ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Passport No.: E12345678", "E12345678")]
    [InlineData("my passport is G1234567 ok", "G1234567")]
    [InlineData("护照号码：EA1234567", "EA1234567")]
    public void Passport_Numbers_Are_Masked_After_Their_Label(string text, string value) =>
        Assert.Equal(value, Assert.Single(Found(text, With(PrivacyCategories.Passport))));

    [Theory]
    [InlineData("a passport office")]
    [InlineData("passport: ABCDEFGH")] // no digits
    [InlineData("E12345678 alone")]
    public void Passport_Needs_The_Label_And_Digits(string text) => Assert.Empty(Found(text, With(PrivacyCategories.Passport)));

    [Theory]
    [InlineData("device 00:1B:44:11:3A:B7 up", "00:1B:44:11:3A:B7")]
    [InlineData("device 00-1b-44-11-3a-b7 up", "00-1b-44-11-3a-b7")]
    public void Mac_Addresses_Are_Masked(string text, string value) =>
        Assert.Equal(value, Assert.Single(Found(text, With(PrivacyCategories.Mac))));

    [Theory]
    [InlineData("ff:ff:ff:ff:ff:ff")] // broadcast
    [InlineData("aa:bb:cc:dd:ee:ff")] // documentation
    [InlineData("12:34:56:78:90:12")] // only decimal digits
    public void Mac_Placeholders_In_Docs_Are_Not_Masked(string mac) => Assert.Empty(Found($"mac {mac}", With(PrivacyCategories.Mac)));

    [Fact]
    public void Public_Ips_Are_Masked_Only_When_Switched_On_And_Never_Private_Ranges()
    {
        const string text = "hosts 8.134.12.7 10.0.0.5 192.168.1.1 127.0.0.1 100.64.1.1 203.0.113.9 and version 2.31.0.1, Chrome/120.0.6099.109, 1.1.1.1";
        // default: off
        Assert.DoesNotContain("8.134.12.7", Found(text, Policy()));
        Assert.Equal(["8.134.12.7"], Found(text, With(PrivacyCategories.Ip)));
    }

    [Fact]
    public void Public_Ipv6_Is_Global_Unicast_Only()
    {
        var found = Found("a 2a00:1450:4001:81b::200e b fe80::1 c ::1 d 2001:db8::1", With(PrivacyCategories.Ip));
        Assert.Equal(["2a00:1450:4001:81b::200e"], found);
    }

    [Theory]
    [InlineData("生日：1990-05-17", "1990-05-17")]
    [InlineData("DOB: 05/17/1990", "05/17/1990")]
    [InlineData("born on March 5, 1990", "March 5, 1990")]
    [InlineData("出生日期 1988年3月2日", "1988年3月2日")]
    public void Birthdays_Are_Masked_After_Their_Label(string text, string value) =>
        Assert.Equal(value, Assert.Single(Found(text, With(PrivacyCategories.Birthday))));

    [Theory]
    [InlineData("deadline 1990-05-17")] // no label
    [InlineData("生日：1990-13-45")] // not a date
    [InlineData("DOB: 2999-01-01")] // future
    public void A_Date_Is_Not_A_Birthday_Without_Its_Label_And_A_Real_Past_Date(string text) =>
        Assert.Empty(Found(text, With(PrivacyCategories.Birthday)));

    [Theory]
    [InlineData("S/N: C02XK1AAJGH5", "C02XK1AAJGH5")]
    [InlineData("Serial Number: ABC-12345-XYZ", "ABC-12345-XYZ")]
    [InlineData("序列号：SN20231100123", "SN20231100123")]
    public void Serial_Numbers_Are_Masked_After_Their_Label(string text, string value) =>
        Assert.Equal(value, Assert.Single(Found(text, With(PrivacyCategories.Serial))));

    [Fact]
    public void Serial_Port_And_Short_Codes_Are_Not_Serial_Numbers() =>
        Assert.Empty(Found("open the serial port, Serial.println(x), sn: abc", With(PrivacyCategories.Serial)));

    [Fact]
    public void Home_Folder_User_Names_Are_Masked_Only_When_Switched_On_And_The_Path_Stays()
    {
        const string text = "see /Users/alice/code/app and C:\\Users\\bob\\proj and /home/shared/x and /home/carol.";
        Assert.DoesNotContain("alice", Found(text, Policy()));
        var policy = With(PrivacyCategories.HomeUser);
        Assert.Equal(["alice", "bob", "carol"], Found(text, policy));
        var masked = new PrivacyDetector().Apply(text, policy).Text;
        Assert.Contains("/code/app", masked);
        Assert.Contains("/home/shared/x", masked);
    }

    [Fact]
    public void The_New_Categories_Are_Built_In_And_The_Noisy_Ones_Are_Off_By_Default()
    {
        var categories = PrivacyDetector.BuiltInRules.Select(r => r.Category).Distinct().ToList();
        foreach (var c in new[] { "passport", "iban", "mac", "ip", "birthday", "serial", "home_user" })
            Assert.Contains(c, categories);
        Assert.Equal(PrivacyCategories.BuiltIn.OrderBy(c => c), categories.OrderBy(c => c));
        var policy = Policy();
        Assert.Equal(PrivacyActions.Off, policy.ActionFor("builtin.public-ipv4", PrivacyCategories.Ip));
        Assert.Equal(PrivacyActions.Redact, policy.ActionFor("builtin.iban", PrivacyCategories.Iban));
        // a client default does not switch them on either
        var clientPolicy = EffectivePrivacyPolicy.Build(new PrivacySettings { Enabled = true, ClientDefaults = { ["x"] = PrivacyActions.Redact } }, "x");
        Assert.Equal(PrivacyActions.Off, clientPolicy.ActionFor("builtin.home-user", PrivacyCategories.HomeUser));
        // an explicit per-rule action does
        var byRule = Policy(s => s.CategoryActions["builtin.public-ipv4"] = PrivacyActions.Redact);
        Assert.Equal(PrivacyActions.Redact, byRule.ActionFor("builtin.public-ipv4", PrivacyCategories.Ip));
    }
}
