using System.ComponentModel;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Accounts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.ProviderDetail;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TKCode.Arc;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;
using TKCode.Arc.DesignTokens;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.ProviderDetail;

/// <summary>
/// The API-key tab of a provider without accounts (web <c>ApiKeyTab</c>): the masked stored key with a replace field, the key's usage (today /
/// total, polled every 5 s) and the balance / quota query section.
/// </summary>
public sealed class ApiKeyTabView : UserControl
{
    private readonly PageContext _ctx;
    private readonly ProviderDetailApi _api;
    private readonly ProviderDto _provider;
    private readonly StackPanel _usage = new() { Spacing = 8 };
    private IDisposable? _watch;
    private readonly SettingsRow _keyRow;
    private string _key = "";
    private ArcButton? _save;

    /// <summary>Builds the tab for <paramref name="provider"/>.</summary>
    public ApiKeyTabView(PageContext ctx, ProviderDetailApi api, ProviderDto provider)
    {
        _ctx = ctx;
        _api = api;
        _provider = provider;
        Quota = new ProviderQuotaSection(ctx, api, provider);

        var group = new SettingsGroup { Title = Loc.T("providers.section.auth"), Footer = Loc.T("providers.apiKeyStored") };
        var input = PdUi.Secret("", v => { _key = v; if (_save is not null) _save.IsEnabled = _key.Trim().Length > 0; }, 256, Loc.T("providers.apiKey"), Loc.T("providers.replaceKey"));
        _save = PdUi.Button(Loc.T("common.save"), PdUi.Look.Primary, click: () => _ = SaveAsync(input));
        _save.IsEnabled = false;
        _keyRow = PdUi.Row(Loc.T("providers.apiKey"), KeyDetail(provider), PdUi.Inline(8, input, _save));
        group.Rows.Add(_keyRow);

        // web: <section className="card my-4 flex flex-col gap-2 p-4">
        var usage = PdUi.Card(_usage, new Thickness(16));
        usage.Margin = new Thickness(0, 0, 0, 16); // web my-4, the top collapsing into the group's own bottom margin
        Content = new StackPanel { Children = { group, usage, Quota } };
        RebuildUsage();
    }

    /// <summary>
    /// A new provider object (a save, a refetch): the masked key follows it; the typed replacement and the quota section's form stay (web: the
    /// tab's state survives the re-render, and the quota form only resets when its configuration changed, which the section checks itself).
    /// </summary>
    internal void UpdateProvider(ProviderDto provider) => _keyRow.Detail = KeyDetail(provider);

    private static string KeyDetail(ProviderDto p) => p.HasApiKey ? p.ApiKeyMasked ?? "••••" : Loc.T("providers.noKey");

    /// <summary>The balance query section.</summary>
    public ProviderQuotaSection Quota { get; }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _watch = _api.Usage.Activate();
        _api.Usage.PropertyChanged += OnUsage;
        RebuildUsage();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _api.Usage.PropertyChanged -= OnUsage;
        _watch?.Dispose();
        _watch = null;
    }

    private string _sig = "?";

    private void OnUsage(object? sender, PropertyChangedEventArgs e) => RebuildUsage();

    private void RebuildUsage()
    {
        var q = _api.Usage;
        var mine = q.Data?.Accounts.FirstOrDefault(u => u.AccountId is null);
        var sig = $"{q.IsError}|{q.Error?.Message}|{q.Data?.TimeZone}|{mine}";
        if (sig == _sig) return;
        _sig = sig;
        _usage.Children.Clear();
        _usage.Children.Add(PdUi.Text(Loc.T("providers.usage.title"), 13, ArcDesignTokenKind.Foreground, FontWeight.Medium));
        if (q.IsError) _usage.Children.Add(PdUi.Alert(AlertTone.Danger, Loc.T("providers.usage.error"), q.Error?.Message));
        _usage.Children.Add(new AccountUsageStatsView(mine, valueColor: Astra.Desktop.Components.Accounts.AccountsUi.Inline));
        _usage.Children.Add(PdUi.Hint(ProviderDetailLogic.UsageHint(q.Data?.TimeZone)));
    }

    private async Task SaveAsync(TextBox input)
    {
        var ok = await PdRun.TryAsync(_ctx, () => _api.UpdateAsync(new JsonObject { ["apiKey"] = _key }), _save);
        if (!ok) return;
        input.Text = "";
        _ctx.Feedback.Success(Loc.T("common.saved"));
    }
}
