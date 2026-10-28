using System.Globalization;
using System.Text;

namespace Astra.Desktop.Core.I18n;

/// <summary>
/// UI strings for the desktop client. The catalogs are generated from web/src/i18n/messages.ts
/// (<c>scripts/gen-desktop-i18n.mjs</c> -> <c>Messages.g.cs</c>); this mirrors the web resolver
/// (<c>translate</c> / <c>format</c> in web/src/i18n/index.tsx):
/// current language's string, else the zh string, else the key itself; <c>{name}</c> placeholders
/// (<c>\w+</c>, ASCII) are replaced and an unknown one is left as written.
/// Differences from the web: a null argument value renders as an empty string (JS would print
/// "null"); numbers and other <see cref="IFormattable"/> values use the invariant culture (as JS's
/// <c>String(n)</c> does, up to number formatting details); the web persists its choice in
/// localStorage, here persistence belongs to the caller (set <see cref="Language"/> at startup).
/// </summary>
public static class Loc
{
    private static volatile string _language = FromCulture(CultureInfo.CurrentUICulture);

    /// <summary>"zh" or "en". Defaults to the OS UI culture (zh* -> zh, else en). Any other value is
    /// normalised the same way (a "zh-CN" becomes "zh"); setting a different language raises <see cref="LanguageChanged"/>.</summary>
    public static string Language
    {
        get => _language;
        set
        {
            var next = FromCulture(value);
            if (next == _language) return;
            _language = next;
            LanguageChanged?.Invoke();
        }
    }

    /// <summary>Raised after <see cref="Language"/> changed, on the thread that set it.</summary>
    public static event Action? LanguageChanged;

    /// <summary>The text for <paramref name="key"/> in the current language.</summary>
    public static string T(string key) => Lookup(key);

    /// <summary>As <see cref="T(string)"/>, replacing <c>{Name}</c> placeholders with the values.</summary>
    public static string T(string key, params (string Name, object? Value)[] args)
    {
        var template = Lookup(key);
        return args.Length == 0 ? template : Format(template, args);
    }

    /// <summary>Whether the catalog defines <paramref name="key"/>.</summary>
    public static bool Has(string key) => Messages.Zh(key) is not null;

    /// <summary>All keys, in the order of the web zh catalog.</summary>
    public static IEnumerable<string> Keys => Messages.Keys;

    private static string Lookup(string key)
    {
        var s = _language == "zh" ? Messages.Zh(key) : Messages.En(key) ?? Messages.Zh(key);
        return s ?? key;
    }

    private static string FromCulture(CultureInfo c) => FromCulture(c.Name);

    private static string FromCulture(string? name) =>
        name is not null && name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en";

    private static bool IsWord(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_';

    private static string Format(string template, (string Name, object? Value)[] args)
    {
        if (template.IndexOf('{') < 0) return template;
        var sb = new StringBuilder(template.Length + 16);
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c == '{')
            {
                var j = i + 1;
                while (j < template.Length && IsWord(template[j])) j++;
                if (j > i + 1 && j < template.Length && template[j] == '}')
                {
                    var name = template.AsSpan(i + 1, j - i - 1);
                    var found = false;
                    foreach (var (n, v) in args)
                    {
                        if (!name.SequenceEqual(n)) continue;
                        sb.Append(v switch
                        {
                            null => "",
                            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                            _ => v.ToString(),
                        });
                        found = true;
                        break;
                    }
                    if (!found) sb.Append(template, i, j - i + 1);
                    i = j + 1;
                    continue;
                }
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
