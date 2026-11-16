using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Astra.Core.Tools;

namespace Astra.Gateway.Tools;

/// <summary>
/// How a search API with several keys spreads its searches (magpie's search_keys.go, MIT): each search starts from the next key,
/// or from the first when the API is set to <see cref="SearchPicks.Order"/>. A key the API refused lately — or one a quota read
/// found used up — rests: it is asked only after every key that is not resting, the one whose rest ends first before the others,
/// so a search still has every key to fall back on. Memory only: a restart forgets the rests.
/// </summary>
public sealed class SearchKeyRotation
{
    private readonly ConcurrentDictionary<string, KeyRest> _rests = new();
    private readonly ConcurrentDictionary<string, long> _turns = new();

    /// <summary>Why a key rests and until when.</summary>
    public sealed record KeyRest(DateTimeOffset Until, string Why);

    public static readonly TimeSpan RateLimitRest = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan RateLimitRestMin = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RateLimitRestMax = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan TurnedAwayRest = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan OutOfCreditRest = TimeSpan.FromHours(1);

    /// <summary>How a key is known here: the vendor and a hash, so no map, log or dump holds the key itself.</summary>
    public static string KeyId(string vendor, string key) =>
        vendor + " " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 8)).ToLowerInvariant();

    /// <summary>How long a key an API refused with this status rests, and why. 432 and 433 are Tavily's own (its plan and pay-as-you-go limits).</summary>
    public static (TimeSpan Duration, string Why) RestFor(string vendor, int status, string? retryAfter, DateTimeOffset now)
    {
        if (status is 401 or 403) return (TurnedAwayRest, "turned away");
        if (status == 402 || (vendor == SearchVendors.Tavily && status is 432 or 433)) return (OutOfCreditRest, "out of credit");
        var rest = RateLimitRest;
        if (int.TryParse(retryAfter, out var seconds) && seconds >= 0) rest = TimeSpan.FromSeconds(seconds);
        else if (retryAfter is not null && DateTimeOffset.TryParse(retryAfter, out var at)) rest = at - now;
        return (rest < RateLimitRestMin ? RateLimitRestMin : rest > RateLimitRestMax ? RateLimitRestMax : rest, "rate limited");
    }

    /// <summary>Sets a key resting; a rest it has that ends later is kept.</summary>
    public void Rest(string vendor, string key, TimeSpan duration, string why, DateTimeOffset now)
    {
        var id = KeyId(vendor, key);
        var next = new KeyRest(now + duration, why);
        _rests.AddOrUpdate(id, next, (_, old) => old.Until > next.Until && old.Until > now ? old : next);
    }

    /// <summary>Ends a key's rest: it answered, or a quota read found credit.</summary>
    public void Wake(string vendor, string key) => _rests.TryRemove(KeyId(vendor, key), out _);

    /// <summary>The rest a key is in now, if it is.</summary>
    public KeyRest? RestOf(string vendor, string key, DateTimeOffset now) =>
        _rests.TryGetValue(KeyId(vendor, key), out var r) && now < r.Until ? r : null;

    /// <summary>
    /// The order a search asks the keys in, by index: from the key whose turn it is (the first, for <see cref="SearchPicks.Order"/>),
    /// each resting key after those that are not, the soonest awake first. Taking turns, each call moves the next search on by one.
    /// </summary>
    public List<int> Order(string vendor, IReadOnlyList<string> keys, string pick, DateTimeOffset now)
    {
        var n = keys.Count;
        var start = 0;
        if (pick != SearchPicks.Order && n > 1)
            start = (int)((_turns.AddOrUpdate(vendor, 1, (_, v) => v + 1) - 1) % n);
        var order = new List<int>(n);
        var resting = new List<(int Index, DateTimeOffset Until)>();
        for (var k = 0; k < n; k++)
        {
            var i = (start + k) % n;
            if (RestOf(vendor, keys[i], now) is { } r) resting.Add((i, r.Until));
            else order.Add(i);
        }
        order.AddRange(resting.OrderBy(r => r.Until).Select(r => r.Index));
        return order;
    }

    /// <summary>The masked form of a key, for lists and errors: the first and last characters.</summary>
    public static string Mask(string key) => key.Length switch
    {
        0 => "",
        <= 8 => new string('*', key.Length),
        _ => key[..3] + "…" + key[^4..],
    };
}
