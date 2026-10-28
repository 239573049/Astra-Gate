using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.Tray;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using static Astra.Desktop.Components.Tray.TrayUi;

namespace Astra.Desktop.Components.Tray;

/// <summary>
/// The tray panel (web components/tray/TrayPanelView.tsx): service status, today's usage, subscription quota windows and service controls.
/// The same view is the tray popup's content and the live preview on Settings › Tray panel (<see cref="Preview"/>: no close button, the
/// buttons are inert). The overview tab leads with what needs attention (failures, a nearly empty quota), then today's numbers and the
/// tightest quota windows; the Quota / Clients tabs hold the full lists. <see cref="TrayPrefs.Sections"/> decides which blocks (and tabs)
/// appear. All decisions come from <see cref="TrayPanelLogic"/>; this class draws them. It holds no inputs, so it simply redraws on every update.
/// </summary>
public sealed class TrayPanelView : UserControl
{
    private readonly Border _card;
    private readonly Border _header = new();
    private readonly Border _tabs = new();
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _body = new() { Spacing = 12, Margin = new Thickness(16, 14) };
    private readonly Border _footer = new();
    private TrayPrefs _prefs = TrayPrefs.Default;
    private TrayPanelState _state = new(false, null, null, false, null);
    private TrayData _data = TrayData.Empty;
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private TimeSpan _zone = TimeSpan.Zero;
    private TrayTab _tab = TrayTab.Overview;
    private double _lastHeight;

    /// <param name="preview">Settings preview: no close button, no fixed height, commands inert unless handlers are set.</param>
    public TrayPanelView(bool preview = false)
    {
        Preview = preview;
        _scroll = new ScrollViewer { Content = _body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var layout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_header, Dock.Top);
        DockPanel.SetDock(_tabs, Dock.Top);
        DockPanel.SetDock(_footer, Dock.Bottom);
        layout.Children.Add(_header);
        layout.Children.Add(_tabs);
        layout.Children.Add(_footer);
        layout.Children.Add(_scroll);
        _card = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), ClipToBounds = true, Child = layout };
        _card.Token(Border.BorderBrushProperty, ArcDesignTokenKind.BorderStrong).Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceRaised);
        _header.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border);
        _header.BorderThickness = new Thickness(0, 0, 0, 1);
        _header.Padding = new Thickness(16, 14, 16, 12);
        _footer.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border);
        _footer.BorderThickness = new Thickness(0, 1, 0, 0);
        _footer.Padding = new Thickness(12, 10);
        Content = _card;
        LayoutUpdated += (_, _) => ReportHeight();
    }

    /// <summary>The Settings preview flavor.</summary>
    public bool Preview { get; }

    /// <summary>A header / footer button (web <c>onCommand</c>); null in the preview.</summary>
    public Action<TrayCommand>? Command { get; set; }

    /// <summary>The refresh button.</summary>
    public Action? RefreshRequested { get; set; }

    /// <summary>The copy-address button.</summary>
    public Action? CopyAddress { get; set; }

    /// <summary>A client row was clicked (switch its provider); null = rows are not clickable.</summary>
    public Action<ClientInfoDto>? SwitchClient { get; set; }

    /// <summary>The tab changed (the popup resets it to the overview on every opening).</summary>
    public Action<TrayTab>? TabChanged { get; set; }

    /// <summary>The height the card wants: header + tabs + the body's full content (not its scroll box) + footer + border (TrayPanelPage's measurement).</summary>
    public event Action<double>? ContentHeightChanged;

    /// <summary>The selected tab (controlled by the popup).</summary>
    public TrayTab Tab
    {
        get => _tab;
        set
        {
            if (_tab == value) return;
            _tab = value;
            Render();
        }
    }

    /// <summary>Shows new prefs, state and data; <paramref name="now"/> / <paramref name="zone"/> are the page clock (chart hour, "updated" text).</summary>
    public void Update(TrayPrefs prefs, TrayPanelState state, TrayData data, DateTimeOffset now, TimeSpan zone)
    {
        _prefs = prefs;
        _state = state;
        _data = data;
        _now = now;
        _zone = zone;
        Render();
    }

    private void Fire(TrayCommand c) => Command?.Invoke(c);

    private void Select(TrayTab tab)
    {
        _tab = tab;
        TabChanged?.Invoke(tab);
        Render();
    }

    /// <summary>Redraws everything from the current inputs.</summary>
    public void Render()
    {
        var busy = _state.Activity is not null;
        var up = _state.Running && !busy;
        var tabs = TrayPanelLogic.Tabs(_prefs, _data);
        var active = TrayPanelLogic.Active(_tab, tabs);

        _header.Child = Header(busy);
        _tabs.Child = TrayPanelLogic.ShowTabs(_state, tabs) ? TabBar(tabs, active) : null;

        _body.Children.Clear();
        if (!_state.Running && !busy) _body.Children.Add(Stopped());
        else if (active == TrayTab.Overview)
        {
            if (Alerts() is { } alerts) _body.Children.Add(alerts);
            if (Usage() is { } usage) _body.Children.Add(usage);
            if (On(TraySection.Quota) && QuotaSummary() is { } quota) _body.Children.Add(quota);
        }
        else if (active == TrayTab.Quota)
        {
            if (QuotaBlock() is { } q) _body.Children.Add(q);
        }
        else if (ClientsBlock(!up) is { } c) _body.Children.Add(c);

        _footer.Child = Footer(busy);
    }

    private bool On(TraySection s) => _prefs.Sections.TryGetValue(s, out var on) && on;

    private void ReportHeight()
    {
        // DesiredSize already includes each part's margin.
        var height = _header.DesiredSize.Height + _tabs.DesiredSize.Height + _body.DesiredSize.Height
                     + _footer.DesiredSize.Height + _card.BorderThickness.Top + _card.BorderThickness.Bottom;
        if (Math.Abs(height - _lastHeight) < 0.5) return;
        _lastHeight = height;
        ContentHeightChanged?.Invoke(height);
    }

    // ---------- header ----------

    private Control Header(bool busy)
    {
        var dot = new StatusDot(busy ? Tone.Orange : _state.Running ? Tone.Green : Tone.Red);
        var status = Text(Loc.T(TrayPanelLogic.StatusKey(_state)), 14, weight: FontWeight.SemiBold);
        var address = TrayPanelLogic.Address(_data.Settings, _state.Port);
        var top = new DockPanel();
        var buttons = Row(0,
            IconButton(LucideIconKind.Copy, Loc.T("tray.copyAddress"), () => CopyAddress?.Invoke(), enabled: address.Length > 0),
            IconButton(LucideIconKind.Settings, Loc.T("tray.settings"), () => Fire(TrayCommand.OpenSettings)),
            IconButton(LucideIconKind.Ellipsis, Loc.T("tray.more"), () => Fire(TrayCommand.Menu)));
        if (!Preview) buttons.Children.Add(IconButton(LucideIconKind.X, Loc.T("common.close"), () => Fire(TrayCommand.Hide)));
        DockPanel.SetDock(dot, Dock.Left);
        DockPanel.SetDock(buttons, Dock.Right);
        dot.Margin = new Thickness(0, 0, 8, 0);
        top.Children.Add(dot);
        top.Children.Add(buttons);
        top.Children.Add(status);

        var column = new StackPanel { Children = { top } };
        if (address.Length > 0)
        {
            var line = Text(address, 12, ArcDesignTokenKind.TextSecondary);
            line.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
            line.Margin = new Thickness(16, 2, 0, 0);
            column.Children.Add(line);
        }
        if (_state.ApiVersionMismatch || _state.UpdateAvailable is not null)
        {
            var pills = new WrapPanel { Margin = new Thickness(16, 8, 0, 0), ItemSpacing = 6, LineSpacing = 6 };
            if (_state.ApiVersionMismatch)
                pills.Children.Add(Pill(Tone.Orange, Row(4, Icon(LucideIconKind.TriangleAlert, 12, ArcDesignTokenKind.Warning), Text(Loc.T("tray.mismatch"), 11.5, ArcDesignTokenKind.Warning))));
            if (_state.UpdateAvailable is not null)
                pills.Children.Add(Clickable(Pill(Tone.Accent, Text(Loc.T("tray.update"), 11.5, ArcDesignTokenKind.Accent)), () => Fire(TrayCommand.CheckUpdates), hoverFill: false));
            column.Children.Add(pills);
        }
        return column;
    }

    private static Control Pill(Tone tone, Control content) => Tinted(tone, content, 6, new Thickness(6, 1));

    // ---------- tabs ----------

    private Control TabBar(IReadOnlyList<TrayTab> tabs, TrayTab active)
    {
        var row = Row(4);
        row.Margin = new Thickness(12, 10, 12, 0);
        foreach (var tab in tabs)
        {
            var selected = tab == active;
            var label = Text(Loc.T(TrayPanelLogic.TabKey(tab)), 12.5, selected ? ArcDesignTokenKind.Foreground : ArcDesignTokenKind.TextSecondary, selected ? FontWeight.Medium : FontWeight.Normal);
            var button = Clickable(label, () => Select(tab), hoverFill: false);
            button.Height = 28;
            button.Padding = new Thickness(10, 0);
            button.CornerRadius = new CornerRadius(7);
            if (selected) Fill(button, ArcDesignTokenKind.SurfaceMuted);
            AutomationProperties.SetName(button, label.Text);
            row.Children.Add(button);
        }
        return row;
    }

    // ---------- body: stopped ----------

    private Control Stopped()
    {
        var hint = Text(Loc.T("tray.stoppedHint"), 12, ArcDesignTokenKind.TextSecondary);
        hint.TextWrapping = TextWrapping.Wrap;
        hint.TextTrimming = TextTrimming.None;
        hint.TextAlignment = TextAlignment.Center;
        hint.MaxWidth = 260;
        var start = AccentButton(Loc.T("shell.startService"), () => Fire(TrayCommand.Start));
        start.Margin = new Thickness(0, 4, 0, 0);
        var column = Column(8, Text(Loc.T("tray.status.stopped"), 13, weight: FontWeight.Medium), hint, start);
        column.Margin = new Thickness(0, 24);
        foreach (var c in column.Children) c.HorizontalAlignment = HorizontalAlignment.Center;
        return column;
    }

    private static Border AccentButton(string text, Action onClick)
    {
        var content = Row(6, Icon(LucideIconKind.Play, 14, ArcDesignTokenKind.AccentForeground), Text(text, 13, ArcDesignTokenKind.AccentForeground, FontWeight.Medium));
        var b = Clickable(content, onClick, text, hoverFill: false);
        b.Height = 32;
        b.Padding = new Thickness(12, 0);
        b.CornerRadius = new CornerRadius(8);
        Fill(b, ArcDesignTokenKind.Accent);
        return b;
    }

    // ---------- body: overview ----------

    private Control? Alerts()
    {
        var alerts = TrayPanelLogic.Alerts(_prefs, _data, (k, a) => Loc.T(k, a));
        if (alerts.Count == 0) return null;
        var column = Column(6);
        foreach (var a in alerts)
        {
            var tone = a.Critical ? Tone.Red : Tone.Orange;
            var color = ToneToken(tone);
            var line = new DockPanel();
            var icon = Icon(LucideIconKind.TriangleAlert, 14, color);
            icon.Margin = new Thickness(0, 0, 6, 0);
            DockPanel.SetDock(icon, Dock.Left);
            line.Children.Add(icon);
            if (a.OpensQuota)
            {
                var chevron = Icon(LucideIconKind.ChevronRight, 14, color);
                chevron.Margin = new Thickness(6, 0, 0, 0);
                DockPanel.SetDock(chevron, Dock.Right);
                line.Children.Add(chevron);
            }
            line.Children.Add(Text(a.Text, 12.5, color));
            var box = Tinted(tone, line, 8, new Thickness(10, 6));
            if (a.OpensQuota)
            {
                var click = Clickable(box, () => Select(TrayTab.Quota), a.Text, hoverFill: false);
                column.Children.Add(click);
            }
            else column.Children.Add(box);
        }
        return column;
    }

    private Control? Usage()
    {
        if (!TrayPanelLogic.HasUsage(_prefs)) return null;
        var s = _data.Summary;
        var head = new DockPanel();
        var refresh = IconButton(LucideIconKind.RefreshCw, Loc.T("common.refresh"), () => RefreshRequested?.Invoke(), glyph: 14);
        DockPanel.SetDock(refresh, Dock.Right);
        head.Children.Add(refresh);
        if (TrayMath.UpdatedAgo(_data.UpdatedAt, _now, Loc.T("tray.justNow")) is { } ago)
        {
            var updated = Text(Loc.T("tray.updated", ("time", ago)), 12, ArcDesignTokenKind.TextMuted);
            updated.Margin = new Thickness(8, 0);
            DockPanel.SetDock(updated, Dock.Right);
            head.Children.Add(updated);
        }
        head.Children.Add(Text(Loc.T("tray.today"), 13, ArcDesignTokenKind.TextSecondary));
        var section = Column(12, head);

        if (s is null)
        {
            if (_data.Error) section.Children.Add(Text(Loc.T("tray.loadFailed"), 12, ArcDesignTokenKind.Danger));
            else section.Children.Add(new LoadingState(2));
            return section;
        }
        var tiles = TrayPanelLogic.Tiles(_prefs, s);
        if (tiles.Count > 0) section.Children.Add(Tiles(tiles));
        if (On(TraySection.Chart)) section.Children.Add(HourlyChart());
        if (TrayPanelLogic.HasChips(_prefs))
        {
            var chips = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
            foreach (var chip in TrayPanelLogic.Chips(_prefs, _data, LocalTime.At(_now, _zone), k => Loc.T(k))) chips.Children.Add(Chip(chip));
            section.Children.Add(chips);
        }
        return section;
    }

    private static Control Tiles(List<(string LabelKey, string Value)> tiles)
    {
        // grid-flow-col auto-cols-fr gap-px over a border-colored, rounded, clipped background: hairline dividers between tiles.
        var grid = new Grid { ColumnSpacing = 1 };
        for (var i = 0; i < tiles.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var tile = new Border { Padding = new Thickness(12, 10), Child = Column(2, Text(Loc.T(tiles[i].LabelKey), 12, ArcDesignTokenKind.TextSecondary), Value(tiles[i].Value)) };
            tile.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
            Avalonia.Controls.Grid.SetColumn(tile, i);
            grid.Children.Add(tile);
        }
        var frame = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = grid };
        return frame.Token(Border.BackgroundProperty, ArcDesignTokenKind.Border);

        static TextBlock Value(string v)
        {
            var t = Text(v, 20, weight: FontWeight.SemiBold);
            t.LineHeight = 25;
            ToolTip.SetTip(t, v);
            return t;
        }
    }

    private Control HourlyChart()
    {
        var now = LocalTime.At(_now, _zone);
        var bars = TrayMath.HourlyRequests(_data.Hourly, now);
        var peak = Math.Max(1, bars.Count == 0 ? 1 : bars.Max(b => b.Value));
        var hour = now.Now.Hour;
        var grid = new Grid { Height = 64, ColumnSpacing = 3 };
        AutomationProperties.SetName(grid, Loc.T("tray.chartLabel"));
        for (var h = 0; h < bars.Count; h++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var b = bars[h];
            Border bar = h == hour ? new Border().Token(Border.BackgroundProperty, ArcDesignTokenKind.Accent)
                : b.Value > 0 ? new AlphaFill(ArcDesignTokenKind.Foreground, 0.32)
                : new Border().Token(Border.BackgroundProperty, ArcDesignTokenKind.Border);
            bar.Height = TrayPanelLogic.BarHeight(b.Value, peak);
            bar.VerticalAlignment = VerticalAlignment.Bottom;
            bar.CornerRadius = new CornerRadius(2);
            bar.Opacity = h > hour ? 0.5 : 1;
            ToolTip.SetTip(bar, Loc.T("tray.hourBar", ("hour", $"{h:00}:00"), ("count", b.Value)));
            Avalonia.Controls.Grid.SetColumn(bar, h);
            grid.Children.Add(bar);
        }
        return grid;
    }

    private static Control Chip(TrayChip chip)
    {
        var value = Row(4);
        if (chip.Up is { } up) value.Children.Add(Icon(up ? LucideIconKind.ArrowUpRight : LucideIconKind.ArrowDownRight, 12, ArcDesignTokenKind.Foreground));
        var main = Text(chip.Value, 12, weight: FontWeight.Medium);
        main.MaxWidth = 170;
        value.Children.Add(main);
        if (chip.Share is { } share) value.Children.Add(Text("· " + share, 12, weight: FontWeight.Medium));
        var box = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 2),
            Child = Row(6, Text(chip.Label, 12, ArcDesignTokenKind.TextSecondary), value),
        };
        if (chip.Tooltip is { } tip) ToolTip.SetTip(box, tip);
        return box.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border);
    }

    // ---------- quota ----------

    /// <summary>The overview's teaser: the few windows closest to running out; the Quota tab has everything.</summary>
    private Control? QuotaSummary()
    {
        if (_data.Plans.Count == 0)
            return _data.HasSubscriptions ? TopRule(Text(Loc.T("tray.quotaPending"), 12, ArcDesignTokenKind.TextMuted), 12) : null;
        var rows = TrayMath.TightestRows(_data.Plans, 3);
        var head = new DockPanel();
        if (TrayPanelLogic.RowCount(_data.Plans) > rows.Count)
        {
            var all = Clickable(Row(2, Text(Loc.T("tray.viewAll"), 12, ArcDesignTokenKind.TextSecondary), Icon(LucideIconKind.ChevronRight, 12, ArcDesignTokenKind.TextSecondary)),
                () => Select(TrayTab.Quota), hoverFill: false);
            DockPanel.SetDock(all, Dock.Right);
            head.Children.Add(all);
        }
        head.Children.Add(Text(Loc.T("tray.quotaTightest"), 12, ArcDesignTokenKind.TextSecondary));
        var column = Column(8, head);
        foreach (var r in rows)
        {
            var name = Row(6, BrandIcon.Provider(r.Plan.ProviderName, r.Plan.ProviderIcon, 14), Text(r.Plan.ProviderName, 12.5, weight: FontWeight.Medium),
                Text(TrayMath.RowName(r.Row), 12.5, ArcDesignTokenKind.TextSecondary));
            column.Children.Add(Columns("*,64,40,60", 8, [name, .. Cells(r.Row)]));
        }
        return TopRule(column, 12);
    }

    private Control? QuotaBlock()
    {
        if (_data.Plans.Count == 0 && !_data.HasSubscriptions) return null;
        var head = Columns("*,48,72", 8,
            Text(Loc.T("tray.quota"), 12, ArcDesignTokenKind.TextSecondary),
            Right(Text(Loc.T("tray.quotaRemaining"), 12, ArcDesignTokenKind.TextSecondary)),
            Right(Text(Loc.T("tray.quotaReset"), 12, ArcDesignTokenKind.TextSecondary)));
        var column = Column(8, head);
        if (_data.Plans.Count == 0) column.Children.Add(Text(Loc.T("tray.quotaPending"), 12, ArcDesignTokenKind.TextMuted));
        foreach (var plan in _data.Plans)
        {
            var title = Row(8, BrandIcon.Provider(plan.ProviderName, plan.ProviderIcon, 16), Text(plan.ProviderName, 13, weight: FontWeight.Medium));
            if (TrayPanelLogic.ShowAccount(_data.Plans, plan)) title.Children.Add(Text(plan.AccountName, 12, ArcDesignTokenKind.TextMuted));
            var group = Column(4, title);
            foreach (var row in plan.Rows)
            {
                var name = TrayMath.RowName(row);
                var label = Text(name, 12.5, ArcDesignTokenKind.TextSecondary);
                ToolTip.SetTip(label, name);
                var line = Columns("52,*,48,72", 8, [label, .. Cells(row)]);
                line.Margin = new Thickness(24, 0, 0, 0);
                group.Children.Add(line);
            }
            column.Children.Add(group);
        }
        return column;
    }

    /// <summary>Meter, remaining share and reset time of one row: three cells of the caller's grid (a balance's amount takes the last two).</summary>
    private Control[] Cells(TrayQuotaRow row)
    {
        Control meter = new Panel();
        if (row.Remaining is { } remaining)
        {
            var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
            track.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
            var fill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
            fill.Token(Border.BackgroundProperty, TrayMath.Tone(remaining) switch
            {
                QuotaTone.Red => ArcDesignTokenKind.Danger,
                QuotaTone.Orange => ArcDesignTokenKind.Warning,
                _ => ArcDesignTokenKind.Success,
            });
            track.Child = fill;
            track.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * Math.Clamp(remaining, 0, 100) / 100;
            meter = track;
        }
        if (row.Amount is { } amount)
        {
            var text = Right(Text(Astra.Desktop.Core.Providers.QuotaText.FormatAmount(amount.Value, amount.Unit), 12.5, weight: FontWeight.Medium));
            Avalonia.Controls.Grid.SetColumnSpan(text, 2);
            return [meter, text];
        }
        return
        [
            meter,
            Right(Text(row.Remaining is { } r ? TrayMath.Percent(r) : StatsFormat.Dash, 12.5, weight: FontWeight.Medium)),
            Right(Text(TrayMath.ResetsIn(row.ResetsAt, _now) ?? StatsFormat.Dash, 12.5, ArcDesignTokenKind.TextSecondary)),
        ];
    }

    private static TextBlock Right(TextBlock t)
    {
        t.TextAlignment = TextAlignment.Right;
        t.HorizontalAlignment = HorizontalAlignment.Stretch;
        return t;
    }

    // ---------- clients ----------

    private Control? ClientsBlock(bool disabled)
    {
        var clients = _data.Clients.Where(c => c.Enabled).ToList();
        if (clients.Count == 0) return null;
        var heading = Text(Loc.T("tray.clients"), 12, ArcDesignTokenKind.TextSecondary);
        heading.Margin = new Thickness(0, 0, 0, 2);
        var column = Column(4, heading);
        foreach (var c in clients)
        {
            var line = new DockPanel();
            if (TrayBrands.IsKnown(c.Kind))
            {
                var glyph = BrandIcon.Client(c.Kind, 16, glyph: true);
                glyph.Margin = new Thickness(0, 0, 8, 0);
                DockPanel.SetDock(glyph, Dock.Left);
                line.Children.Add(glyph);
            }
            if (SwitchClient is not null)
            {
                var chevron = Icon(LucideIconKind.ChevronsUpDown, 14, ArcDesignTokenKind.TextMuted);
                chevron.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(chevron, Dock.Right);
                line.Children.Add(chevron);
            }
            var provider = Text(TrayPanelLogic.ProviderName(_data.Providers, c.ProviderId, Loc.T("tray.unbound")), 12, ArcDesignTokenKind.TextSecondary);
            provider.MaxWidth = 150;
            provider.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(provider, Dock.Right);
            line.Children.Add(provider);
            line.Children.Add(Text(c.Name, 13));
            var client = c;
            var row = Clickable(line, SwitchClient is null || disabled ? null : () => SwitchClient?.Invoke(client), Loc.T("tray.switchProvider", ("client", c.Name)), enabled: !disabled || SwitchClient is null);
            row.Height = 32;
            row.Padding = new Thickness(8, 0);
            row.Margin = new Thickness(-8, 0);
            row.CornerRadius = new CornerRadius(8);
            column.Children.Add(row);
        }
        return column;
    }

    // ---------- footer ----------

    private Control Footer(bool busy)
    {
        var dock = new DockPanel();
        var left = Row(4);
        if (_state.Running)
        {
            left.Children.Add(IconButton(LucideIconKind.RotateCcw, Loc.T("tray.restart"), () => Fire(TrayCommand.Restart), enabled: !busy));
            left.Children.Add(IconButton(LucideIconKind.Ban, Loc.T("tray.stop"), () => Fire(TrayCommand.Stop), enabled: !busy));
        }
        else left.Children.Add(IconButton(LucideIconKind.Play, Loc.T("shell.startService"), () => Fire(TrayCommand.Start), enabled: !busy));
        DockPanel.SetDock(left, Dock.Left);
        dock.Children.Add(left);

        var open = Clickable(Row(6, Text(Loc.T("tray.open"), 13, weight: FontWeight.Medium), Icon(LucideIconKind.ArrowUpRight, 14, ArcDesignTokenKind.Foreground)),
            () => Fire(TrayCommand.Open), Loc.T("tray.open"));
        open.Height = 32;
        open.Padding = new Thickness(12, 0);
        open.CornerRadius = new CornerRadius(8);
        open.BorderThickness = new Thickness(1);
        open.Token(Border.BorderBrushProperty, ArcDesignTokenKind.BorderStrong);
        DockPanel.SetDock(open, Dock.Right);

        var quit = Clickable(Text(Loc.T("tray.quit"), 13, ArcDesignTokenKind.TextSecondary), () => Fire(TrayCommand.Quit), Loc.T("tray.quit"));
        quit.Height = 32;
        quit.Padding = new Thickness(10, 0);
        quit.CornerRadius = new CornerRadius(8);
        quit.Margin = new Thickness(0, 0, 4, 0);
        DockPanel.SetDock(quit, Dock.Right);

        dock.Children.Add(open);
        dock.Children.Add(quit);
        dock.Children.Add(new Panel());
        return dock;
    }
}

/// <summary>Whether the brand catalog has a client glyph (web <c>c.kind in CLIENT_META</c>).</summary>
internal static class TrayBrands
{
    public static bool IsKnown(string kind) => BrandCatalog.Client(kind) is not null;
}
