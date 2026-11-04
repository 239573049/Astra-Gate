using Astra.Api.Contracts;
using Astra.Desktop.Components.Clients;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Clients;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TKCode.Arc;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;
using Tone = Astra.Desktop.Components.Kit.Tone;

namespace Astra.Desktop.Pages.Clients;

/// <summary>
/// The selected client's panel in the desktop's compact layout (it no longer mirrors the web <c>ClientPanel</c> markup; the behavior is
/// the same). A card in three parts:
/// <list type="bullet">
/// <item>a fixed header — mark, name, status, "detected v · config path [open folder] · written at", install / enable / disable; then
/// one-line bars for the role-map hint, repairs and warnings (the drift bar offers the enable preview, which shows the diff and rewrites);
/// then the tabs <see cref="ClientTab"/>;</item>
/// <item>the selected tab in its own scroll area, rows dense (<see cref="SettingsRow.DenseProperty"/>), static explanations as ⓘ
/// hints: Routing (provider list with an "add provider" picker instead of the full list of the others, account pin, Claude Code's
/// direct panel), Models &amp; token, Options (options and per-model protocols), Version &amp; backups;</item>
/// <item>a fixed bar while an enabled client has choices not yet written to its config (<see cref="ClientDraft.PendingCount"/>), with
/// discard and one apply (the enable preview) — instead of an apply button per group.</item>
/// </list>
/// <para>
/// The panel is redrawn from its queries and the selected client's <see cref="ClientDraft"/> (which keeps unsaved choices across redraws);
/// a query's answer redraws it only when what the panel read from it changed (<see cref="QueryWatch"/>), so polls while an install runs
/// leave it alone (issue #16). A pick in a select updates the draft and schedules one redraw; typing in an option's number field updates
/// the draft and the bottom bar in place, never redrawing under the caret.
/// </para>
/// </summary>
public sealed class ClientPanelView : UserControl
{
    private readonly ClientsPageViewModel _vm;
    private readonly QueryWatch _watch;
    private readonly StackPanel _head = new();
    private readonly StackPanel _body = new();
    private readonly ScrollViewer _scroll;
    private readonly Border _pending = new();
    private readonly Border _card;
    private ClaudeDirectPanel? _directPanel;
    private string? _directKind;
    private Control? _roleMapAnchor;
    private Control? _directAnchor;
    private Control? _shadowAnchor;
    private bool _scheduled;
    private string? _shownKind;
    private ClientTab? _shownTab;

    // Provider list: shown immediately while a save is in flight, reverted when it fails (web ProviderBindings `order`).
    private string? _orderKind;
    private string _orderSavedKey = "";
    private List<ClientBindingDto> _order = [];
    private bool _bindingsBusy;
    private bool _accountBusy;
    private bool _disableBusy;
    private bool _repairBusy;

    /// <summary>The panel of <paramref name="vm"/>'s selected client.</summary>
    public ClientPanelView(ClientsPageViewModel vm)
    {
        _vm = vm;
        _watch = new QueryWatch(Schedule);
        _body.Margin = new Thickness(16, 14, 16, 20);
        _body.MaxWidth = 760;
        _body.HorizontalAlignment = HorizontalAlignment.Left;
        _scroll = new ScrollViewer { Content = _body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        grid.Children.Add(_head);
        Grid.SetRow(_scroll, 1);
        grid.Children.Add(_scroll);
        Grid.SetRow(_pending, 2);
        grid.Children.Add(_pending);
        _card = new Border { BorderThickness = new Thickness(0.5), ClipToBounds = true, Child = grid };
        _card.Token(Border.BackgroundProperty, ArcDesignTokenKind.Surface)
            .Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border)
            .Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusPanel);
        SettingsRow.SetDense(this, true);
        Content = _card;
    }

    /// <summary>The header's first row as last drawn (the polling tests check it is not rebuilt).</summary>
    internal Control? HeaderRow { get; private set; }

    /// <summary>Shows <paramref name="tab"/> now (the visual scenes open a tab before they capture).</summary>
    internal void ShowTab(ClientTab tab)
    {
        _vm.Tab = tab;
        Rebuild();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _watch.Attached = true;
        Rebuild();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _watch.Attached = false;
    }

    /// <summary>Redraws once, after the current event (handlers fire inside controls the redraw removes).</summary>
    public void Schedule()
    {
        if (_scheduled) return;
        _scheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scheduled = false;
            Rebuild();
        });
    }

    // Switches to a tab and scrolls to a part of it (after the redraw has placed it).
    private void Reveal(ClientTab tab, Func<Control?> anchor)
    {
        _vm.Tab = tab;
        Dispatcher.UIThread.Post(() =>
        {
            Rebuild();
            Dispatcher.UIThread.Post(() => anchor()?.BringIntoView(), DispatcherPriority.Loaded);
        });
    }

    private Translate T => (key, args) => Loc.T(key, args);

    private async Task<bool> CopyAsync(string text)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return false;
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or NotSupportedException)
        {
            return false;
        }
    }

    private async Task OpenUrlAsync(string url)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher && Uri.TryCreate(url, UriKind.Absolute, out var uri)) await launcher.LaunchUriAsync(uri);
    }

    /// <summary>Draws the panel from the current state.</summary>
    public void Rebuild()
    {
        _watch.BeginPass();
        _head.Children.Clear();
        _body.Children.Clear();
        _pending.Child = null;
        HeaderRow = null;
        _roleMapAnchor = _directAnchor = _shadowAnchor = null;
        var client = _vm.Selected;
        _card.IsVisible = client is not null;
        if (client is not null) Build(client);
        _watch.EndPass();
    }

    private void Build(ClientInfoDto client)
    {
        var draft = _vm.DraftOf(client);
        var providers = _watch.Use(_vm.Providers);
        var tokens = _watch.Use(_vm.Tokens);
        var soon = client.Availability == "coming_soon";
        var name = ClientCatalog.Name(client.Kind, client.Name);
        var enabledProviders = (providers.Data ?? []).Where(p => p.Enabled).ToList();
        var bound = enabledProviders.FirstOrDefault(p => p.Id == client.ProviderId);
        var accountBinding = ClientsLogic.IsAccountBinding(bound);
        var models = client.ProviderId is not null ? _watch.Use(_vm.Api.Models(client.Kind)) : null;
        // Only the mode matters here (the direct panel draws the rest itself and redraws on its own).
        var direct = draft.IsClaudeCode ? _watch.Use(_vm.Api.ClaudeDirect(), d => d?.Mode) : null;
        var isDirect = draft.IsClaudeCode && direct?.Data?.Mode == "direct";
        // Claude Desktop without a role map: the role rows show a suggestion from the providers' models.
        draft.SuggestRoles(models?.Data);
        SyncOrder(client);
        var boundCount = ClientsLogic.Split(_order, providers.Data ?? []).Bound.Count;

        // ----- tabs this client has -----
        var tabs = new List<(ClientTab Tab, string Label, int? Count)> { (ClientTab.Routing, Loc.T("clients.tab.routing"), isDirect ? null : boundCount) };
        if (!soon && !isDirect) tabs.Add((ClientTab.Models, Loc.T("clients.tab.models"), null));
        if (!soon && ((!isDirect && draft.Descriptors.Count > 0) || draft.ProtocolModels.Count > 0)) tabs.Add((ClientTab.Options, Loc.T("clients.tab.options"), null));
        if (!soon && (client.Install is not null || !isDirect)) tabs.Add((ClientTab.Maintenance, Loc.T("clients.tab.maintenance"), null));
        var tab = tabs.Any(t => t.Tab == _vm.Tab) ? _vm.Tab : ClientTab.Routing;

        BuildHeader(client, draft, name, soon, isDirect);
        AddBars(client, draft, name, soon, isDirect);
        _head.Children.Add(TabStrip(tabs, tab));

        // ----- the selected tab -----
        switch (tab)
        {
            case ClientTab.Routing:
                if (!isDirect)
                {
                    AddBindings(client, providers, soon);
                    if (accountBinding && bound is not null) AddAccount(client, draft, bound);
                }
                if (draft.IsClaudeCode) AddDirectPanel(client);
                break;
            case ClientTab.Models:
                AddToken(client, draft, tokens);
                AddModels(client, draft, models);
                break;
            case ClientTab.Options:
                AddOptions(client, draft, isDirect);
                break;
            case ClientTab.Maintenance:
                if (client.Install is { } inst) AddVersionGroup(client, inst);
                if (!isDirect) AddMaintenance(client);
                break;
        }

        UpdatePending(client, draft, isDirect);
        if (_shownKind != client.Kind || _shownTab != tab) _scroll.Offset = default;
        _shownKind = client.Kind;
        _shownTab = tab;
    }

    // ---------- header ----------

    private void BuildHeader(ClientInfoDto client, ClientDraft draft, string name, bool soon, bool isDirect)
    {
        var title = ClientUi.HRow(6, ClientUi.Text(name, 14, ArcDesignTokenKind.Foreground, FontWeight.SemiBold));
        title.Children.Add(isDirect ? KitBadge.Create(Loc.T("clients.claudeDirect.direct")) : StatusBadge(client));

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 2, 0, 0) };
        TextBlock Muted(string text)
        {
            var block = ClientUi.Text(text, 11.5, ArcDesignTokenKind.TextMuted);
            block.VerticalAlignment = VerticalAlignment.Center;
            return block;
        }
        if (soon) meta.Children.Add(Muted(client.AvailabilityReason ?? Loc.T("clients.comingSoonDetail")));
        else
        {
            meta.Children.Add(Muted(ClientsLogic.GroupOf(client) == ClientGroup.Installed
                ? Loc.T("clients.detected", ("version", ClientsLogic.VersionOf(client) ?? "")).TrimEnd()
                : Loc.T("clients.notDetected")));
            var configPath = client.Detection.ConfigPaths.Count > 0 ? client.Detection.ConfigPaths[0] : null;
            if (!isDirect && !string.IsNullOrEmpty(configPath))
            {
                meta.Children.Add(Muted("·"));
                var path = Muted(configPath);
                path.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
                path.FontSize = 11;
                path.TextTrimming = TextTrimming.PrefixCharacterEllipsis;
                path.MaxWidth = 340;
                ToolTip.SetTip(path, configPath);
                meta.Children.Add(path);
                // The desktop can always open paths (web systemActions.canOpenPaths); copying is the fallback when the folder is gone.
                var open = ClientUi.Button(null, () => _ = _vm.OpenConfigDirAsync(configPath, CopyAsync), ButtonVariant.Ghost, small: true,
                    icon: ClientUi.Icon(LucideIconKind.FolderOpen, 12, ArcDesignTokenKind.TextSecondary));
                open.Padding = new Thickness(4, 0);
                open.MinHeight = open.Height = 20;
                ToolTip.SetTip(open, Loc.T("clients.openConfigDir"));
                Avalonia.Automation.AutomationProperties.SetName(open, Loc.T("clients.openConfigDir"));
                meta.Children.Add(open);
            }
            if (!isDirect && client.AppliedAt is { } applied)
            {
                meta.Children.Add(Muted("·"));
                meta.Children.Add(Muted(Loc.T("clients.appliedAt", ("time", StatsFormat.DateTime(applied, _vm.Zone, Loc.Language)))));
            }
        }
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, meta } };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (!soon && InstallButtonFor(client) is { } install) actions.Children.Add(install);
        if (isDirect)
            actions.Children.Add(ClientUi.Button(Loc.T("clients.claudeDirect.configure"), () => Reveal(ClientTab.Routing, () => _directAnchor), ButtonVariant.Primary));
        else if (client.Enabled)
        {
            var disable = ClientUi.Button(Loc.T("clients.disableRestore"), null);
            disable.IsLoading = _disableBusy;
            disable.Click += async (_, _) =>
            {
                _disableBusy = true;
                disable.IsLoading = true;
                try { await _vm.DisableAsync(client); }
                finally
                {
                    _disableBusy = false;
                    Schedule();
                }
            };
            actions.Children.Add(disable);
        }
        else
        {
            var canEnable = !soon && client.ProviderId is not null && !(draft.IsDesktopClient && !draft.HasRole);
            var enable = ClientUi.Button(Loc.T("clients.enable"), () => _vm.BeginEnable(client), ButtonVariant.Primary, enabled: canEnable);
            var tip = client.ProviderId is null ? Loc.T("clients.pickProviderFirst") : draft.IsDesktopClient && !draft.HasRole ? Loc.T("clients.roleMapRequired") : null;
            if (tip is not null) ToolTip.SetTip(enable, tip);
            actions.Children.Add(enable);
        }

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10, Margin = new Thickness(16, 12, 14, 0) };
        var icon = BrandIcon.Client(client.Kind, 32, fallbackName: client.Name);
        icon.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon);
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);
        HeaderRow = row;
        _head.Children.Add(row);
    }

    private static Control StatusBadge(ClientInfoDto c) => ClientsLogic.StatusOf(c) switch
    {
        ClientStatusBadge.ComingSoon => KitBadge.Create(Loc.T("clients.comingSoon")),
        ClientStatusBadge.Drifted => KitBadge.Create(Loc.T("clients.drifted"), Tone.Orange),
        ClientStatusBadge.Enabled => KitBadge.Create(Loc.T("clients.enabled"), Tone.Green),
        _ => KitBadge.Create(Loc.T("clients.disabled")),
    };

    // ---------- bars under the header ----------

    private void AddBars(ClientInfoDto client, ClientDraft draft, string name, bool soon, bool isDirect)
    {
        if (!soon && !client.Enabled && draft.IsDesktopClient && client.ProviderId is not null && !draft.HasRole)
            _head.Children.Add(Bar(ArcDesignTokenKind.Accent, LucideIconKind.Info, Loc.T("clients.roleMapHint"),
                ClientUi.Button(Loc.T("clients.roleMapGoto"), () => Reveal(ClientTab.Models, () => _roleMapAnchor), ButtonVariant.Secondary, small: true)));
        if (client.Repairs is { Count: > 0 } repairs) _head.Children.Add(RepairBox(client, name, repairs));
        if (isDirect) return;
        var warnings = client.Warnings.Select(w => ClientsLogic.WarningText(w, T)).ToList();
        var driftText = Loc.T("clients.warning.drifted");
        if (client.Status == "drifted" && !warnings.Contains(driftText)) warnings.Insert(0, driftText);
        foreach (var w in warnings)
        {
            // The enable preview shows what changed and writes the config again; "disable & restore" stays in the header.
            var review = w == driftText && client.Enabled && client.ProviderId is not null
                ? ClientUi.Button(Loc.T("clients.drift.review"), () => _vm.BeginEnable(client), ButtonVariant.Secondary, small: true)
                : null;
            _head.Children.Add(Bar(ArcDesignTokenKind.Warning, LucideIconKind.TriangleAlert, w, review));
        }
    }

    /// <summary>A one-line tinted bar (the compact layout's alert): a glyph and text in <paramref name="tone"/>, an optional action.</summary>
    private static Control Bar(ArcDesignTokenKind tone, LucideIconKind glyph, string text, Control? action)
    {
        var tint = new Border { Opacity = 0.12, IsHitTestVisible = false };
        tint.Token(Border.BackgroundProperty, tone).Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusControl);
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8, Margin = new Thickness(10, 5, 6, 5), MinHeight = 22 };
        var icon = ClientUi.Icon(glyph, 14, tone);
        line.Children.Add(icon);
        var label = ClientUi.Wrap(text, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Foreground, 12);
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 1);
        line.Children.Add(label);
        if (action is not null)
        {
            action.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(action, 2);
            line.Children.Add(action);
        }
        return new Panel { Margin = new Thickness(16, 8, 14, 0), Children = { tint, line } };
    }

    private Control RepairBox(ClientInfoDto client, string name, IReadOnlyList<ConfigRepairDto> repairs)
    {
        var lines = new StackPanel { Spacing = 2 };
        foreach (var r in repairs)
        {
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            var file = new Avalonia.Controls.Documents.Run(r.File);
            file.Token(Avalonia.Controls.Documents.TextElement.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
            line.Inlines!.Add(file);
            line.Inlines.Add(new Avalonia.Controls.Documents.Run(" — " + Loc.T("clients.repair.problem." + r.Problem, ("line", r.Line))));
            line.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
            lines.Children.Add(line);
        }
        lines.Children.Add(ClientUi.Wrap(Loc.T("clients.repair.hint"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, 12));
        var fix = ClientUi.Button(Loc.T("clients.repair.action"), null, ButtonVariant.Primary, small: true);
        fix.HorizontalAlignment = HorizontalAlignment.Left;
        fix.Margin = new Thickness(0, 4, 0, 0);
        fix.IsLoading = _repairBusy;
        fix.Click += async (_, _) =>
        {
            _repairBusy = true;
            fix.IsLoading = true;
            try { await _vm.RepairAsync(client); }
            finally
            {
                _repairBusy = false;
                Schedule();
            }
        };
        lines.Children.Add(fix);
        var alert = ClientUi.Alert(AlertTone.Danger, Loc.T("clients.repair.title", ("name", name)), lines);
        alert.Margin = new Thickness(16, 8, 14, 0);
        return alert;
    }

    // ---------- tabs ----------

    private Control TabStrip(IReadOnlyList<(ClientTab Tab, string Label, int? Count)> tabs, ClientTab selected)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(10, 6, 14, 0) };
        foreach (var (tab, label, count) in tabs)
        {
            var on = tab == selected;
            var text = ClientUi.HRow(5, ClientUi.Text(label, 12.5, on ? ArcDesignTokenKind.Foreground : ArcDesignTokenKind.TextSecondary, on ? FontWeight.Medium : FontWeight.Normal));
            if (count is { } n)
            {
                var pill = new Border { Padding = new Thickness(5, 0), CornerRadius = new CornerRadius(8), Child = ClientUi.Text(n.ToString(System.Globalization.CultureInfo.InvariantCulture), 10.5, ArcDesignTokenKind.TextMuted) };
                pill.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
                text.Children.Add(pill);
            }
            var underline = new Border { Height = 2, CornerRadius = new CornerRadius(1), IsVisible = on };
            underline.Token(Border.BackgroundProperty, ArcDesignTokenKind.Accent);
            var cell = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Height = 34 };
            text.Margin = new Thickness(8, 0);
            cell.Children.Add(text);
            Grid.SetRow(underline, 1);
            cell.Children.Add(underline);
            var item = new Border { Child = cell, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Focusable = true };
            Avalonia.Automation.AutomationProperties.SetName(item, label);
            var t = tab;
            void Pick()
            {
                if (t == _vm.Tab) return;
                _vm.Tab = t;
                Schedule();
            }
            item.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(item).Properties.IsLeftButtonPressed) Pick();
            };
            item.KeyDown += (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Space)
                {
                    e.Handled = true;
                    Pick();
                }
            };
            strip.Children.Add(item);
        }
        var line = new Border { Height = 1 };
        line.Token(Border.BackgroundProperty, ArcDesignTokenKind.BorderSubtle);
        return new StackPanel { Children = { strip, line } };
    }

    /// <summary>A section title with an ⓘ hint and an action on the right (the provider list's title row).</summary>
    private static Control SectionHeader(string title, string? hint, Control? action)
    {
        var label = ClientUi.Text(title, 12, ArcDesignTokenKind.TextSecondary, FontWeight.Medium);
        var line = ClientUi.HRow(5, label);
        line.Background = Brushes.Transparent;
        if (!string.IsNullOrEmpty(hint))
        {
            line.Children.Add(ClientUi.Icon(LucideIconKind.Info, 12, ArcDesignTokenKind.TextMuted));
            ToolTip.SetTip(line, SettingsRow.HintTip(hint));
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(4, 0, 0, 6), MinHeight = 24 };
        grid.Children.Add(line);
        if (action is not null)
        {
            action.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(action, 1);
            grid.Children.Add(action);
        }
        return grid;
    }

    // ---------- bottom bar: choices not yet written ----------

    private void UpdatePending(ClientInfoDto client, ClientDraft draft, bool isDirect)
    {
        _pending.Child = null;
        var count = draft.PendingCount;
        if (!client.Enabled || isDirect || client.Availability == "coming_soon" || count == 0) return;
        var dot = new StatusDot(Tone.Accent) { VerticalAlignment = VerticalAlignment.Center };
        var text = ClientUi.Text(Loc.T("clients.pending", ("count", count)), 12, ArcDesignTokenKind.Foreground);
        text.VerticalAlignment = VerticalAlignment.Center;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        var discard = ClientUi.Button(Loc.T("clients.pending.discard"), () =>
        {
            draft.Revert();
            Schedule();
        }, ButtonVariant.Ghost, small: true);
        var blocked = draft.IsDesktopClient && !draft.HasRole;
        var apply = ClientUi.Button(Loc.T("clients.pending.apply"), () => _vm.BeginEnable(client), ButtonVariant.Primary, small: true, enabled: !blocked);
        if (blocked) ToolTip.SetTip(apply, Loc.T("clients.roleMapRequired"));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(16, 8, 12, 8) };
        grid.Children.Add(dot);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(discard, 2);
        grid.Children.Add(discard);
        Grid.SetColumn(apply, 3);
        grid.Children.Add(apply);
        var bar = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = grid };
        bar.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted).Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border);
        _pending.Child = bar;
    }

    // ---------- routing: provider bindings (web ProviderBindings) ----------

    private void SyncOrder(ClientInfoDto client)
    {
        var saved = ClientsLogic.SavedBindings(client);
        var savedKey = string.Join("\n", saved.Select(b => b.ProviderId + "|" + b.AccountId));
        if (_orderKind == client.Kind && _orderSavedKey == savedKey) return;
        _orderKind = client.Kind;
        _orderSavedKey = savedKey;
        _order = saved;
    }

    private void AddBindings(ClientInfoDto client, Query<List<ProviderDto>> providers, bool soon)
    {
        var all = providers.Data ?? [];
        var (bound, available) = ClientsLogic.Split(_order, all);
        var busy = soon || _bindingsBusy;
        var loading = !providers.HasData && !providers.IsError;

        async Task Save(List<ClientBindingDto> next, string done)
        {
            var previous = _order;
            _order = next;
            _bindingsBusy = true;
            Schedule();
            var ok = await _vm.SaveBindingsAsync(client, next, done);
            _bindingsBusy = false;
            if (!ok) _order = previous;
            Schedule();
        }

        void Add(ProviderDto p) => _ = Save([.. _order, new ClientBindingDto(p.Id, null)], Loc.T("clients.providerAdded", ("provider", p.Name)));

        var picker = !loading && !soon && bound.Count > 0 && available.Count > 0 ? AddProviderPicker(available, busy, Add) : null;
        _body.Children.Add(SectionHeader(Loc.T("clients.providerTitle"), Loc.T("clients.providerFooter"), picker));
        var group = new SettingsGroup();
        if (loading) group.Rows.Add(new LoadingState(2) { Margin = new Thickness(16) });
        if (!loading && bound.Count == 0 && available.Count == 0)
        {
            var add = ClientUi.Button(Loc.T("providers.add"), _vm.AddProvider, ButtonVariant.Primary, icon: ClientUi.Icon(LucideIconKind.Plus));
            add.HorizontalAlignment = HorizontalAlignment.Center;
            group.Rows.Add(new EmptyStateView(Loc.T("clients.noProviders"), Loc.T("clients.noProvidersDetail"), action: add));
        }
        if (bound.Count > 0)
        {
            var list = new ReorderRows { CanReorder = !busy };
            for (var i = 0; i < bound.Count; i++)
            {
                var provider = bound[i].Provider;
                var grip = ClientUi.Icon(LucideIconKind.GripVertical, 14, ArcDesignTokenKind.TextMuted);
                grip.IsHitTestVisible = true;
                var dragLabel = Loc.T("clients.dragToReorder", ("provider", provider.Name));
                var gripBox = new Border { Child = grip, Background = Brushes.Transparent, Margin = new Thickness(-6, 0, 0, 0), Opacity = busy ? 0.5 : 1 };
                ToolTip.SetTip(gripBox, dragLabel);
                Avalonia.Automation.AutomationProperties.SetName(gripBox, dragLabel);
                var label = ClientUi.HRow(6, KitStyle.Text(provider.Name, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground));
                if (i == 0) label.Children.Add(KitBadge.Create(Loc.T("clients.primaryProvider"), Tone.Accent));
                if (!provider.Enabled) label.Children.Add(KitBadge.Create(Loc.T("clients.providerDisabledBadge"), Tone.Orange));
                var detail = ClientUi.Text(ClientsLogic.ProviderDetail(provider, T), 11.5, ArcDesignTokenKind.TextMuted);
                detail.VerticalAlignment = VerticalAlignment.Center;
                label.Children.Add(detail);
                var row = new SettingsRow { Icon = ClientUi.HRow(8, gripBox, BrandIcon.Provider(provider.Name, provider.Icon, 20)), Label = label };
                if (bound.Count > 1)
                {
                    var removeLabel = Loc.T("clients.removeProvider", ("provider", provider.Name));
                    var p = provider;
                    var remove = ClientUi.Button(null, () => _ = Save(_order.Where(b => b.ProviderId != p.Id).ToList(), Loc.T("clients.providerRemoved", ("provider", p.Name))),
                        ButtonVariant.Ghost, small: true, icon: ClientUi.Icon(LucideIconKind.X, 13), enabled: !busy);
                    ToolTip.SetTip(remove, removeLabel);
                    Avalonia.Automation.AutomationProperties.SetName(remove, removeLabel);
                    row.Trailing.Add(remove);
                }
                list.Add(row, gripBox);
            }
            var boundIds = bound.Select(b => b.Provider.Id).ToList();
            list.Reordered += (from, to) =>
            {
                // The rows are the bound providers; map their positions back onto the stored order (which may name gone providers).
                var fromIndex = _order.FindIndex(b => b.ProviderId == boundIds[from]);
                var toIndex = _order.FindIndex(b => b.ProviderId == boundIds[to]);
                if (fromIndex < 0 || toIndex < 0) return;
                _ = Save(ClientsLogic.Move(_order, fromIndex, toIndex), Loc.T("clients.providerOrderSaved"));
            };
            group.Rows.Add(list);
        }
        // Nothing bound yet: the choice is the whole task, so the providers are listed right here.
        if (!loading && bound.Count == 0)
            foreach (var p in available)
            {
                var row = new SettingsRow { Icon = BrandIcon.Provider(p.Name, p.Icon, 20), Label = p.Name, Detail = ClientsLogic.ProviderDetail(p, T) };
                if (!soon) row.Trailing.Add(ClientUi.Icon(LucideIconKind.Plus, 14, ArcDesignTokenKind.TextSecondary));
                var provider = p;
                if (!busy) row.Invoked += (_, _) => Add(provider);
                group.Rows.Add(row);
            }
        _body.Children.Add(group);
    }

    /// <summary>The "add provider" button and its searchable list of the providers not bound yet (replaces the web's full list of them).</summary>
    private Control AddProviderPicker(IReadOnlyList<ProviderDto> available, bool busy, Action<ProviderDto> add)
    {
        var button = ClientUi.Button(Loc.T("clients.addProvider"), null, ButtonVariant.Secondary, small: true, icon: ClientUi.Icon(LucideIconKind.Plus, 12), enabled: !busy);
        var list = new StackPanel { Margin = new Thickness(4, 0, 4, 4) };
        var search = new SearchField { Placeholder = Loc.T("clients.searchProviders"), Label = Loc.T("clients.searchProviders"), IsLabelVisible = false, Size = ControlSize.Small, Margin = new Thickness(6, 6, 6, 4) };
        var stack = new StackPanel { Children = { search, new ScrollViewer { MaxHeight = 300, Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } } };
        var card = new Border { Width = 300, BorderThickness = new Thickness(0.5), CornerRadius = new CornerRadius(8), Child = stack };
        card.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceRaised).Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border);
        card.BoxShadow = BoxShadows.Parse("0 8 24 0 #33000000");
        var popup = new Popup { PlacementTarget = button, Placement = PlacementMode.BottomEdgeAlignedRight, IsLightDismissEnabled = true, VerticalOffset = 4, Child = card };

        void Fill()
        {
            list.Children.Clear();
            var q = (search.Text ?? "").Trim();
            var matches = available.Where(p => q.Length == 0 || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Id.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                var none = ClientUi.Text(Loc.T("common.noResults"), 12, ArcDesignTokenKind.TextMuted);
                none.Margin = new Thickness(8, 6);
                list.Children.Add(none);
            }
            foreach (var p in matches)
            {
                var name = ClientUi.Text(p.Name, 12.5, ArcDesignTokenKind.Foreground);
                name.VerticalAlignment = VerticalAlignment.Center;
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                var detail = ClientUi.Text(ClientsLogic.ProviderDetail(p, T), 11, ArcDesignTokenKind.TextMuted);
                detail.VerticalAlignment = VerticalAlignment.Center;
                detail.MaxWidth = 130;
                detail.TextTrimming = TextTrimming.CharacterEllipsis;
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
                grid.Children.Add(BrandIcon.Provider(p.Name, p.Icon, 18));
                Grid.SetColumn(name, 1);
                grid.Children.Add(name);
                Grid.SetColumn(detail, 2);
                grid.Children.Add(detail);
                var row = new Border { Height = 32, Padding = new Thickness(8, 0), Child = grid, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Focusable = true };
                row.Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusControl);
                Avalonia.Automation.AutomationProperties.SetName(row, p.Name);
                row.PointerEntered += (_, _) => row.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
                row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
                var provider = p;
                void Pick()
                {
                    popup.IsOpen = false;
                    add(provider);
                }
                row.PointerPressed += (_, e) =>
                {
                    if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) Pick();
                };
                row.KeyDown += (_, e) =>
                {
                    if (e.Key is Key.Enter or Key.Space)
                    {
                        e.Handled = true;
                        Pick();
                    }
                };
                list.Children.Add(row);
            }
        }

        search.TextChanged += (_, _) => Fill();
        search.KeyDown += (_, e) =>
        {
            // Enter picks the only (or first) match.
            if (e.Key != Key.Enter) return;
            var q = (search.Text ?? "").Trim();
            if (available.FirstOrDefault(p => q.Length == 0 || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Id.Contains(q, StringComparison.OrdinalIgnoreCase)) is not { } first) return;
            e.Handled = true;
            popup.IsOpen = false;
            add(first);
        };
        button.Click += (_, _) =>
        {
            search.Text = "";
            Fill();
            popup.IsOpen = true;
            Dispatcher.UIThread.Post(() => search.Focus(), DispatcherPriority.Loaded);
        };
        return new Panel { Children = { button, popup } };
    }

    private void AddAccount(ClientInfoDto client, ClientDraft draft, ProviderDto bound)
    {
        var accounts = _watch.Use(new ListQueries(_vm.Context!.Api, _vm.Context.Queries).Accounts(bound.Id));
        var group = new SettingsGroup { Title = Loc.T("clients.accountTitle"), Hint = Loc.T("clients.accountFooter") };
        if (!accounts.HasData && !accounts.IsError) group.Rows.Add(new LoadingState(2) { Margin = new Thickness(16) });
        else
        {
            var row = new SettingsRow { Label = Loc.T("clients.account") };
            row.Trailing.Add(ClientUi.Select(ClientUi.Options(ClientsLogic.AccountOptions(accounts.Data, T)), draft.Account, v =>
            {
                draft.Account = v;
                Schedule();
            }, Loc.T("clients.account"), maxWidth: 300));
            // The account pin is a binding, saved on its own (it is not written into the client's config).
            if (draft.AccountChanged)
            {
                var apply = ClientUi.Button(Loc.T("common.apply"), null, ButtonVariant.Primary, small: true);
                apply.IsLoading = _accountBusy;
                apply.Click += async (_, _) =>
                {
                    _accountBusy = true;
                    apply.IsLoading = true;
                    try { await _vm.SaveAccountAsync(client, draft.Account); }
                    finally
                    {
                        _accountBusy = false;
                        Schedule();
                    }
                };
                row.Trailing.Add(apply);
            }
            group.Rows.Add(row);
        }
        _body.Children.Add(group);
    }

    private void AddDirectPanel(ClientInfoDto client)
    {
        if (_directPanel is null || _directKind != client.Kind)
        {
            _directPanel = new ClaudeDirectPanel(_vm.Api, _vm.Context!.Feedback, _vm.Context.Navigator, CopyAsync, _vm.Zone, client.Enabled);
            _directKind = client.Kind;
        }
        else _directPanel.GatewayEnabled = client.Enabled;
        _directAnchor = _directPanel;
        _body.Children.Add(_directPanel);
    }

    // ---------- models & token ----------

    private void AddToken(ClientInfoDto client, ClientDraft draft, Query<List<TokenDto>> tokens)
    {
        var group = new SettingsGroup { Title = Loc.T("clients.tokenTitle"), Hint = Loc.T("clients.tokenFooter") };
        var row = new SettingsRow { Label = Loc.T("clients.token") };
        row.Trailing.Add(ClientUi.Select(ClientsLogic.TokenOptions(tokens.Data, draft.SavedToken, T), draft.TokenId, v =>
        {
            draft.TokenId = v;
            Schedule();
        }, Loc.T("clients.token"), maxWidth: 300));
        group.Rows.Add(row);
        var manage = new SettingsRow { Icon = ClientUi.Icon(LucideIconKind.KeyRound, 14, ArcDesignTokenKind.TextSecondary), Label = Loc.T("clients.manageTokens") };
        manage.Invoked += (_, _) => _vm.OpenTokens();
        group.Rows.Add(manage);
        _body.Children.Add(group);
    }

    private void AddModels(ClientInfoDto client, ClientDraft draft, Query<List<string>>? models)
    {
        if (client.ProviderId is null)
        {
            var note = ClientUi.Wrap(Loc.T("clients.pickProviderFirst"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted, 12);
            note.Margin = new Thickness(4, 0, 4, 16);
            _body.Children.Add(note);
            return;
        }
        var modelOptions = new List<(string, string, bool)> { ("", Loc.T("clients.modelUnset"), false) };
        modelOptions.AddRange((models?.Data ?? []).Select(m => (m, m, false)));

        if (draft.IsDesktopClient)
        {
            var group = new SettingsGroup { Title = Loc.T("clients.roleMapTitle"), Hint = Loc.T("clients.roleMapFooter") };
            foreach (var role in ClientExtras.Roles)
            {
                var label = Loc.T("clients.role." + role);
                var row = new SettingsRow { Label = label };
                var r = role;
                row.Trailing.Add(ClientUi.Select(modelOptions, draft.Roles.GetValueOrDefault(role) ?? "", v =>
                {
                    var next = new Dictionary<string, string>(draft.Roles, StringComparer.Ordinal);
                    if (v.Length == 0) next.Remove(r); else next[r] = v;
                    draft.Roles = next;
                    Schedule();
                }, label, Loc.T("clients.modelPlaceholder"), maxWidth: 300));
                group.Rows.Add(row);
            }
            if (!draft.HasRole)
                group.Rows.Add(new SettingsRow { Label = ClientUi.Text(Loc.T("clients.roleMapRequired"), 12, ArcDesignTokenKind.Warning) });
            _roleMapAnchor = group;
            _body.Children.Add(group);
            return;
        }

        var models2 = new SettingsGroup { Title = Loc.T("clients.modelTitle"), Hint = draft.IsClaudeCode ? Loc.T("clients.modelSlotsFooter") : Loc.T("clients.modelFooter") };
        var model = new SettingsRow { Label = Loc.T("clients.defaultModel") };
        model.Trailing.Add(ClientUi.Select(modelOptions, draft.Model ?? "", v =>
        {
            draft.Model = v.Length == 0 ? null : v;
            Schedule();
        }, Loc.T("clients.defaultModel"), Loc.T("clients.modelPlaceholder"), maxWidth: 300));
        models2.Rows.Add(model);
        foreach (var slot in draft.TierSlots)
        {
            var label = Loc.T("clients.modelSlot." + slot);
            var slotRow = new SettingsRow { Label = label };
            var s = slot;
            slotRow.Trailing.Add(ClientUi.Select(modelOptions, draft.Slots.GetValueOrDefault(slot) ?? "", v =>
            {
                var next = new Dictionary<string, string>(draft.Slots, StringComparer.Ordinal);
                if (v.Length == 0) next.Remove(s); else next[s] = v;
                draft.Slots = next;
                Schedule();
            }, label, Loc.T("clients.modelPlaceholder"), maxWidth: 300));
            models2.Rows.Add(slotRow);
        }
        _body.Children.Add(models2);
    }

    // ---------- options & protocols ----------

    private void AddOptions(ClientInfoDto client, ClientDraft draft, bool isDirect)
    {
        if (!isDirect && draft.Descriptors.Count > 0)
            _body.Children.Add(ClientOptionsGroup.Create(draft.Descriptors, draft.Options, next =>
            {
                draft.Options = next;
                // In place: a number field is being typed into.
                UpdatePending(client, draft, isDirect);
            }, client.Enabled));
        if (draft.ProtocolModels.Count > 0)
            _body.Children.Add(ClientProtocolsGroup.Create(draft.ProtocolModels, draft.SupportedProtocols, draft.Overrides, next =>
            {
                draft.Overrides = next;
                Schedule();
            }, client.Enabled));
    }

    // ---------- install button (web InstallActions) ----------

    private Control? InstallButtonFor(ClientInfoDto client)
    {
        if (client.Install is not { } inst) return null;
        var tipKey = InstallLogic.AdminTipKey(inst);
        Control Admin(string text, LucideIconKind icon, Action click, ButtonVariant variant = ButtonVariant.Secondary)
        {
            var content = ClientUi.HRow(6, ClientUi.Icon(icon), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            if (inst.NeedsAdmin)
            {
                var badge = ClientUi.HRow(4, ClientUi.Icon(LucideIconKind.ShieldAlert, 12, ArcDesignTokenKind.Warning), ClientUi.Text(Loc.T("clients.admin.badge"), 11, ArcDesignTokenKind.Warning));
                content.Children.Add(badge);
            }
            var button = new ArcButton { Variant = variant, Content = content };
            button.Click += (_, _) => click();
            if (tipKey is not null) ToolTip.SetTip(button, Loc.T(tipKey));
            return button;
        }
        return InstallLogic.ButtonFor(inst) switch
        {
            InstallButton.Running => ClientUi.Button(Loc.T("clients.installRunning"), () => _vm.BeginInstall(client, InstallSheetMode.Log), icon: ClientUi.Spinner()),
            InstallButton.Update => Admin(Loc.T("clients.updateTo", ("version", inst.LatestVersion)), LucideIconKind.CircleArrowUp, () => _vm.BeginInstall(client, InstallSheetMode.Update)),
            InstallButton.Shadowed => ClientUi.Button(Loc.T("clients.shadowedBadge"), () => Reveal(ClientTab.Maintenance, () => _shadowAnchor), icon: ClientUi.Icon(LucideIconKind.ShieldAlert)),
            InstallButton.Install => Admin(Loc.T("clients.install"), LucideIconKind.Download, () => _vm.BeginInstall(client, InstallSheetMode.Install)),
            InstallButton.Download => ClientUi.Button(Loc.T("clients.download"), () => _ = OpenUrlAsync(inst.HomepageUrl), icon: ClientUi.Icon(LucideIconKind.ExternalLink)),
            _ => null,
        };
    }

    // ---------- version & backups (web VersionGroup + maintenance rows) ----------

    private void AddVersionGroup(ClientInfoDto client, ClientInstallDto inst)
    {
        // The log row shows the command and the state: the output a running job adds every poll redraws nothing here (issue #16).
        var job = _watch.Use(_vm.Api.InstallJob(client.Kind), j => (j?.Command, j?.State));
        var shadowing = InstallLogic.Shadowing(inst);
        if (shadowing is not null)
        {
            var alert = ClientUi.Alert(AlertTone.Warning, Loc.T("clients.shadowed.title", ("version", shadowing.Version)),
                Loc.T("clients.shadowed.detail", ("path", inst.Executable ?? ""), ("version", inst.Version ?? ""), ("other", shadowing.Path), ("dir", ClientsLogic.DirnameOf(shadowing.Path))));
            alert.Margin = new Thickness(0, 0, 0, 12);
            _shadowAnchor = alert;
            _body.Children.Add(alert);
        }
        else if (inst.NotOnPath && inst.Executable is { } exe)
        {
            var alert = ClientUi.Alert(AlertTone.Warning, Loc.T("clients.notOnPath.title"), Loc.T("clients.notOnPath.detail", ("dir", ClientsLogic.DirnameOf(exe))));
            alert.Margin = new Thickness(0, 0, 0, 12);
            _body.Children.Add(alert);
        }

        Control Value(string text, bool muted)
        {
            var block = new SelectableTextBlock { Text = text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            block.Token(TextBlock.ForegroundProperty, muted ? ArcDesignTokenKind.TextSecondary : ArcDesignTokenKind.Foreground);
            return block;
        }

        var group = new SettingsGroup { Title = Loc.T("clients.versionTitle"), Footer = InstallLogic.BlockerText(inst, T) };
        var current = new SettingsRow { Label = Loc.T("clients.currentVersion"), Detail = inst.Installed ? inst.Executable : null };
        current.Trailing.Add(inst.Installed ? Value(inst.Version ?? Loc.T("clients.versionUnknown"), inst.Version is null) : Value(Loc.T("clients.notInstalled"), true));
        group.Rows.Add(current);
        foreach (var c in inst.OtherCopies ?? [])
        {
            var row = new SettingsRow { Label = Loc.T("clients.otherCopy"), Detail = c.Path };
            if (c.Newer) row.Trailing.Add(KitBadge.Create(Loc.T("clients.otherCopyNewer"), Tone.Orange));
            row.Trailing.Add(Value(c.Version, !c.Newer));
            group.Rows.Add(row);
        }
        if (InstallLogic.ShowsLatest(inst))
        {
            var row = new SettingsRow { Label = Loc.T("clients.latestVersion"), Detail = InstallLogic.LatestDetail(inst, _vm.Zone, Loc.Language, T) };
            if (inst.UpdateAvailable) row.Trailing.Add(KitBadge.Create(Loc.T("clients.updateBadge"), Tone.Orange));
            row.Trailing.Add(Value(inst.LatestVersion ?? Loc.T("clients.latestUnknown"), inst.LatestVersion is null));
            group.Rows.Add(row);
        }
        var method = new SettingsRow { Label = Loc.T("clients.installMethod"), Detail = Loc.T("clients.method." + inst.Method, ("package", inst.Package ?? "")) };
        method.Trailing.Add(ClientUi.Button(Loc.T("clients.homepage"), () => _ = OpenUrlAsync(inst.HomepageUrl), ButtonVariant.Ghost, small: true, icon: ClientUi.Icon(LucideIconKind.ExternalLink, 12)));
        group.Rows.Add(method);
        if (InstallLogic.ShowsUninstall(inst))
        {
            // web VersionGroup: the command and a destructive button, or why Astra will not remove this client.
            var uninstall = new SettingsRow { Label = Loc.T("clients.uninstall"), Detail = inst.UninstallCommand ?? InstallLogic.UninstallUnavailableText(inst, T) };
            if (inst.UninstallCommand is not null)
                uninstall.Trailing.Add(ClientUi.Button(Loc.T("clients.uninstall"), () => _vm.BeginInstall(client, InstallSheetMode.Uninstall), ButtonVariant.Danger,
                    small: true, icon: ClientUi.Icon(LucideIconKind.Trash2, 12), enabled: !inst.Busy));
            group.Rows.Add(uninstall);
        }
        if (job.HasData && job.Data is { } j)
        {
            var log = new SettingsRow
            {
                Icon = ClientUi.Icon(LucideIconKind.ScrollText, 14, ArcDesignTokenKind.TextSecondary),
                Label = Loc.T("clients.installLog"),
                Detail = j.Command + " · " + Loc.T("clients.job." + j.State),
            };
            log.Invoked += (_, _) => _vm.BeginInstall(client, InstallSheetMode.Log);
            group.Rows.Add(log);
        }
        _body.Children.Add(group);
    }

    private void AddMaintenance(ClientInfoDto client)
    {
        var configPath = client.Detection.ConfigPaths.Count > 0 ? client.Detection.ConfigPaths[0] : null;
        var group = new SettingsGroup { Title = Loc.T("clients.maintenance") };
        if (configPath is not null)
        {
            var open = new SettingsRow { Icon = ClientUi.Icon(LucideIconKind.FolderOpen, 14, ArcDesignTokenKind.TextSecondary), Label = Loc.T("clients.openConfigDir"), Detail = configPath };
            open.Invoked += (_, _) => _ = _vm.OpenConfigDirAsync(configPath, CopyAsync);
            group.Rows.Add(open);
        }
        var backups = new SettingsRow { Icon = ClientUi.Icon(LucideIconKind.Archive, 14, ArcDesignTokenKind.TextSecondary), Label = Loc.T("clients.backups"), Hint = Loc.T("clients.backupsDetail") };
        backups.Invoked += (_, _) => _vm.BeginBackups(client);
        group.Rows.Add(backups);
        _body.Children.Add(group);
    }
}
