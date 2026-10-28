using Astra.Api.Contracts;
using Astra.Desktop.Components.Clients;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Clients;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Avalonia;
using Avalonia.Controls;
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
/// The selected client's panel (web <c>ClientPanel</c> with <c>ProviderBindings</c>, <c>InstallActions</c> and <c>VersionGroup</c>): the header
/// row (mark, name, status, detection, install / enable / disable), the repair and warning alerts, the provider list (drag to reorder), the
/// account pin, the token, Claude Desktop's role map or the default model (Claude Code: plus its tiers), the descriptor-driven options,
/// per-model protocols, the version group, Claude Code's direct panel and the maintenance rows.
/// <para>
/// The panel is redrawn from its queries and the selected client's <see cref="ClientDraft"/> (which keeps unsaved choices across redraws).
/// A pick in a select only updates the draft and schedules one redraw (so the apply buttons appear); typing in an option's number field
/// updates the draft and the apply button in place, never redrawing under the caret.
/// </para>
/// </summary>
public sealed class ClientPanelView : UserControl
{
    private readonly ClientsPageViewModel _vm;
    private readonly QueryWatch _watch;
    private readonly StackPanel _root = new();
    private ClaudeDirectPanel? _directPanel;
    private string? _directKind;
    private Control? _roleMapAnchor;
    private Control? _directAnchor;
    private Control? _shadowAnchor;
    private bool _scheduled;

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
        Content = _root;
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
        _root.Children.Clear();
        _roleMapAnchor = _directAnchor = _shadowAnchor = null;
        if (_vm.Selected is { } client) Build(client);
        _watch.EndPass();
    }

    private void Build(ClientInfoDto client)
    {
        var draft = _vm.DraftOf(client);
        var providers = _watch.Use(_vm.Providers);
        var tokens = _watch.Use(_vm.Tokens);
        var soon = client.Availability == "coming_soon";
        var name = ClientCatalog.Name(client.Kind, client.Name);
        var configPath = client.Detection.ConfigPaths.Count > 0 ? client.Detection.ConfigPaths[0] : null;
        var enabledProviders = (providers.Data ?? []).Where(p => p.Enabled).ToList();
        var bound = enabledProviders.FirstOrDefault(p => p.Id == client.ProviderId);
        var accountBinding = ClientsLogic.IsAccountBinding(bound);
        var models = client.ProviderId is not null ? _watch.Use(_vm.Api.Models(client.Kind)) : null;
        var direct = draft.IsClaudeCode ? _watch.Use(_vm.Api.ClaudeDirect()) : null;
        var isDirect = draft.IsClaudeCode && direct?.Data?.Mode == "direct";
        // Claude Desktop without a role map: the role rows below show a suggestion from the providers' models.
        draft.SuggestRoles(models?.Data);

        // ----- header -----
        var header = new SettingsGroup();
        var title = ClientUi.HRow(8, ClientUi.Text(name, 15, ArcDesignTokenKind.Foreground, FontWeight.Medium));
        title.Children.Add(isDirect ? KitBadge.Create(Loc.T("clients.claudeDirect.direct")) : StatusBadge(client));
        var headRow = new SettingsRow
        {
            Icon = BrandIcon.Client(client.Kind, 36, fallbackName: client.Name),
            Label = title,
            Detail = ClientsLogic.HeaderDetail(client, isDirect, T),
            Padding = new Thickness(0, 4),
        };
        if (!soon && InstallButtonFor(client) is { } install) headRow.Trailing.Add(install);
        if (isDirect)
            headRow.Trailing.Add(ClientUi.Button(Loc.T("clients.claudeDirect.configure"), () => _directAnchor?.BringIntoView(), ButtonVariant.Primary));
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
            headRow.Trailing.Add(disable);
        }
        else
        {
            var canEnable = !soon && client.ProviderId is not null && !(draft.IsDesktopClient && !draft.HasRole);
            var enable = ClientUi.Button(Loc.T("clients.enable"), () => _vm.BeginEnable(client), ButtonVariant.Primary, enabled: canEnable);
            var tip = client.ProviderId is null ? Loc.T("clients.pickProviderFirst") : draft.IsDesktopClient && !draft.HasRole ? Loc.T("clients.roleMapRequired") : null;
            if (tip is not null) ToolTip.SetTip(enable, tip);
            headRow.Trailing.Add(enable);
        }
        header.Rows.Add(headRow);
        if (!isDirect && client.AppliedAt is { } applied)
            header.Rows.Add(new SettingsRow { Label = ClientUi.Text(Loc.T("clients.appliedAt", ("time", StatsFormat.DateTime(applied, _vm.Zone, Loc.Language))), 12, ArcDesignTokenKind.TextSecondary) });
        _root.Children.Add(header);

        // ----- hints and alerts -----
        if (!soon && !client.Enabled && draft.IsDesktopClient && client.ProviderId is not null && !draft.HasRole)
        {
            // The enable button's tooltip alone is easy to miss, and the role map sits below the provider list.
            var box = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 20) };
            box.Children.Add(ClientUi.Alert(AlertTone.Info, Loc.T("clients.roleMapHint")));
            var go = ClientUi.Button(Loc.T("clients.roleMapGoto"), () => _roleMapAnchor?.BringIntoView());
            go.HorizontalAlignment = HorizontalAlignment.Left;
            box.Children.Add(go);
            _root.Children.Add(box);
        }
        if (client.Repairs is { Count: > 0 } repairs) _root.Children.Add(RepairBox(client, name, repairs));
        if (!isDirect && client.Warnings.Count > 0)
        {
            var box = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 20) };
            foreach (var w in client.Warnings) box.Children.Add(ClientUi.Alert(AlertTone.Warning, ClientsLogic.WarningText(w, T)));
            _root.Children.Add(box);
        }

        if (!isDirect) AddBindings(client, providers, soon);

        // ----- account pin -----
        if (!isDirect && accountBinding && bound is not null)
        {
            var accounts = _watch.Use(new ListQueries(_vm.Context!.Api, _vm.Context.Queries).Accounts(bound.Id));
            var group = new SettingsGroup { Title = Loc.T("clients.accountTitle"), Footer = Loc.T("clients.accountFooter") };
            if (!accounts.HasData && !accounts.IsError) group.Rows.Add(new LoadingState(2) { Margin = new Thickness(16) });
            else
            {
                var row = new SettingsRow { Label = Loc.T("clients.account") };
                row.Trailing.Add(ClientUi.Select(ClientUi.Options(ClientsLogic.AccountOptions(accounts.Data, T)), draft.Account, v =>
                {
                    draft.Account = v;
                    Schedule();
                }, Loc.T("clients.account"), maxWidth: 300));
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
            _root.Children.Add(group);
        }

        // ----- token -----
        if (!soon && !isDirect)
        {
            var group = new SettingsGroup { Title = Loc.T("clients.tokenTitle"), Footer = client.Enabled ? Loc.T("clients.tokenFooterEnabled") : Loc.T("clients.tokenFooter") };
            var row = new SettingsRow { Label = Loc.T("clients.token") };
            row.Trailing.Add(ClientUi.Select(ClientsLogic.TokenOptions(tokens.Data, draft.SavedToken, T), draft.TokenId, v =>
            {
                draft.TokenId = v;
                Schedule();
            }, Loc.T("clients.token"), maxWidth: 300));
            if (client.Enabled && draft.TokenChanged) row.Trailing.Add(ApplyButton(client));
            group.Rows.Add(row);
            var manage = new SettingsRow { Icon = ClientUi.Icon(LucideIconKind.KeyRound, 16, ArcDesignTokenKind.TextSecondary), Label = Loc.T("clients.manageTokens") };
            manage.Invoked += (_, _) => _vm.OpenTokens();
            group.Rows.Add(manage);
            _root.Children.Add(group);
        }

        var modelOptions = new List<(string, string, bool)> { ("", Loc.T("clients.modelUnset"), false) };
        modelOptions.AddRange((models?.Data ?? []).Select(m => (m, m, false)));

        // ----- Claude Desktop role map -----
        if (!soon && !isDirect && client.ProviderId is not null && draft.IsDesktopClient)
        {
            var group = new SettingsGroup { Title = Loc.T("clients.roleMapTitle"), Footer = client.Enabled ? Loc.T("clients.roleMapFooterEnabled") : Loc.T("clients.roleMapFooter") };
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
            if (client.Enabled && draft.RolesChanged)
            {
                var row = new SettingsRow { Label = draft.HasRole ? "" : ClientUi.Text(Loc.T("clients.roleMapRequired"), 12, ArcDesignTokenKind.Warning) };
                var apply = ApplyButton(client);
                apply.IsEnabled = draft.HasRole;
                row.Trailing.Add(apply);
                group.Rows.Add(row);
            }
            _roleMapAnchor = group;
            _root.Children.Add(group);
        }

        // ----- default model (+ Claude Code tiers) -----
        if (!soon && !isDirect && client.ProviderId is not null && !draft.IsDesktopClient)
        {
            var footer = draft.IsClaudeCode
                ? client.Enabled ? Loc.T("clients.modelSlotsFooterEnabled") : Loc.T("clients.modelSlotsFooter")
                : client.Enabled ? Loc.T("clients.modelFooterEnabled") : Loc.T("clients.modelFooter");
            var group = new SettingsGroup { Title = Loc.T("clients.modelTitle"), Footer = footer };
            var row = new SettingsRow { Label = Loc.T("clients.defaultModel") };
            row.Trailing.Add(ClientUi.Select(modelOptions, draft.Model ?? "", v =>
            {
                draft.Model = v.Length == 0 ? null : v;
                Schedule();
            }, Loc.T("clients.defaultModel"), Loc.T("clients.modelPlaceholder"), maxWidth: 300));
            group.Rows.Add(row);
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
                group.Rows.Add(slotRow);
            }
            if (client.Enabled && draft.ModelsChanged)
            {
                var apply = new SettingsRow { Label = "" };
                apply.Trailing.Add(ApplyButton(client));
                group.Rows.Add(apply);
            }
            _root.Children.Add(group);
        }

        // ----- options -----
        if (!soon && !isDirect && draft.Descriptors.Count > 0)
        {
            var apply = ApplyButton(client);
            var applyBox = new Border { Child = apply, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, -12, 0, 20) };
            applyBox.IsVisible = client.Enabled && draft.OptionsChanged;
            _root.Children.Add(ClientOptionsGroup.Create(draft.Descriptors, draft.Options, next =>
            {
                draft.Options = next;
                // In place: a number field is being typed into.
                applyBox.IsVisible = client.Enabled && draft.OptionsChanged;
            }, client.Enabled));
            _root.Children.Add(applyBox);
        }

        // ----- protocols -----
        if (!soon && draft.ProtocolModels.Count > 0)
        {
            _root.Children.Add(ClientProtocolsGroup.Create(draft.ProtocolModels, draft.SupportedProtocols, draft.Overrides, next =>
            {
                draft.Overrides = next;
                Schedule();
            }, client.Enabled));
            if (client.Enabled && draft.OverridesChanged)
                _root.Children.Add(new Border { Child = ApplyButton(client), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, -12, 0, 20) });
        }

        if (!soon && client.Install is { } inst) AddVersionGroup(client, inst);

        // ----- Claude Code direct panel -----
        if (draft.IsClaudeCode)
        {
            if (_directPanel is null || _directKind != client.Kind)
            {
                _directPanel = new ClaudeDirectPanel(_vm.Api, _vm.Context!.Feedback, _vm.Context.Navigator, CopyAsync, _vm.Zone, client.Enabled);
                _directKind = client.Kind;
            }
            else _directPanel.GatewayEnabled = client.Enabled;
            _directAnchor = _directPanel;
            _root.Children.Add(_directPanel);
        }

        // ----- maintenance -----
        if (!soon && !isDirect)
        {
            var group = new SettingsGroup { Title = Loc.T("clients.maintenance") };
            if (configPath is not null)
            {
                // The desktop can always open paths (web systemActions.canOpenPaths); copying is the fallback when the folder is gone.
                var open = new SettingsRow { Icon = ClientUi.Icon(LucideIconKind.FolderOpen, 16, ArcDesignTokenKind.TextSecondary), Label = Loc.T("clients.openConfigDir"), Detail = configPath };
                open.Invoked += (_, _) => _ = _vm.OpenConfigDirAsync(configPath, CopyAsync);
                group.Rows.Add(open);
            }
            var backups = new SettingsRow { Icon = ClientUi.Icon(LucideIconKind.Archive, 16, ArcDesignTokenKind.TextSecondary), Label = Loc.T("clients.backups"), Detail = Loc.T("clients.backupsDetail") };
            backups.Invoked += (_, _) => _vm.BeginBackups(client);
            group.Rows.Add(backups);
            _root.Children.Add(group);
        }
    }

    private ArcButton ApplyButton(ClientInfoDto client) => ClientUi.Button(Loc.T("common.apply"), () => _vm.BeginEnable(client), ButtonVariant.Primary, small: true);

    private static Control StatusBadge(ClientInfoDto c) => ClientsLogic.StatusOf(c) switch
    {
        ClientStatusBadge.ComingSoon => KitBadge.Create(Loc.T("clients.comingSoon")),
        ClientStatusBadge.Drifted => KitBadge.Create(Loc.T("clients.drifted"), Tone.Orange),
        ClientStatusBadge.Enabled => KitBadge.Create(Loc.T("clients.enabled"), Tone.Green),
        _ => KitBadge.Create(Loc.T("clients.disabled")),
    };

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
            InstallButton.Shadowed => ClientUi.Button(Loc.T("clients.shadowedBadge"), () => _shadowAnchor?.BringIntoView(), icon: ClientUi.Icon(LucideIconKind.ShieldAlert)),
            InstallButton.Install => Admin(Loc.T("clients.install"), LucideIconKind.Download, () => _vm.BeginInstall(client, InstallSheetMode.Install)),
            InstallButton.Download => ClientUi.Button(Loc.T("clients.download"), () => _ = OpenUrlAsync(inst.HomepageUrl), icon: ClientUi.Icon(LucideIconKind.ExternalLink)),
            _ => null,
        };
    }

    // ---------- repair ----------

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
        var box = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 20) };
        box.Children.Add(ClientUi.Alert(AlertTone.Danger, Loc.T("clients.repair.title", ("name", name)), lines));
        var fix = ClientUi.Button(Loc.T("clients.repair.action"), null, ButtonVariant.Primary);
        fix.HorizontalAlignment = HorizontalAlignment.Left;
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
        box.Children.Add(fix);
        return box;
    }

    // ---------- provider bindings (web ProviderBindings) ----------

    private void AddBindings(ClientInfoDto client, Query<List<ProviderDto>> providers, bool soon)
    {
        var saved = ClientsLogic.SavedBindings(client);
        var savedKey = string.Join("\n", saved.Select(b => b.ProviderId + "|" + b.AccountId));
        if (_orderKind != client.Kind || _orderSavedKey != savedKey)
        {
            _orderKind = client.Kind;
            _orderSavedKey = savedKey;
            _order = saved;
        }
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

        List<Control> AvailableRows() => available.Select(p =>
        {
            var row = new SettingsRow { Icon = BrandIcon.Provider(p.Name, p.Icon, 24), Label = p.Name, Detail = ClientsLogic.ProviderDetail(p, T) };
            if (!soon) row.Trailing.Add(ClientUi.Icon(LucideIconKind.Plus, 16, ArcDesignTokenKind.TextSecondary));
            if (!busy) row.Invoked += (_, _) => _ = Save([.. _order, new ClientBindingDto(p.Id, null)], Loc.T("clients.providerAdded", ("provider", p.Name)));
            return (Control)row;
        }).ToList();

        var group = new SettingsGroup { Title = Loc.T("clients.providerTitle"), Footer = Loc.T("clients.providerFooter") };
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
                var grip = ClientUi.Icon(LucideIconKind.GripVertical, 16, ArcDesignTokenKind.TextMuted);
                grip.IsHitTestVisible = true;
                var dragLabel = Loc.T("clients.dragToReorder", ("provider", provider.Name));
                var gripBox = new Border { Child = grip, Background = Brushes.Transparent, Margin = new Thickness(-4, 0, 0, 0), Opacity = busy ? 0.5 : 1 };
                ToolTip.SetTip(gripBox, dragLabel);
                Avalonia.Automation.AutomationProperties.SetName(gripBox, dragLabel);
                var label = ClientUi.HRow(8, KitStyle.Text(provider.Name, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground));
                if (i == 0) label.Children.Add(KitBadge.Create(Loc.T("clients.primaryProvider"), Tone.Accent));
                if (!provider.Enabled) label.Children.Add(KitBadge.Create(Loc.T("clients.providerDisabledBadge"), Tone.Orange));
                var row = new SettingsRow
                {
                    Icon = ClientUi.HRow(8, gripBox, BrandIcon.Provider(provider.Name, provider.Icon, 24)),
                    Label = label,
                    Detail = ClientsLogic.ProviderDetail(provider, T),
                };
                if (bound.Count > 1)
                {
                    var removeLabel = Loc.T("clients.removeProvider", ("provider", provider.Name));
                    var p = provider;
                    var remove = ClientUi.Button(null, () => _ = Save(_order.Where(b => b.ProviderId != p.Id).ToList(), Loc.T("clients.providerRemoved", ("provider", p.Name))),
                        ButtonVariant.Ghost, small: true, icon: ClientUi.Icon(LucideIconKind.X), enabled: !busy);
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
        if (!loading && bound.Count == 0)
            foreach (var row in AvailableRows()) group.Rows.Add(row);
        _root.Children.Add(group);

        if (bound.Count > 0 && available.Count > 0)
        {
            var more = new SettingsGroup { Title = Loc.T("clients.moreProvidersTitle") };
            foreach (var row in AvailableRows()) more.Rows.Add(row);
            _root.Children.Add(more);
        }
    }

    // ---------- version (web VersionGroup) ----------

    private void AddVersionGroup(ClientInfoDto client, ClientInstallDto inst)
    {
        var job = _watch.Use(_vm.Api.InstallJob(client.Kind));
        var shadowing = InstallLogic.Shadowing(inst);
        if (shadowing is not null)
        {
            var alert = ClientUi.Alert(AlertTone.Warning, Loc.T("clients.shadowed.title", ("version", shadowing.Version)),
                Loc.T("clients.shadowed.detail", ("path", inst.Executable ?? ""), ("version", inst.Version ?? ""), ("other", shadowing.Path), ("dir", ClientsLogic.DirnameOf(shadowing.Path))));
            alert.Margin = new Thickness(0, 0, 0, 12);
            _shadowAnchor = alert;
            _root.Children.Add(alert);
        }
        else if (inst.NotOnPath && inst.Executable is { } exe)
        {
            var alert = ClientUi.Alert(AlertTone.Warning, Loc.T("clients.notOnPath.title"), Loc.T("clients.notOnPath.detail", ("dir", ClientsLogic.DirnameOf(exe))));
            alert.Margin = new Thickness(0, 0, 0, 12);
            _root.Children.Add(alert);
        }

        Control Value(string text, bool muted)
        {
            var block = new SelectableTextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
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
                Icon = ClientUi.Icon(LucideIconKind.ScrollText, 16, ArcDesignTokenKind.TextSecondary),
                Label = Loc.T("clients.installLog"),
                Detail = j.Command + " · " + Loc.T("clients.job." + j.State),
            };
            log.Invoked += (_, _) => _vm.BeginInstall(client, InstallSheetMode.Log);
            group.Rows.Add(log);
        }
        _root.Children.Add(group);
    }
}
