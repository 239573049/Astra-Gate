using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Settings;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;

namespace Astra.Desktop.Pages.Settings;

/// <summary>
/// Settings › Notifications (web <c>NotificationSettings</c>, plan alerts-webhooks-token-limits §5): the alerts firing right now, the webhooks
/// that receive notifications and the rules that decide what is sent. The lists redraw whenever their query changes (nothing typed lives in
/// them); every editor is a sheet with its own state.
/// </summary>
public sealed class NotificationSettingsView : LiveView
{
    private readonly SettingsApi _api;
    private SettingsGroup _firing = null!, _hooks = null!, _rules = null!;
    private string? _testing;

    /// <summary>Creates the view.</summary>
    public NotificationSettingsView(PageContext context, SettingsApi api) : base(context)
    {
        _api = api;
        Watch(api.FiringAlerts);
        Watch(api.Webhooks);
        Watch(api.AlertRules);
        Watch(api.AlertEvents);
        Watch(api.Providers);
        Watch(api.Tokens);
    }

    /// <inheritdoc />
    protected override void Build()
    {
        _firing = RowBuilder.Group(Loc.T("alerts.firing"), null);
        _hooks = RowBuilder.Group(Loc.T("alerts.webhooks"), Loc.T("alerts.webhooks.hint"));
        _rules = RowBuilder.Group(Loc.T("alerts.rules"), Loc.T("alerts.rules.hint"));
        Content = Ui.Stack(0, _firing, _hooks, _rules);
    }

    /// <inheritdoc />
    protected override void Refresh()
    {
        RefreshFiring();
        RefreshWebhooks();
        RefreshRules();
    }

    private static Control Muted(string text)
    {
        var block = KitStyle.Text(text, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.TextMuted);
        return block;
    }

    private static Control RowIcon(LucideIconKind kind, ArcDesignTokenKind color)
    {
        var icon = new LucideIcon { Kind = kind, Size = 16, VerticalAlignment = VerticalAlignment.Center };
        icon.Token(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, color);
        return icon;
    }

    // ---------------------------------------------------------------- firing

    /// <summary>A list's row before it has data: the spinner while loading, the error once the load failed (never an endless spinner).</summary>
    private static Control Pending<T>(Query<T> q) => q.IsError && q.Error is { } e
        ? new Border { Padding = new Thickness(16, 12), Child = Ui.ErrorText(UiHost.ErrorText(e)) }
        : new Border { Padding = new Thickness(16, 12), Child = Ui.Spinner(), HorizontalAlignment = HorizontalAlignment.Left };

    private void RefreshFiring()
    {
        var rows = new List<Control>();
        var states = _api.FiringAlerts.Data;
        if (states is null) rows.Add(Pending(_api.FiringAlerts));
        else if (states.Count == 0) rows.Add(RowBuilder.Row(Muted(Loc.T("alerts.firing.empty"))));
        else
            foreach (var s in states)
                rows.Add(new SettingsRow
                {
                    Icon = RowIcon(LucideIconKind.BellRing, ArcDesignTokenKind.Danger),
                    Label = $"{AlertLogic.EventName(s.Event).Text} · {s.SubjectName}",
                    Detail = $"{s.RuleName} · {SettingsFormat.DateTime(s.ChangedAt, Loc.Language)}",
                });
        RowAssembly.Sync(_firing, rows);
    }

    // ---------------------------------------------------------------- webhooks

    private void RefreshWebhooks()
    {
        var rows = new List<Control>();
        var hooks = _api.Webhooks.Data;
        if (hooks is null) rows.Add(Pending(_api.Webhooks));
        else if (hooks.Count == 0) rows.Add(RowBuilder.Row(Muted(Loc.T("alerts.webhooks.empty"))));
        else
            foreach (var hook in hooks) rows.Add(WebhookRow(hook));
        rows.Add(Ui.GroupLine(Ui.Button(Loc.T("alerts.webhooks.add"), () => _ = OpenWebhookEditor(null), icon: LucideIconKind.Plus), 8, 8));
        RowAssembly.Sync(_hooks, rows);
    }

    private SettingsRow WebhookRow(WebhookDto hook)
    {
        var name = Ui.Inline(8, KitStyle.Text(hook.Name, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground));
        foreach (var (text, tone) in WebhookLogic.Badges(hook)) name.Children.Add(Ui.Badge(text.Text, tone));
        // web: plain text (selectable); trimmed to one line like the web's truncate.
        var url = new SelectableTextBlock { Text = hook.Url, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        url.Token(TextBlock.FontSizeProperty, ArcDesignTokenKind.TypeXS).Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
        url.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        var row = new SettingsRow { Icon = RowIcon(LucideIconKind.Webhook, ArcDesignTokenKind.TextMuted), Label = name, Detail = url };
        row.Trailing.Add(Ui.Button(Loc.T("alerts.webhooks.test"), () => _ = TestAsync(hook), icon: LucideIconKind.FlaskConical, loading: _testing == hook.Id));
        row.Trailing.Add(Ui.Menu(Loc.T("common.more"), LucideIconKind.Ellipsis,
        [
            new Ui.MenuItem(Loc.T("alerts.webhooks.edit"), LucideIconKind.Pencil, () => _ = OpenWebhookEditor(hook)),
            new Ui.MenuItem(Loc.T("alerts.webhooks.deliveries"), LucideIconKind.ListChecks, () => _ = new DeliveriesSheet(Host, _api, hook).ShowAsync()),
            new Ui.MenuItem(Loc.T("alerts.webhooks.resetSecret"), LucideIconKind.KeyRound, () => _ = ResetSecretAsync(hook)),
            new Ui.MenuItem(Loc.T("common.delete"), LucideIconKind.Trash2, () => _ = DeleteWebhookAsync(hook), Destructive: true, SeparatorBefore: true),
        ]));
        return row;
    }

    /// <summary>Opens the webhook editor (null = a new webhook).</summary>
    public Task OpenWebhookEditor(WebhookDto? hook) => new WebhookEditorSheet(Host, _api, hook, secret => _ = new WebhookSecretSheet(Host, secret).ShowAsync()).ShowAsync();

    /// <summary>Opens the delivery history of a webhook.</summary>
    public Task OpenDeliveries(WebhookDto hook) => new DeliveriesSheet(Host, _api, hook).ShowAsync();

    private async Task TestAsync(WebhookDto hook)
    {
        _testing = hook.Id;
        RefreshWebhooks();
        var delivery = await Host.TryAsync(() => _api.TestWebhookAsync(hook.Id));
        _testing = null;
        if (delivery is not null) Host.Toast(WebhookLogic.TestToast(delivery));
        RefreshWebhooks();
    }

    private async Task ResetSecretAsync(WebhookDto hook)
    {
        if (!await Host.ConfirmAsync(Loc.T("alerts.webhooks.resetSecretConfirm", ("name", hook.Name)), Loc.T("alerts.webhooks.resetSecretDetail"), Loc.T("alerts.webhooks.resetSecret"), true)) return;
        var r = await Host.TryAsync(() => _api.ResetWebhookSecretAsync(hook.Id));
        if (r?.Secret is { } secret) await new WebhookSecretSheet(Host, secret).ShowAsync();
    }

    private async Task DeleteWebhookAsync(WebhookDto hook)
    {
        if (!await Host.ConfirmAsync(Loc.T("alerts.webhooks.deleteConfirm", ("name", hook.Name)), Loc.T("alerts.webhooks.deleteDetail"), Loc.T("common.delete"), true)) return;
        if (await Host.TryAsync(() => _api.DeleteWebhookAsync(hook.Id))) Host.Success(Loc.T("alerts.webhooks.deleted", ("name", hook.Name)));
    }

    // ---------------------------------------------------------------- rules

    private void RefreshRules()
    {
        var rows = new List<Control>();
        var rules = _api.AlertRules.Data;
        if (rules is null) rows.Add(Pending(_api.AlertRules));
        else if (rules.Count == 0) rows.Add(RowBuilder.Row(Muted(Loc.T("alerts.rules.empty"))));
        else
            foreach (var rule in rules) rows.Add(RuleRow(rule));
        rows.Add(Ui.GroupLine(Ui.Button(Loc.T("alerts.rules.add"), () => _ = OpenRuleEditor(null), icon: LucideIconKind.Plus), 8, 8));
        RowAssembly.Sync(_rules, rows);
    }

    private SettingsRow RuleRow(AlertRuleDto rule)
    {
        var name = Ui.Inline(8, KitStyle.Text(rule.Name, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground));
        if (rule.Firing > 0) name.Children.Add(Ui.Badge(Loc.T("alerts.rules.firing", ("count", rule.Firing)), "red"));
        if (!rule.Enabled) name.Children.Add(Ui.Badge(Loc.T("alerts.rules.disabledBadge"), "orange"));
        var hookNames = rule.WebhookIds.Select(id => _api.Webhooks.Data?.FirstOrDefault(w => w.Id == id)?.Name ?? id).ToList();
        var row = new SettingsRow
        {
            Icon = RowIcon(LucideIconKind.BellRing, ArcDesignTokenKind.TextMuted),
            Label = name,
            Detail = $"{AlertLogic.EventName(rule.Event).Text} → {(hookNames.Count == 0 ? "—" : string.Join(", ", hookNames))}",
        };
        var enabled = new ControlledSwitch { IsChecked = rule.Enabled };
        Avalonia.Automation.AutomationProperties.SetName(enabled, Loc.T("alerts.rules.enabled"));
        enabled.ToggleRequested += on => _ = Host.TryAsync(() => _api.UpdateRuleAsync(rule.Id, JsonPatch.Of(("enabled", on))));
        row.Trailing.Add(enabled);
        row.Trailing.Add(Ui.Menu(Loc.T("common.more"), LucideIconKind.Ellipsis,
        [
            new Ui.MenuItem(Loc.T("alerts.rules.edit"), LucideIconKind.Pencil, () => _ = OpenRuleEditor(rule)),
            new Ui.MenuItem(Loc.T("common.delete"), LucideIconKind.Trash2, () => _ = DeleteRuleAsync(rule), Destructive: true, SeparatorBefore: true),
        ]));
        return row;
    }

    /// <summary>Opens the rule editor (null = a new rule).</summary>
    public Task OpenRuleEditor(AlertRuleDto? rule)
        => new RuleSheet(Host, _api, rule, _api.AlertEvents.Data ?? [], _api.Webhooks.Data ?? []).ShowAsync();

    private async Task DeleteRuleAsync(AlertRuleDto rule)
    {
        if (!await Host.ConfirmAsync(Loc.T("alerts.rules.deleteConfirm", ("name", rule.Name)), null, Loc.T("common.delete"), true)) return;
        if (await Host.TryAsync(() => _api.DeleteRuleAsync(rule.Id))) Host.Success(Loc.T("alerts.rules.deleted", ("name", rule.Name)));
    }
}
