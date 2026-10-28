using Astra.Api.Contracts;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Core.Tray;

/// <summary>What the menu bar shows next to the icon (<see cref="Title"/>, macOS) and the extra tooltip line (<see cref="Detail"/>, every platform).</summary>
public sealed record TrayTitleText(string Title, string Detail)
{
    /// <summary>Nothing to show.</summary>
    public static TrayTitleText None { get; } = new("", "");
}

/// <summary>
/// The menu-bar title for one "menu bar text" choice (web lib/tray.ts <c>trayTitle</c> with the labels of
/// components/tray/TrayTitleSource.tsx <c>useTrayTitle</c>): empty when there is nothing to show (no data yet, no quota, or — in "alerts"
/// mode — nothing wrong). The result goes through <see cref="Sanitize"/> before it reaches the OS, like Electron's IPC check.
/// </summary>
public static class TrayTitle
{
    /// <summary>Computes the title and tooltip line.</summary>
    public static TrayTitleText Compute(TrayTitleMode mode, bool running, StatsSummaryDto? summary, IReadOnlyList<TrayQuotaPlan> plans)
    {
        if (mode == TrayTitleMode.None) return TrayTitleText.None;
        if (mode == TrayTitleMode.Alerts)
        {
            if (!running) return new("!", Loc.T("tray.status.stopped"));
            var failed = summary is null ? 0 : TrayMath.FailedRequests(summary);
            if (failed > 0) return new($"⚠ {failed}", Loc.T("tray.title.failures", ("count", failed)));
            var tight = TrayMath.TightestQuota(plans);
            if (tight is not null && tight.Remaining < TrayMath.QuotaCritical)
            {
                var pct = TrayMath.Percent(tight.Remaining);
                return new($"⚠ {pct}", Quota(tight, pct));
            }
            return TrayTitleText.None;
        }
        if (mode == TrayTitleMode.Quota)
        {
            var tight = TrayMath.TightestQuota(plans);
            if (tight is null) return TrayTitleText.None;
            var pct = TrayMath.Percent(tight.Remaining);
            return new(pct, Quota(tight, pct));
        }
        if (summary is null || !running) return TrayTitleText.None;
        switch (mode)
        {
            case TrayTitleMode.Requests:
            {
                var v = StatsFormat.Tokens(summary.Requests, true);
                return new(v, Loc.T("tray.title.requests", ("count", v)));
            }
            case TrayTitleMode.Tokens:
            {
                var v = StatsFormat.Tokens(summary.InputTokens + summary.OutputTokens, true);
                return new(v, Loc.T("tray.title.tokens", ("count", v)));
            }
            default:
            {
                var v = TrayMath.FormatCostShort(summary.CostUsd);
                return new(v, Loc.T("tray.title.cost", ("amount", v)));
            }
        }
    }

    private static string Quota(TightRow tight, string pct)
        => Loc.T("tray.title.quota", ("plan", tight.Plan.ProviderName), ("window", TrayMath.RowName(tight.Row)), ("remaining", pct));

    /// <summary>Single-line, trimmed, at most 24 / 80 characters (desktop/src/shared/trayPanel.ts <c>sanitizeTrayTitle</c>).</summary>
    public static TrayTitleText Sanitize(string? title, string? detail) => new(Clean(title, 24), Clean(detail, 80));

    private static string Clean(string? v, int max)
    {
        if (string.IsNullOrEmpty(v)) return "";
        var s = System.Text.RegularExpressions.Regex.Replace(v, "[\r\n\t]+", " ").Trim();
        return s.Length > max ? s[..max] : s;
    }
}
