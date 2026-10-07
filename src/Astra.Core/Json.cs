using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Astra.Core;

public static class Json
{
    /// <summary>Options for persisted documents (pricing, overrides, snapshots): snake_case, no nulls.</summary>
    public static readonly JsonSerializerOptions Storage = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Options for the admin HTTP API: camelCase.</summary>
    public static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Compact options for wire payloads we generate ourselves.</summary>
    public static readonly JsonSerializerOptions Wire = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Storage);

    public static T? Deserialize<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Storage);
}

/// <summary>Sortable unique id (ULID, Crockford base32).</summary>
public static class Ulid
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string NewUlid() => NewUlid(DateTimeOffset.UtcNow);

    public static string NewUlid(DateTimeOffset time)
    {
        Span<char> chars = stackalloc char[26];
        var ms = time.ToUnixTimeMilliseconds();
        for (var i = 9; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(ms & 31)];
            ms >>= 5;
        }
        Span<byte> random = stackalloc byte[16];
        RandomNumberGenerator.Fill(random);
        for (var i = 10; i < 26; i++) chars[i] = Alphabet[random[i - 10] & 31];
        return new string(chars);
    }
}

public static class Money
{
    public const long NanosPerUsd = 1_000_000_000L;

    public static long ToNanos(decimal usd) =>
        (long)decimal.Round(usd * NanosPerUsd, 0, MidpointRounding.AwayFromZero);

    public static decimal FromNanos(long nanos) => nanos / (decimal)NanosPerUsd;

    public static string Format(decimal usd, int decimals = 6) =>
        "$" + usd.ToString("N" + decimals, System.Globalization.CultureInfo.InvariantCulture);
}
