using Astra.Desktop.Core.I18n;

namespace Astra.Desktop.Core.Settings;

/// <summary>
/// A translated sentence kept as its catalog key plus arguments, so logic can decide WHICH message a page shows (and tests can assert it)
/// without formatting text, and the view turns it into text in the current language when it draws (<see cref="Text"/>).
/// </summary>
/// <param name="Key">The i18n key (same keys as web/src/i18n/messages.ts).</param>
/// <param name="Args">The <c>{name}</c> placeholder values.</param>
public readonly record struct Msg(string Key, (string Name, object? Value)[]? Args = null)
{
    /// <summary>A message with placeholders.</summary>
    public static Msg Of(string key, params (string Name, object? Value)[] args) => new(key, args.Length == 0 ? null : args);

    /// <summary>The message in the current UI language.</summary>
    public string Text => Args is null ? Loc.T(Key) : Loc.T(Key, Args);

    /// <summary>The text of an optional message.</summary>
    public static string? TextOf(Msg? msg) => msg?.Text;

    /// <inheritdoc />
    public override string ToString() => Key;
}
