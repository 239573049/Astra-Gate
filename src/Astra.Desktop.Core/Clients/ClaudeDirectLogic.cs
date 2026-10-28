using Astra.Api.Contracts;
using Astra.Desktop.Core.Overview;

namespace Astra.Desktop.Core.Clients;

/// <summary>Totals of the shown requests (web <c>claudeDirectTotals</c>).</summary>
public sealed record ClaudeDirectTotals(long Requests, long InputTokens, long TotalInputTokens, long OutputTokens, long Failed, long EstimatedCostNanoUsd);

/// <summary>
/// The rules of web/src/components/ClaudeDirectPanel.tsx: which accounts may carry a session, how they are labelled, which one is selected,
/// when the "gateway is on" warning and the live statistics apply, what a mode switch sends, and the totals of the reported requests.
/// </summary>
public static class ClaudeDirectLogic
{
    /// <summary>The launcher command: it takes the account from the server, so it carries no id and no credential.</summary>
    public const string LaunchCommand = "astra claude";

    /// <summary>Only an active, enabled account may carry a session; the server refuses the rest with a 409.</summary>
    public static bool Usable(ClaudeDirectAccountDto a) => a.Status == "active" && a.Enabled;

    /// <summary>"Work · me@example.com · Pro", plus <paramref name="suffix"/> when the account cannot be used (web <c>accountLabel</c>).</summary>
    public static string Label(ClaudeDirectAccountDto a, string suffix)
    {
        var parts = new List<string> { string.IsNullOrEmpty(a.Name) ? a.Id : a.Name };
        if (!string.IsNullOrEmpty(a.Email)) parts.Add("· " + a.Email);
        if (!string.IsNullOrEmpty(a.Plan)) parts.Add("· " + a.Plan);
        var head = string.Join(" ", parts);
        return Usable(a) ? head : head + " · " + suffix;
    }

    /// <summary>The selected account: the saved one, else the first usable.</summary>
    public static string? SelectedId(ClaudeDirectStateDto? state)
        => state is null ? null : state.AccountId ?? state.Accounts.FirstOrDefault(Usable)?.Id;

    /// <summary>The selected account's entry, if listed.</summary>
    public static ClaudeDirectAccountDto? Selected(ClaudeDirectStateDto? state)
    {
        var id = SelectedId(state);
        return state?.Accounts.FirstOrDefault(a => a.Id == id);
    }

    /// <summary>The mode, "gateway" when unknown (also the server default).</summary>
    public static string Mode(ClaudeDirectStateDto? state) => state?.Mode ?? "gateway";

    /// <summary>While the client is taken over, any direct selection would be written over by the gateway config (web <c>warned</c>).</summary>
    public static bool Warned(bool gatewayEnabled, ClaudeDirectStateDto? state) => gatewayEnabled && (Mode(state) == "direct" || state is not null);

    /// <summary>Statistics are collected only for a selected direct account with consent on; polling follows that (web <c>live</c>).</summary>
    public static bool Live(ClaudeDirectStateDto? state) => Mode(state) == "direct" && Selected(state)?.TelemetryEnabled == true;

    /// <summary>
    /// What a mode switch sends, or why it does nothing (web <c>switchMode</c>): null when nothing changes; <c>NeedAccount</c> when direct
    /// has no usable account (the panel toasts instead).
    /// </summary>
    public static (ClaudeDirectSelectInput? Body, bool NeedAccount) Switch(ClaudeDirectStateDto? state, string next, bool busy)
    {
        if (state is null || busy || next == Mode(state)) return (null, false);
        var target = SelectedId(state) ?? state.Accounts.FirstOrDefault(Usable)?.Id;
        if (next == "direct" && target is null) return (null, true);
        return (new ClaudeDirectSelectInput(next, target, null), false);
    }

    /// <summary>Totals of <paramref name="rows"/> (web <c>claudeDirectTotals</c>).</summary>
    public static ClaudeDirectTotals Totals(IReadOnlyList<ClaudeDirectRequestDto> rows)
    {
        long requests = 0, input = 0, total = 0, output = 0, failed = 0, cost = 0;
        foreach (var r in rows)
        {
            requests++;
            input += r.InputTokens;
            total += r.InputTokens + r.CacheReadTokens + r.CacheCreationTokens;
            output += r.OutputTokens;
            if (r.Status == "error") failed++;
            cost += r.EstimatedCostNanoUsd ?? 0;
        }
        return new ClaudeDirectTotals(requests, input, total, output, failed, cost);
    }

    /// <summary>The cost line: the summed estimate when any row reported one, else a dash.</summary>
    public static string CostText(IReadOnlyList<ClaudeDirectRequestDto> rows)
        => StatsFormat.Nanos(rows.Any(r => r.EstimatedCostNanoUsd is not null) ? Totals(rows).EstimatedCostNanoUsd : null);

    /// <summary>A request row's detail: when, tokens, duration and the upstream account (web row <c>detail</c>).</summary>
    public static string RowDetail(ClaudeDirectRequestDto r, TimeSpan zone, string language, Translate t)
    {
        var parts = new List<string>
        {
            StatsFormat.DateTime(r.OccurredAtUtc, zone, language),
            t("clients.claudeDirect.tokensDetail", ("input", StatsFormat.Tokens(r.InputTokens)), ("output", StatsFormat.Tokens(r.OutputTokens))),
        };
        if (r.DurationMs is { } ms) parts.Add(StatsFormat.Ms(ms));
        if (!string.IsNullOrEmpty(r.AccountUuid)) parts.Add(r.AccountUuid);
        return string.Join(" · ", parts);
    }
}
