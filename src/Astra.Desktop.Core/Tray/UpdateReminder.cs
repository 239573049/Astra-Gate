using System.Text.Json.Nodes;
using Astra.Desktop.Core.Settings;

namespace Astra.Desktop.Core.Tray;

/// <summary>Why a reminder was not shown (<c>ReminderDecision.reason</c>).</summary>
public enum ReminderSuppression { Skipped, Cooldown, Limit }

/// <summary>
/// The update reminder's circuit breaker (desktop/src/shared/updateReminder.ts): for one version the user is reminded at most
/// <see cref="MaxPerVersion"/> times, at least <see cref="Cooldown"/> apart, and never again once they chose "skip this version"; a newer
/// version starts a fresh budget. The state is the <c>updateReminder</c> object of <c>desktop-prefs.json</c>, in the TS shape
/// (<c>{ version, count, lastRemindedAt, skippedVersion }</c>, epoch ms), so the Electron app and this one share it (AGENTS.md linked update 22).
/// </summary>
public static class UpdateReminder
{
    /// <summary>The key in the prefs file.</summary>
    public const string Key = ElectronPrefsMigration.ReminderKey;

    /// <summary><c>REMINDER_COOLDOWN_MS</c>.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromHours(24);

    /// <summary><c>MAX_REMINDERS_PER_VERSION</c>.</summary>
    public const int MaxPerVersion = 3;

    /// <summary><c>decideReminder</c>: null = remind; otherwise why not.</summary>
    public static ReminderSuppression? Decide(JsonNode? state, string version, DateTimeOffset now)
    {
        var s = ElectronPrefsMigration.ParseReminder(state);
        if (Str(s, "skippedVersion") == version) return ReminderSuppression.Skipped;
        if (Str(s, "version") != version) return null;
        if (s["count"]!.GetValue<long>() >= MaxPerVersion) return ReminderSuppression.Limit;
        // A clock moved backwards gives a negative elapsed time, which also counts as "still cooling down".
        if (s["lastRemindedAt"] is JsonValue last && now.ToUnixTimeMilliseconds() - Num(last) < Cooldown.TotalMilliseconds)
            return ReminderSuppression.Cooldown;
        return null;
    }

    /// <summary><c>recordReminder</c>: the state after a reminder for <paramref name="version"/> is shown at <paramref name="now"/>.</summary>
    public static JsonObject Record(JsonNode? state, string version, DateTimeOffset now)
    {
        var s = ElectronPrefsMigration.ParseReminder(state);
        var count = Str(s, "version") == version ? s["count"]!.GetValue<long>() + 1 : 1;
        s["version"] = version;
        s["count"] = count;
        s["lastRemindedAt"] = now.ToUnixTimeMilliseconds();
        return s;
    }

    /// <summary><c>skipVersion</c>.</summary>
    public static JsonObject Skip(JsonNode? state, string version)
    {
        var s = ElectronPrefsMigration.ParseReminder(state);
        s["skippedVersion"] = version;
        return s;
    }

    private static double Num(JsonValue v) => v.TryGetValue<long>(out var l) ? l : v.GetValue<double>();

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>
/// The texts of the update reminder and the tray's update / error dialogs. Electron writes them in the main process, outside the web catalog
/// (<c>reminderLabels</c> in updateReminder.ts; the <c>checkInteractive</c> and mismatch dialogs in update.ts / main.ts were English only),
/// so like <see cref="TrayMenuLabels"/> they are zh / en pairs here, picked by the UI language.
/// </summary>
public sealed class UpdateDialogLabels
{
    private readonly bool _zh;

    private UpdateDialogLabels(bool zh) => _zh = zh;

    /// <summary>The labels for <paramref name="language"/> ("zh" → Chinese, anything else English).</summary>
    public static UpdateDialogLabels For(string language) => new(language.StartsWith("zh", StringComparison.OrdinalIgnoreCase));

    private string T(string zh, string en) => _zh ? zh : en;

#pragma warning disable CS1591 // one label each, named like reminderLabels
    public string Title => T("发现新版本", "Update available");
    public string Message(string version) => _zh ? $"Astra {version} 现已可用" : $"Astra {version} is available";
    public string Update => T("立即更新", "Update Now");
    public string Later => T("稍后提醒", "Remind Me Later");
    public string Skip => T("跳过此版本", "Skip This Version");
    public string NotificationBody => T("点击查看并更新", "Click to review and update");
    public string Updated => T("Astra 已更新", "Astra has been updated");
    public string Failed => T("更新失败", "Update failed");
    public string CheckTitle => T("检查更新", "Check for updates");
    public string UpdateServer => T("更新服务", "Update Server");
    public string Download => T("下载", "Download");
    public string Ok => T("好", "OK");
    public string MismatchTitle => T("Astra API 版本不匹配", "Astra API version mismatch");
    public string MismatchDetail(string? apiVersion, int expectedMajor) => _zh
        ? $"正在运行的服务报告的 API 版本为“{apiVersion ?? "未知"}”，而此桌面应用需要 API 主版本 {expectedMajor}。\n\n请运行 \"astra update\" 更新服务，然后重启 Astra。"
        : $"The running server reports API version \"{apiVersion ?? "unknown"}\", but this desktop app expects API major version {expectedMajor}.\n\nRun \"astra update\" to update the server, then restart Astra.";
    public string FatalError(string message) => _zh ? $"严重错误：{message}" : $"Fatal error: {message}";
#pragma warning restore CS1591

    /// <summary>Release notes as the reminder dialog shows them: trimmed, cut at 600 characters with an ellipsis; null when empty.</summary>
    public static string? Notes(string? notes)
    {
        var n = notes?.Trim();
        if (string.IsNullOrEmpty(n)) return null;
        return n.Length > 600 ? n[..600] + "…" : n;
    }
}
