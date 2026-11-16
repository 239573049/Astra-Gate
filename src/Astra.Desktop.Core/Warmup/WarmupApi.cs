using System.Globalization;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;

namespace Astra.Desktop.Core.Warmup;

/// <summary>
/// The warm-up's queries and writes (web/src/api/hooks.ts <c>useWarmupSettings</c> …, linked update 36): <c>['warmup']</c> is the global settings,
/// <c>['warmup-accounts']</c> each Claude / ChatGPT account's own plan and last outcome. The same keys as the web's.
/// </summary>
public sealed class WarmupApi
{
    private readonly ApiClient _api;
    private readonly QueryClient _queries;

    /// <summary>Creates the facade over the shared client and cache.</summary>
    public WarmupApi(ApiClient api, QueryClient queries)
    {
        _api = api;
        _queries = queries;
        Settings = queries.Query(QueryKey.Of("warmup"), async ct => await api.GetAsync<WarmupSettingsDto>("/api/warmup", ct) ?? throw new ApiException(0, "empty response"));
        Accounts = queries.Query(QueryKey.Of("warmup-accounts"), async ct => await api.GetAsync<List<WarmupAccountDto>>("/api/warmup/accounts", ct) ?? []);
    }

    /// <summary>['warmup'].</summary>
    public Query<WarmupSettingsDto> Settings { get; }

    /// <summary>['warmup-accounts'].</summary>
    public Query<List<WarmupAccountDto>> Accounts { get; }

    /// <summary>PUT /api/warmup; omitted fields keep their value.</summary>
    public async Task<WarmupSettingsDto> UpdateAsync(WarmupSettingsPatch patch, CancellationToken ct = default)
    {
        var result = await _api.PutAsync<WarmupSettingsPatch, WarmupSettingsDto>("/api/warmup", patch, ct) ?? throw new ApiException(0, "empty response");
        _queries.Invalidate("warmup");
        _queries.Invalidate("warmup-accounts");
        return result;
    }

    /// <summary>PATCH /api/warmup/accounts/{id}.</summary>
    public async Task PatchAccountAsync(string accountId, WarmupAccountPatch patch, CancellationToken ct = default)
    {
        await _api.PatchAsync<WarmupAccountPatch, WarmupAccountDto>("/api/warmup/accounts/" + ApiClient.Enc(accountId), patch, ct);
        _queries.Invalidate("warmup-accounts");
    }

    /// <summary>POST /api/warmup/accounts/{id}/run: the warm-up's request now, whatever the schedule says.</summary>
    public async Task<WarmupRunDto> WarmNowAsync(string accountId, CancellationToken ct = default)
    {
        var result = await _api.PostAsync<WarmupRunDto>("/api/warmup/accounts/" + ApiClient.Enc(accountId) + "/run", ct) ?? throw new ApiException(0, "empty response");
        _queries.Invalidate("warmup-accounts");
        return result;
    }
}

/// <summary>The warm-up form's rules, shared with the web's component: what a time of day looks like, the weekday order, an account's summary line.</summary>
public static class WarmupLogic
{
    /// <summary>Weekdays in the order the picker shows them (Monday first); the values are the server's (0 = Sunday).</summary>
    public static readonly int[] Days = [1, 2, 3, 4, 5, 6, 0];

    /// <summary>Whether <paramref name="text"/> is a time of day the server accepts ("HH:mm").</summary>
    public static bool IsTime(string text) =>
        text.Length == 5 && TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
        && time.ToString("HH:mm", CultureInfo.InvariantCulture) == text;

    /// <summary>The times typed into a field ("06:00, 11:05"), sorted and without repeats; null when one of them is not a time.</summary>
    public static List<string>? ParseTimes(string text)
    {
        var parts = text.Split([',', ';', ' ', '，', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Any(p => !IsTime(p))) return null;
        return parts.Distinct().Order().ToList();
    }

    /// <summary>The weekdays after one was toggled: sorted, and a day that was in is out.</summary>
    public static List<int> Toggle(IReadOnlyList<int> days, int day) =>
        (days.Contains(day) ? days.Where(d => d != day) : days.Append(day)).Order().ToList();

    /// <summary>Whether a weekday is picked (an empty list is every day).</summary>
    public static bool IsPicked(IReadOnlyList<int> days, int day) => days.Count == 0 || days.Contains(day);

    /// <summary>The summary of an account's warm-up: the last outcome, why it was skipped, the next slot.</summary>
    public static string Detail(WarmupAccountDto a, DateTimeOffset now)
    {
        _ = now;
        var parts = new List<string>();
        if (a.LastAt is { } at && a.LastResult is { } result)
            parts.Add(Loc.T("warmup.last", ("result", Loc.T("warmup.result." + result)), ("when", at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))));
        if (!string.IsNullOrEmpty(a.LastMessage)) parts.Add("— " + a.LastMessage);
        if (a.NextAt is { } next) parts.Add(Loc.T("warmup.next", ("when", next.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))));
        if (!a.Warmed) parts.Add(Loc.T("warmup.notWarmed"));
        return string.Join(' ', parts);
    }
}
