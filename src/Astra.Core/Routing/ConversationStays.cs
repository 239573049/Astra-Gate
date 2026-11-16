namespace Astra.Core.Routing;

/// <summary>How long a conversation stays with the account (or group member) that answered it (a group's <c>stays</c>).</summary>
public static class Stays
{
    /// <summary>While the vendor's prompt cache of the conversation is worth keeping (<see cref="ConversationStays.Holds"/>).</summary>
    public const string Auto = "auto";

    /// <summary>For the whole conversation (until the binding idles out).</summary>
    public const string Session = "session";

    /// <summary>Within one turn of the conversation; the next turn is placed afresh.</summary>
    public const string Turn = "turn";

    /// <summary>Never: every request is placed afresh.</summary>
    public const string Off = "off";

    /// <summary>For the whole conversation, even when the account is spent or resting: its errors go to the agent.</summary>
    public const string Strict = "strict";

    public static readonly IReadOnlyList<string> All = [Auto, Session, Turn, Off, Strict];
}

/// <summary>What was last seen of a conversation on the account or member that answered it.</summary>
/// <param name="LastUsed">When it last answered.</param>
/// <param name="CachedTokens">What its last answer read from and wrote to the vendor's prompt cache; null before any answer reported.</param>
/// <param name="TurnId">The turn the last request belonged to.</param>
public sealed record StayEntry(DateTimeOffset LastUsed, long? CachedTokens, string? TurnId);

/// <summary>
/// Cache-aware affinity (magpie's "stays", MIT): a conversation is kept on the account that served it while the provider's prompt
/// cache is still worth keeping — it answered in the last <see cref="CacheTtl"/> and its last request read from the cache and
/// wrote to it at least <see cref="MinCachedTokens"/> tokens (a first answer reports nothing yet, so the second request stays to
/// read what the first wrote) — and otherwise lets the conversation move to a better account.
/// </summary>
public static class ConversationStays
{
    /// <summary>How long a vendor keeps a prompt cached (Anthropic's default; OpenAI's automatic prefix cache is similar).</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>A cached prefix smaller than this is not worth holding an account for (the vendors' own minimum is 1,024 tokens).</summary>
    public const long MinCachedTokens = 1024;

    /// <summary>A binding nobody used for this long is forgotten whatever the setting says.</summary>
    public static readonly TimeSpan SessionTtl = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether the conversation stays with its candidate. <paramref name="spent"/> = the candidate is out of the running
    /// (resting or at its usage cap): only <see cref="Stays.Strict"/> keeps a conversation there.
    /// </summary>
    public static bool Holds(string? stays, StayEntry entry, DateTimeOffset now, string? currentTurn, bool spent)
    {
        stays ??= Stays.Auto;
        if (stays == Stays.Strict) return now - entry.LastUsed <= SessionTtl;
        if (stays == Stays.Off || spent) return false;
        if (now - entry.LastUsed > SessionTtl) return false;
        var sameTurn = currentTurn is not null && entry.TurnId == currentTurn;
        switch (stays)
        {
            case Stays.Session:
                return true;
            case Stays.Turn:
                return sameTurn;
            default:
                // Within a turn the cache is always worth keeping; across turns only while it is still warm and holds enough.
                if (sameTurn) return true;
                return now - entry.LastUsed <= CacheTtl && (entry.CachedTokens is null || entry.CachedTokens >= MinCachedTokens);
        }
    }
}
