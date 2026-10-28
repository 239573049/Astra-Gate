using System.Text.Json.Nodes;
using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Settings;

/// <summary>
/// The outbound-proxy form of Settings › General (web <c>ProxySettings</c>). System / direct apply at once; custom needs a URL first, so its
/// fields save together with an explicit button. The mode shown is a local choice until a custom proxy has an address.
/// </summary>
public sealed class ProxyForm
{
    /// <summary>The three modes, in display order.</summary>
    public static readonly string[] Modes = ["system", "custom", "direct"];

    /// <summary>The mode shown (may be custom although nothing is saved yet).</summary>
    public string Mode { get; set; }

    /// <summary>Address field.</summary>
    public string Url { get; set; }

    /// <summary>User name field.</summary>
    public string Username { get; set; }

    /// <summary>Password field; blank keeps the stored one.</summary>
    public string Password { get; set; } = "";

    /// <summary>Bypass list field.</summary>
    public string Bypass { get; set; }

    /// <summary>Starts from what the server stores.</summary>
    public ProxyForm(SettingsDto s)
    {
        Mode = s.ProxyMode;
        Url = s.ProxyUrl ?? "";
        Username = s.ProxyUsername ?? "";
        Bypass = s.ProxyBypass ?? "";
    }

    /// <summary>Re-reads the fields the server owns (web: an effect on the stored values), keeping what is typed when nothing changed there.</summary>
    public void Sync(SettingsDto previous, SettingsDto s)
    {
        if (previous.ProxyMode != s.ProxyMode) Mode = s.ProxyMode;
        if (previous.ProxyUrl != s.ProxyUrl || previous.ProxyUsername != s.ProxyUsername || previous.ProxyBypass != s.ProxyBypass
            || previous.HasProxyPassword != s.HasProxyPassword)
        {
            Url = s.ProxyUrl ?? "";
            Username = s.ProxyUsername ?? "";
            Password = "";
            Bypass = s.ProxyBypass ?? "";
        }
    }

    /// <summary>
    /// The body for switching the mode, or null when only the local choice changes (custom without a saved address waits for the Save button).
    /// </summary>
    public JsonObject? ChangeMode(string mode, SettingsDto saved)
    {
        Mode = mode;
        return mode != "custom" || !string.IsNullOrEmpty(saved.ProxyUrl) ? JsonPatch.Of(("proxyMode", mode)) : null;
    }

    /// <summary>The Save button's body: custom with the form's values; the password only when typed (clearing the username clears it too).</summary>
    public JsonObject SaveBody()
    {
        var o = JsonPatch.Of(
            ("proxyMode", "custom"),
            ("proxyUrl", Trimmed(Url)),
            ("proxyUsername", Trimmed(Username)),
            ("proxyBypass", Trimmed(Bypass)));
        if (Password != "") o["proxyPassword"] = Password;
        return o;
    }

    /// <summary>Whether anything differs from what is stored.</summary>
    public bool Dirty(SettingsDto s)
        => Mode != s.ProxyMode || Url != (s.ProxyUrl ?? "") || Username != (s.ProxyUsername ?? "") || Password != "" || Bypass != (s.ProxyBypass ?? "");

    /// <summary>Whether Save is enabled.</summary>
    public bool CanSave(SettingsDto s, bool saving) => Dirty(s) && Url.Trim() != "" && !saving;

    private static string? Trimmed(string text) => text.Trim() is { Length: > 0 } t ? t : null;
}

/// <summary>The plugin-market address of Settings › General (web <c>PluginMarketSettings</c>): the site's registry, an own address, or local copies only.</summary>
public sealed class MarketForm
{
    /// <summary>Address field.</summary>
    public string Url { get; set; }

    /// <summary>Starts from the stored value.</summary>
    public MarketForm(SettingsDto s) => Url = s.PluginMarketUrl ?? "";

    /// <summary>Re-reads when the stored address changed.</summary>
    public void Sync(SettingsDto previous, SettingsDto s)
    {
        if (previous.PluginMarketUrl != s.PluginMarketUrl) Url = s.PluginMarketUrl ?? "";
    }

    /// <summary>The market is kept to the local copies (an empty address is stored).</summary>
    public static bool Offline(SettingsDto s) => s.PluginMarketUrl == "";

    /// <summary>The switch's body: on stores "" (local copies only), off resets to the site's registry (null).</summary>
    public static JsonObject OfflineBody(bool on) => JsonPatch.Of(("pluginMarketUrl", on ? "" : null));

    /// <summary>The Save button's body: the address, or null (reset) when empty.</summary>
    public JsonObject SaveBody() => JsonPatch.Of(("pluginMarketUrl", Url.Trim() is { Length: > 0 } u ? u : null));

    /// <summary>The reset button's body.</summary>
    public static JsonObject ResetBody() => JsonPatch.Of(("pluginMarketUrl", null));

    /// <summary>Whether Save is enabled.</summary>
    public bool CanSave(SettingsDto s, bool saving) => !saving && Url.Trim() != (s.PluginMarketUrl ?? "");

    /// <summary>Whether the reset button shows (an own address is stored).</summary>
    public static bool CanReset(SettingsDto s) => s.PluginMarketUrl is not null;
}
