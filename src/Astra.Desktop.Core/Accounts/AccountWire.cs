using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core;

namespace Astra.Desktop.Core.Accounts;

/// <summary>
/// A started subscription login (web <c>SubscriptionLoginStart</c>). The server answers with one of four shapes (<c>LoginModeDto</c> for
/// pkce, <c>LoginDeviceDto</c>, <c>LoginCliDto</c>, <c>LoginPasteDto</c>), so the answer is read as JSON and folded into this one record.
/// </summary>
/// <param name="Mode">pkce, device, cli or paste.</param>
/// <param name="State">The login's state id (poll / complete address).</param>
/// <param name="AuthorizeUrl">The page to open (pkce, cli, paste).</param>
/// <param name="UserCode">The code to type on the verification page (device).</param>
/// <param name="VerificationUrl">The verification page (device).</param>
/// <param name="Interval">Seconds between polls (device, cli).</param>
public sealed record LoginStart(string Mode, string State, string? AuthorizeUrl, string? UserCode, string? VerificationUrl, int Interval)
{
    /// <summary>Reads the server's answer.</summary>
    public static LoginStart Parse(JsonObject o) => new(
        AccountQuota.Str(o["mode"]) ?? "pkce", AccountQuota.Str(o["state"]) ?? "", AccountQuota.Str(o["authorizeUrl"]),
        AccountQuota.Str(o["userCode"]), AccountQuota.Str(o["verificationUrl"]), (int)(AccountQuota.Num(o["interval"]) ?? 0));

    /// <summary>A device or server-mediated CLI login: the page polls until it is done.</summary>
    public bool Polls => Mode is "device" or "cli";

    /// <summary>Time between polls: the server's interval (5 s when it gives none), at least 3 s.</summary>
    public TimeSpan PollEvery => TimeSpan.FromSeconds(Math.Max(Interval == 0 ? 5 : Interval, 3));
}

/// <summary>The answer of a login poll or completion (web <c>SubscriptionPollResult</c>).</summary>
/// <param name="Status">pending, slow_down, done, expired, denied or error.</param>
/// <param name="Account">The new account when done.</param>
/// <param name="Error">Why it failed.</param>
public sealed record LoginPoll(string Status, ProviderAccountDto? Account, string? Error)
{
    /// <summary>Reads the server's answer.</summary>
    public static LoginPoll Parse(JsonObject o) => new(
        AccountQuota.Str(o["status"]) ?? "error",
        o["account"] is JsonObject a ? Json.DeserializeApi<ProviderAccountDto>(a) : null,
        AccountQuota.Str(o["error"]));

    /// <summary>Still waiting for the user (keep polling).</summary>
    public bool Pending => Status is "pending" or "slow_down";

    /// <summary>The login finished with an account.</summary>
    public bool Done => Status == "done" && Account is not null;

    /// <summary>The failure text: the server's error, else the status itself.</summary>
    public string FailureText => Error ?? Status;
}

/// <summary>One codex reset card (web <c>ResetCredit</c>; upstream JSON, snake_case).</summary>
public sealed record ResetCredit(
    string Id, string? ResetType, bool? SupportedByPlan, string Status, DateTimeOffset? ExpiresAt, DateTimeOffset? RedeemedAt, string? Title, string? Description)
{
    /// <summary>Not usable any more (redeemed or expired).</summary>
    public bool Spent => Status != "available";

    /// <summary>The row's name: title, else the reset type, else the id.</summary>
    public string Name => Title ?? ResetType ?? Id;
}

/// <summary>The codex reset card list (web <c>ResetCreditList</c>).</summary>
/// <param name="Credits">The cards.</param>
/// <param name="AvailableCount">The upstream count of usable cards, else counted from the list.</param>
public sealed record ResetCreditList(IReadOnlyList<ResetCredit> Credits, int AvailableCount)
{
    /// <summary>Reads the upstream document leniently.</summary>
    public static ResetCreditList Parse(JsonObject? o)
    {
        var list = new List<ResetCredit>();
        if (o?["credits"] is JsonArray array)
            foreach (var item in array.OfType<JsonObject>())
                list.Add(new ResetCredit(
                    AccountQuota.Str(item["id"]) ?? "", AccountQuota.Str(item["reset_type"]), AccountQuota.Bool(item["is_supported_by_plan"]),
                    AccountQuota.Str(item["status"]) ?? "", AccountQuota.Date(item["expires_at"]), AccountQuota.Date(item["redeemed_at"]),
                    AccountQuota.Str(item["title"]), AccountQuota.Str(item["description"])));
        var available = AccountQuota.Num(o?["available_count"]) is { } n ? (int)n : list.Count(c => c.Status == "available");
        return new ResetCreditList(list, available);
    }
}

/// <summary>A question a plugin's sign-in method asks (web <c>PluginPrompt</c>).</summary>
public sealed record PluginPrompt(string Type, string Key, string Message, string? Placeholder, IReadOnlyList<(string Label, string Value, string? Hint)> Options)
{
    /// <summary>Reads <c>{prompt}</c> of the prompt endpoint; null when the method has no question left.</summary>
    public static PluginPrompt? Parse(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var options = new List<(string, string, string?)>();
        if (o["options"] is JsonArray array)
            foreach (var item in array.OfType<JsonObject>())
                options.Add((AccountQuota.Str(item["label"]) ?? "", AccountQuota.Str(item["value"]) ?? "", AccountQuota.Str(item["hint"])));
        return new PluginPrompt(AccountQuota.Str(o["type"]) ?? "text", AccountQuota.Str(o["key"]) ?? "", AccountQuota.Str(o["message"]) ?? "",
            AccountQuota.Str(o["placeholder"]), options);
    }

    /// <summary>A choice among options (else a text answer).</summary>
    public bool IsSelect => Type == "select";

    /// <summary>The answer a fresh prompt starts with: the first option of a choice, else empty.</summary>
    public string InitialAnswer => IsSelect && Options.Count > 0 ? Options[0].Value : "";
}

/// <summary>One allowance window of a plugin account (web <c>PluginUsage.windows[]</c>).</summary>
public sealed record PluginUsageWindow(string Name, double? Used, double? ResetsAt, string? Display, double? Amount, double? Limit, string? Unit);

/// <summary>An account's allowance as its plugin tells it (web <c>PluginUsage</c>).</summary>
public sealed record PluginUsage(string? Plan, DateTimeOffset? Until, string? Error, IReadOnlyList<PluginUsageWindow> Windows)
{
    /// <summary>Reads the usage endpoint's answer leniently.</summary>
    public static PluginUsage Parse(JsonObject? o)
    {
        var windows = new List<PluginUsageWindow>();
        if (o?["windows"] is JsonArray array)
            foreach (var w in array.OfType<JsonObject>())
                windows.Add(new PluginUsageWindow(AccountQuota.Str(w["name"]) ?? "", AccountQuota.Num(w["used"]), AccountQuota.Num(w["resetsAt"]),
                    AccountQuota.Str(w["display"]), AccountQuota.Num(w["amount"]), AccountQuota.Num(w["limit"]), AccountQuota.Str(w["unit"])));
        return new PluginUsage(AccountQuota.Str(o?["plan"]), AccountQuota.Date(o?["until"]), AccountQuota.Str(o?["error"]), windows);
    }
}
