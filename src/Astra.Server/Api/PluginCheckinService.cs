using System.Text.Json.Nodes;
using Astra.Core.Models;
using Astra.Data;
using Astra.Plugins.Host;

namespace Astra.Server.Api;

/// <summary>One account's check-in today, as the plugin answered it.</summary>
/// <param name="Day">The Beijing day it is for (yyyy-MM-dd).</param>
/// <param name="Outcome">claimed, done, ineligible, inactive, captcha or failed.</param>
public sealed record PluginCheckinDto(string AccountId, string Account, string? Day, string? Outcome, string? Message, double? Credit, int? Streak, DateTimeOffset? At);

/// <summary>A provider's daily check-in: whether its plugin can, whether it is on, and each account's last result.</summary>
public sealed record PluginCheckinStateDto(bool Supported, bool On, List<PluginCheckinDto> Accounts);

/// <summary>POST …/checkin: switch the daily check-in on or off.</summary>
public sealed record PluginCheckinSwitch(bool On);

/// <summary>
/// A plugin's daily check-in (magpie's plugin_checkin.go): a plugin whose auth hook has <c>checkin</c> presses its
/// vendor's daily check-in (签到) for each account. Off until the user switches it on per provider. Each usable
/// account is asked once a Beijing day; a <c>failed</c> answer is asked again after 30 minutes, every other answer
/// settles the day. The result is kept in the account's <c>extra.checkin</c>.
/// </summary>
public sealed class PluginCheckinService(AstraDatabase db, PluginProviders providers, ILogger<PluginCheckinService> logger)
{
    public static readonly TimeSpan RetryFailed = TimeSpan.FromMinutes(30);
    private static readonly string[] Outcomes = ["claimed", "done", "ineligible", "inactive", "captcha", "failed"];
    private static readonly TimeZoneInfo Beijing = FindBeijing();
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Today in Beijing, the day a vendor's check-in counts by.</summary>
    public static string Today(DateTimeOffset now) => TimeZoneInfo.ConvertTime(now, Beijing).ToString("yyyy-MM-dd");

    public async Task<PluginCheckinStateDto> StateAsync(string providerId, CancellationToken ct)
    {
        var provider = await RequireAsync(providerId, ct);
        var accounts = await db.Accounts.ListAsync(providerId, ct);
        return new PluginCheckinStateDto(Supported(provider), IsOn(provider), accounts.Select(Dto).ToList());
    }

    public async Task<PluginCheckinStateDto> SwitchAsync(string providerId, bool on, CancellationToken ct)
    {
        var provider = await RequireAsync(providerId, ct);
        if (!Supported(provider)) throw new AdminApiException(409, $"{provider.Name}'s plugin presses no daily check-in");
        var plugin = provider.Settings["plugin"] as JsonObject ?? new JsonObject();
        plugin["checkin_on"] = on;
        provider.Settings["plugin"] = plugin;
        await db.Providers.UpdateAsync(provider, ct);
        return await StateAsync(providerId, ct);
    }

    /// <summary>Checks in now: every usable account of the provider (whatever today's results), or of every provider switched on.</summary>
    public async Task<PluginCheckinStateDto> RunNowAsync(string providerId, CancellationToken ct)
    {
        var provider = await RequireAsync(providerId, ct);
        if (!Supported(provider)) throw new AdminApiException(409, $"{provider.Name}'s plugin presses no daily check-in");
        await RunAsync(provider, force: true, ct);
        return await StateAsync(providerId, ct);
    }

    /// <summary>The scheduled pass: each provider switched on, each account not yet settled today.</summary>
    public async Task RunDueAsync(CancellationToken ct)
    {
        foreach (var provider in (await db.Providers.ListAsync(ct)).Where(p => p.AuthScheme == AuthSchemes.Plugin && p.Enabled && Supported(p) && IsOn(p)))
            await RunAsync(provider, force: false, ct);
    }

    private async Task RunAsync(Provider provider, bool force, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var today = Today(now);
            var id = Gateway.Pipeline.GatewayPipeline.PluginProviderOf(provider);
            foreach (var account in await db.Accounts.ListAsync(provider.Id, ct))
            {
                if (!account.Enabled || account.Status != AccountStatus.Active || PluginProviderSync.PluginKeyOf(account) is not { } key) continue;
                if (!force && account.Extra["checkin"] is JsonObject last && Str(last, "day") == today)
                {
                    var settled = Str(last, "outcome") != "failed"
                                  || (DateTimeOffset.TryParse(Str(last, "at"), out var at) && now - at < RetryFailed);
                    if (settled) continue;
                }
                var result = new JsonObject { ["day"] = today, ["at"] = now.ToString("O") };
                try
                {
                    var p = new JsonObject { ["provider"] = id, ["account"] = key };
                    if (account.HttpProxy is { Length: > 0 } proxy) p["proxy"] = proxy;
                    var answer = await providers.Host.CallAsync("checkin", p, ct) as JsonObject;
                    var outcome = Str(answer, "outcome");
                    result["outcome"] = outcome is not null && Outcomes.Contains(outcome) ? outcome : "failed";
                    if (outcome is null || !Outcomes.Contains(outcome)) result["message"] = $"the plugin answered {outcome ?? "nothing"}";
                    else if (Str(answer, "message") is { } message) result["message"] = message;
                    if (answer?["credit"] is JsonValue credit && credit.TryGetValue<double>(out var c)) result["credit"] = c;
                    if (answer?["streak"] is JsonValue streak && streak.TryGetValue<int>(out var s)) result["streak"] = s;
                }
                catch (PluginHostException e)
                {
                    // a throw is a failure, asked again later; it never resets the account or a day's result
                    result["outcome"] = "failed";
                    result["message"] = e.Message;
                }
                account.Extra["checkin"] = result;
                await db.Accounts.UpdateAsync(account, CancellationToken.None);
                logger.LogInformation("Check-in for {Provider} / {Account}: {Outcome}", provider.Name, account.DisplayName, Str(result, "outcome"));
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Provider> RequireAsync(string providerId, CancellationToken ct)
    {
        var provider = await db.Providers.GetAsync(providerId, ct) ?? throw new AdminApiException(404, "Provider not found");
        if (provider.AuthScheme != AuthSchemes.Plugin) throw new AdminApiException(409, "This provider isn't a plugin's");
        return provider;
    }

    private static bool Supported(Provider p) => p.Settings["plugin"]?["checkin"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static bool IsOn(Provider p) => p.Settings["plugin"]?["checkin_on"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static PluginCheckinDto Dto(ProviderAccount a)
    {
        var c = a.Extra["checkin"] as JsonObject;
        return new PluginCheckinDto(a.Id, a.DisplayName, Str(c, "day"), Str(c, "outcome"), Str(c, "message"),
            c?["credit"] is JsonValue cv && cv.TryGetValue<double>(out var credit) ? credit : null,
            c?["streak"] is JsonValue sv && sv.TryGetValue<int>(out var streak) ? streak : null,
            DateTimeOffset.TryParse(Str(c, "at"), out var at) ? at : null);
    }

    private static string? Str(JsonNode? o, string key) => PluginProviders.Str(o, key);

    private static TimeZoneInfo FindBeijing()
    {
        foreach (var id in new[] { "Asia/Shanghai", "China Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }
        return TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
    }
}

/// <summary>Runs the daily check-ins: first 2 minutes after start, then every 30 minutes.</summary>
public sealed class PluginCheckinWorker(PluginCheckinService checkins, ILogger<PluginCheckinWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await checkins.RunDueAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "Plugin check-ins failed");
                }
                await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
