using System.Text.RegularExpressions;
using Astra.Desktop.Core.I18n;

namespace Astra.Desktop.Tests.I18n;

// Loc.Language is process-wide state; keep these tests from racing each other.
[Collection("Loc")]
public sealed partial class LocTests : IDisposable
{
    private readonly string _saved = Loc.Language;

    public void Dispose() => Loc.Language = _saved;

    private static string Raw(string lang, string key)
    {
        Loc.Language = lang;
        return Loc.T(key);
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();

    private static string[] Placeholders(string s) =>
        Placeholder().Matches(s).Select(m => m.Groups[1].Value).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    [Fact]
    public void Catalogs_have_the_same_keys_and_no_empty_strings()
    {
        var keys = Loc.Keys.ToList();
        Assert.NotEmpty(keys);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        foreach (var k in keys)
        {
            var zh = Raw("zh", k);
            var en = Raw("en", k);
            Assert.False(string.IsNullOrWhiteSpace(zh), k);
            Assert.False(string.IsNullOrWhiteSpace(en), k);
        }
    }

    [Fact]
    public void Placeholders_match_between_languages()
    {
        foreach (var k in Loc.Keys)
            Assert.Equal(Placeholders(Raw("zh", k)!), Placeholders(Raw("en", k)!));
    }

    [Fact]
    public void Substitutes_like_the_web()
    {
        Loc.Language = "en";
        Assert.Equal("Page 2 of 5", Loc.T("requests.page", ("page", 2), ("pages", 5)));
        Loc.Language = "zh";
        Assert.Equal("第 2 / 5 页", Loc.T("requests.page", ("page", 2), ("pages", 5)));
    }

    [Fact]
    public void Unknown_placeholders_stay_and_missing_args_are_ignored()
    {
        Loc.Language = "en";
        Assert.Equal("Page 2 of {pages}", Loc.T("requests.page", ("page", 2)));
        Assert.Equal("Page {page} of {pages}", Loc.T("requests.page"));
        Assert.Equal("Page 2 of 5", Loc.T("requests.page", ("page", 2), ("pages", 5), ("other", 1)));
    }

    [Fact]
    public void Numbers_use_the_invariant_culture()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Loc.Language = "en";
            Assert.Equal("Page 1.5 of 5", Loc.T("requests.page", ("page", 1.5), ("pages", 5)));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void Missing_key_returns_the_key_and_has_is_false()
    {
        Loc.Language = "en";
        Assert.Equal("no.such.key", Loc.T("no.such.key"));
        Assert.Equal("no.such.key", Loc.T("no.such.key", ("a", 1)));
        Assert.False(Loc.Has("no.such.key"));
        Assert.True(Loc.Has("requests.page"));
    }

    [Fact]
    public void Language_normalises_and_raises_the_event_only_on_change()
    {
        Loc.Language = "en";
        var n = 0;
        void Handler() => n++;
        Loc.LanguageChanged += Handler;
        try
        {
            Loc.Language = "en";
            Assert.Equal(0, n);
            Loc.Language = "zh-CN";
            Assert.Equal("zh", Loc.Language);
            Assert.Equal(1, n);
            Assert.Equal("第 2 / 5 页", Loc.T("requests.page", ("page", 2), ("pages", 5)));
            Loc.Language = "fr";
            Assert.Equal("en", Loc.Language);
            Assert.Equal(2, n);
        }
        finally { Loc.LanguageChanged -= Handler; }
    }

    [Fact]
    public void Keys_follow_zh_order_and_match_Has()
    {
        Assert.All(Loc.Keys.Take(50), k => Assert.True(Loc.Has(k)));
    }
}
