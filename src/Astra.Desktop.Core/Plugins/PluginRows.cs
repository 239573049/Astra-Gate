using System.Globalization;
using Astra.Api.Contracts;
using Astra.Desktop.Core.I18n;

namespace Astra.Desktop.Core.Plugins;

/// <summary>A badge on a plugin row.</summary>
/// <param name="Text">What it says.</param>
/// <param name="Tone">Its color.</param>
public sealed record PluginBadge(string Text, PluginTone Tone);

/// <summary>An error box on a plugin row: a title and what went wrong.</summary>
public sealed record PluginAlert(string Title, string Text);

/// <summary>One line of the grey block under a plugin row (web <c>Detail</c>): a label and what the plugin does there.</summary>
public abstract record PluginDetail(string Label);

/// <summary>A gateway middleware's hooks and counters.</summary>
/// <param name="Label">The "middleware" kind label.</param>
/// <param name="Stats">Hooks, calls, µs per call, failures.</param>
/// <param name="LastError">The last failure, null for none.</param>
public sealed record MiddlewareDetail(string Label, string Stats, string? LastError) : PluginDetail(Label);

/// <summary>The client an agent plugin adds: a link to it on the Clients page.</summary>
/// <param name="Label">The "agent" kind label.</param>
/// <param name="Text">"Adds the client X".</param>
/// <param name="ClientKind">The client kind to open (<c>plugin-&lt;id&gt;</c>).</param>
public sealed record AgentDetail(string Label, string Text, string ClientKind) : PluginDetail(Label);

/// <summary>The providers a plugin signs in to, and which of them another plugin serves instead.</summary>
/// <param name="Label">The "provider" kind label.</param>
/// <param name="Text">"Provides: …".</param>
/// <param name="OpenProvidersText">The link to the Providers page.</param>
/// <param name="ServedBy">Providers another plugin serves, each with the button to switch.</param>
/// <param name="PickedText">Says this plugin was picked, null when it was not (for any of its providers).</param>
public sealed record ProvidesDetail(string Label, string Text, string OpenProvidersText, IReadOnlyList<ServedByItem> ServedBy, string? PickedText) : PluginDetail(Label);

/// <summary>"Provider X is served by plugin Y" and the button that makes this plugin serve it.</summary>
/// <param name="Text">The sentence.</param>
/// <param name="ButtonText">The button's label.</param>
/// <param name="Provider">The provider id to prefer.</param>
public sealed record ServedByItem(string Text, string ButtonText, string Provider);

/// <summary>Everything one installed-plugin row shows, computed from the API data (web <c>PluginRow</c>).</summary>
public sealed class PluginRowModel
{
    /// <summary>The plugin as the API gave it.</summary>
    public required PluginDto Plugin { get; init; }

    /// <summary>The first kind (what the icon follows); "provider" when the plugin has none.</summary>
    public required string Kind { get; init; }

    /// <summary>"v1.2.3", null without a version.</summary>
    public string? VersionText { get; init; }

    /// <summary>One badge per kind, then "local" for a path install.</summary>
    public required IReadOnlyList<PluginBadge> Badges { get; init; }

    /// <summary>Why it cannot load, from the plugin itself, the host, its middleware or its agent.</summary>
    public required IReadOnlyList<PluginAlert> Alerts { get; init; }

    /// <summary>The grey block's lines.</summary>
    public required IReadOnlyList<PluginDetail> Details { get; init; }

    /// <summary>It has gateway middleware: its row has move up / down.</summary>
    public bool HasMiddleware => Plugin.Middleware is not null;

    /// <summary>A path install cannot be updated.</summary>
    public bool CanUpdate => !Plugin.IsPath;

    /// <summary>The icon decision.</summary>
    public PluginIconKind IconKind => PluginIcons.Resolve(Plugin.Icon, Kind);
}

/// <summary>Builds <see cref="PluginRowModel"/>s (the text parts of web <c>PluginRow</c>).</summary>
public static class PluginRows
{
    /// <summary>The row of <paramref name="p"/>; <paramref name="host"/> may be null (not asked for, or not loaded yet).</summary>
    public static PluginRowModel Build(PluginDto p, PluginHostStatusDto? host, IReadOnlyDictionary<string, string> preferences)
    {
        var kind = p.Kinds.Count > 0 ? p.Kinds[0] : PluginKinds.Provider;
        var loaded = host?.Plugins.FirstOrDefault(x => x.Spec == p.Spec);
        var mw = p.Middleware;

        var badges = p.Kinds.Select(k => new PluginBadge(PluginKinds.Label(k), PluginKinds.Tone(k))).ToList();
        if (p.IsPath) badges.Add(new PluginBadge(Loc.T("plugins.local"), PluginTone.Orange));

        var alerts = new List<PluginAlert>();
        if (!string.IsNullOrEmpty(p.Error)) alerts.Add(new PluginAlert(Loc.T("plugins.cantLoad"), p.Error));
        if (!string.IsNullOrEmpty(loaded?.Error)) alerts.Add(new PluginAlert(Loc.T("plugins.hostCantLoad"), loaded!.Error!));
        if (!string.IsNullOrEmpty(mw?.Error)) alerts.Add(new PluginAlert(Loc.T("plugins.cantLoad"), Loc.T("plugins.mw.error", ("error", mw!.Error))));
        if (!string.IsNullOrEmpty(p.Agent?.Error)) alerts.Add(new PluginAlert(Loc.T("plugins.cantLoad"), Loc.T("plugins.agent.error", ("error", p.Agent!.Error))));

        var details = new List<PluginDetail>();
        if (mw is not null && string.IsNullOrEmpty(mw.Error))
        {
            var stats = Loc.T("plugins.mw.stats",
                ("hooks", string.Join(", ", mw.Hooks)), ("calls", mw.Calls), ("micros", mw.AvgMicros.ToString("F1", CultureInfo.InvariantCulture)), ("failures", mw.Failures));
            var last = string.IsNullOrEmpty(mw.LastError) ? null : Loc.T("plugins.mw.lastError", ("error", mw.LastError));
            details.Add(new MiddlewareDetail(Loc.T("plugins.kind.middleware"), stats, last));
        }
        if (!string.IsNullOrEmpty(p.Agent?.Kind))
            details.Add(new AgentDetail(Loc.T("plugins.kind.agent"), Loc.T("plugins.agent.adds", ("name", p.Agent!.Name ?? p.Agent.Id ?? "")), p.Agent.Kind));
        if (loaded is { Provides.Count: > 0 })
        {
            var servedBy = loaded.ServedBy
                .Select(kv => new ServedByItem(Loc.T("plugins.servedBy", ("provider", kv.Key), ("other", kv.Value)), Loc.T("plugins.useFor", ("provider", kv.Key)), kv.Key))
                .ToList();
            var picked = loaded.Provides.Any(id => preferences.TryGetValue(id, out var spec) && spec == p.Spec) ? Loc.T("plugins.picked") : null;
            details.Add(new ProvidesDetail(Loc.T("plugins.kind.provider"), Loc.T("plugins.provides", ("providers", string.Join(", ", loaded.Provides))), Loc.T("plugins.openProviders"), servedBy, picked));
        }

        return new PluginRowModel
        {
            Plugin = p,
            Kind = kind,
            VersionText = string.IsNullOrEmpty(p.Version) ? null : "v" + p.Version,
            Badges = badges,
            Alerts = alerts,
            Details = details,
        };
    }

    /// <summary>The order after moving the plugin at <paramref name="index"/> by <paramref name="by"/> places (web <c>move</c>); the same order for an index out of range.</summary>
    public static List<string> Move(IReadOnlyList<string> specs, int index, int by)
    {
        var order = specs.ToList();
        var to = index + by;
        if (index < 0 || index >= order.Count || to < 0 || to >= order.Count) return order;
        var item = order[index];
        order.RemoveAt(index);
        order.Insert(to, item);
        return order;
    }

    /// <summary>True when the list needs the hint about the middleware order (web: some plugin has middleware).</summary>
    public static bool ShowsOrderHint(IEnumerable<PluginDto> plugins) => plugins.Any(p => p.Middleware is not null);

    /// <summary>How many of magpie's plugins an import would bring (those Astra has not got); null when there is nothing to import (web <c>canImport</c>).</summary>
    public static int? ImportableCount(MagpieImportPreviewDto? preview)
    {
        if (preview is not { Found: true }) return null;
        var n = preview.Plugins.Count(p => !p.Installed);
        return n > 0 ? n : null;
    }
}
