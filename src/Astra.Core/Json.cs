using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        TypeInfoResolver = JsonContexts.Resolver,
    };

    /// <summary>Options for the admin HTTP API: camelCase.</summary>
    public static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = JsonContexts.Resolver,
    };

    /// <summary>Compact options for wire payloads we generate ourselves.</summary>
    public static readonly JsonSerializerOptions Wire = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = JsonContexts.Resolver,
    };

    /// <summary>
    /// Options byte-identical to the System.Text.Json defaults (no naming policy, default encoder), with
    /// the resolver swapped for source-generated metadata. Value texts embedded in client configuration
    /// files must escape exactly like the plain <c>JsonSerializer</c> default, so no formatting knob is set.
    /// </summary>
    public static readonly JsonSerializerOptions Raw = new() { TypeInfoResolver = JsonContexts.Resolver };

    // The generic JsonSerializer.Serialize<T>/Deserialize<T> overloads are annotated
    // RequiresDynamicCode/RequiresUnreferencedCode; resolving the JsonTypeInfo first keeps the same
    // formatting (the options instance still supplies the naming policy) without the reflection path.

    /// <summary>Serializes with <see cref="Storage"/>; the type must be registered in a <see cref="JsonContexts"/>-backed context.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonContexts.Info<T>(Storage));

    /// <summary>Deserializes with <see cref="Storage"/>; null/whitespace yields <c>default</c>.</summary>
    public static T? Deserialize<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, JsonContexts.Info<T>(Storage));

    /// <summary>Serializes with <see cref="Api"/> (camelCase), for text files and non-HTTP contracts.</summary>
    public static string SerializeApi<T>(T value) => JsonSerializer.Serialize(value, JsonContexts.Info<T>(Api));

    /// <summary>Deserializes a <see cref="JsonNode"/> tree as camelCase (<see cref="Api"/>).</summary>
    public static T? DeserializeApi<T>(JsonNode? node) =>
        node is null ? default : node.Deserialize<T>(JsonContexts.Info<T>(Api));

    /// <summary>Encodes a string as its default-escaped JSON text (see <see cref="Raw"/>).</summary>
    public static string EncodeString(string value) => JsonSerializer.Serialize(value, JsonContexts.Info<string>(Raw));

    /// <summary>Decodes the string a JSON text holds (see <see cref="Raw"/>); null yields null, malformed text throws <see cref="JsonException"/>.</summary>
    public static string? DecodeString(string? json) =>
        json is null ? null : JsonSerializer.Deserialize(json, JsonContexts.Info<string>(Raw));
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
