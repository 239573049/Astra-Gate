namespace Astra.Core;

/// <summary>Runtime-editable settings stored in the database (key <see cref="StorageKey"/>).</summary>
public sealed class AppSettings
{
    public const string StorageKey = "app";

    /// <summary>Default language for stored billing descriptions: "zh" | "en".</summary>
    public string Locale { get; set; } = "zh";

    /// <summary>Capture client/upstream request and response bodies under bodies/.</summary>
    public bool DebugBodies { get; set; }

    public int BodyRetentionDays { get; set; } = 7;

    /// <summary>Null = keep request logs forever.</summary>
    public int? RequestRetentionDays { get; set; }

    public EffortBudgets EffortBudgets { get; set; } = new();
    public int StreamIdleTimeoutSec { get; set; } = 300;

    /// <summary>Release feed channel for update checks: "stable" | "beta".</summary>
    public string UpdateChannel { get; set; } = "stable";

    /// <summary>Whether the server polls the update feed on a schedule (UpdateCheckWorker).</summary>
    public bool UpdateAutoCheck { get; set; } = true;

    /// <summary>Update feed base URL override; null = built-in default, empty = checks disabled.</summary>
    public string? UpdateFeedUrl { get; set; }

    /// <summary>
    /// Background balance / quota refresh for API-key providers, in minutes (ProviderQuotaWorker).
    /// 0 turns the background refresh off; a provider's own <c>settings.quota.intervalMinutes</c> wins.
    /// </summary>
    public int QuotaAutoIntervalMinutes { get; set; } = 30;

    public AppSettings Clone() => Json.Deserialize<AppSettings>(Json.Serialize(this))!;
}

/// <summary>reasoning effort → thinking budget tokens, used when translating between protocols.</summary>
public sealed class EffortBudgets
{
    public int Low { get; set; } = 1024;
    public int Medium { get; set; } = 4096;
    public int High { get; set; } = 16384;
}
