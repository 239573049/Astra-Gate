using Astra.Api.Contracts;
using Astra.Core;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Accounts;
using Astra.Desktop.Core.I18n;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Components.Accounts;

/// <summary>
/// One account card (web SubscriptionAccountCard.tsx): a top band tinted by the plan family with the status, the proxy mark and the
/// "more" menu; then the name, the kind / source / current line, the one-line status (expiry, cooldown, failure), the compact usage table
/// and, at the bottom, the quota strip — or "signed out" with a re-login button for a lapsed login. A click opens the detail, or toggles
/// the selection once some card is selected; the check box shows on hover and while selecting. The card redraws only when what it shows
/// changed (the usage poll answers every 5 s with equal data), so an open menu is not closed by a poll.
/// </summary>
internal sealed class SubscriptionAccountCardView : UserControl
{
    private readonly SubscriptionAccountsModel _model;
    private readonly Border _surface;
    private string _signature = "";
    private AccountCardModel? _card;
    private bool _hover;
    private bool _selected;
    private bool _selecting;
    private Control? _check;
    private Control? _order;

    /// <summary>Creates an empty card; <see cref="Update"/> fills it.</summary>
    public SubscriptionAccountCardView(SubscriptionAccountsModel model)
    {
        _model = model;
        _surface = new Border { MinHeight = 292, ClipToBounds = true, Cursor = new Cursor(StandardCursorType.Hand) };
        _surface.Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusPanel);
        Content = _surface;
        PointerEntered += (_, _) => { _hover = true; Paint(); };
        PointerExited += (_, _) => { _hover = false; Paint(); };
        _surface.PointerPressed += OnPressed;
    }

    /// <summary>The account shown.</summary>
    public string? AccountId => _card?.Account.Id;

    /// <summary>Shows <paramref name="card"/>; redraws only when something it shows changed.</summary>
    public void Update(AccountCardModel card, ProviderAccountUsageDto? usage, bool selected, bool selecting, bool quotaPending)
    {
        var signature = string.Join('|', Json.SerializeApi(card.Account), usage is null ? "" : Json.SerializeApi(usage), card.Index, card.Menu.Count,
            selected, selecting, quotaPending, card.State, Loc.Language);
        _card = card;
        _selected = selected;
        _selecting = selecting;
        if (signature == _signature) return;
        _signature = signature;
        Build(card, usage, quotaPending);
        Paint();
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_card is null || e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // A press on the check box, the menu or a button belongs to that control.
        for (var v = e.Source as Visual; v is not null && v != _surface; v = v.GetVisualParent())
            if (v is Avalonia.Controls.Primitives.ToggleButton or Avalonia.Controls.Button or DropdownMenu) return;
        e.Handled = true;
        _model.Open(_card.Account);
    }

    private void Paint()
    {
        if (_card is null) return;
        var a = _card.Account;
        // web: hover:bg-[var(--fill)], selected bg-[var(--accent-subtle)], current ring-1 ring-[var(--accent)], disabled opacity-55.
        _surface.Token(Border.BackgroundProperty, _selected ? ArcDesignTokenKind.AccentSubtle : _hover ? ArcDesignTokenKind.SurfaceMuted : ArcDesignTokenKind.Surface);
        _surface.BorderThickness = new Thickness(a.IsCurrent ? 1 : 0.5);
        _surface.Token(Border.BorderBrushProperty, a.IsCurrent ? ArcDesignTokenKind.Accent : ArcDesignTokenKind.Border);
        Opacity = _card.State == AccountState.Disabled ? 0.55 : 1;
        // web: hidden group-hover:flex — the box takes no room until hovered, or while selecting.
        if (_check is not null) _check.IsVisible = _selecting || _selected || _hover;
        if (_order is not null) _order.Opacity = _hover ? 1 : 0;
    }

    private void Build(AccountCardModel card, ProviderAccountUsageDto? usage, bool quotaPending)
    {
        var a = card.Account;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(Band(card));
        var body = Body(card, usage, quotaPending);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        _surface.Child = root;
        Avalonia.Automation.AutomationProperties.SetName(this, card.Name);
        _ = a;
    }

    // Top band: the plan family's gradient (34 % to 8 % alpha at 135°), status, proxy mark, menu, and the plan name in 17 px bold.
    private Control Band(AccountCardModel card)
    {
        var a = card.Account;
        var (r, g, b) = card.PlanRgb;
        var band = new Border
        {
            Padding = new Thickness(12, 10, 12, 12),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(87, r, g, b), 0), new GradientStop(Color.FromArgb(20, r, g, b), 1) },
            },
        };
        var stack = new StackPanel();
        // web: the row is as tall as the iconOnly Menu trigger (a 28 px sm button), which sets the band's height.
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto"), ColumnSpacing = 6, MinHeight = 28 };

        // ARC-GAP: Arc's check box is a 32 px hit area (ControlSizeMD) around its 18 px square; the web's sits in a 20 px slot, so the
        // control hangs 6 px over each side of the slot to keep the square centred in it.
        var box = new Checkbox { IsChecked = _selected, MinHeight = 0, MinWidth = 0, Margin = new Thickness(-6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        Avalonia.Automation.AutomationProperties.SetName(box, Loc.T("providers.accounts.select", ("name", card.Name)));
        box.IsCheckedChanged += (_, _) =>
        {
            if ((box.IsChecked == true) != _model.Selected.Contains(a.Id)) _model.ToggleSelect(a.Id);
        };
        _check = new Border { Width = 20, Height = 20, Child = box };
        top.Children.Add(_check);

        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
        dot.Token(Border.BackgroundProperty, AccountsUi.TokenOf(AccountRules.DotTone(card.State)));
        status.Children.Add(dot);
        status.Children.Add(AccountsUi.Text(card.StatusText, 11, ArcDesignTokenKind.TextSecondary));
        Grid.SetColumn(status, 1);
        top.Children.Add(status);

        if (!string.IsNullOrEmpty(a.HttpProxy))
        {
            var globe = AccountsUi.Icon(LucideIconKind.Globe, 14, ArcDesignTokenKind.TextMuted);
            ToolTip.SetTip(globe, $"{Loc.T("providers.accounts.proxyBadge")}: {a.HttpProxy}");
            Avalonia.Automation.AutomationProperties.SetName(globe, Loc.T("providers.accounts.proxyBadge"));
            Grid.SetColumn(globe, 2);
            top.Children.Add(globe);
        }

        var menu = AccountMenu.Create(_model, card);
        menu.Margin = new Thickness(0, -4, -6, -4);
        Grid.SetColumn(menu, 4);
        top.Children.Add(menu);
        stack.Children.Add(top);

        var plan = AccountsUi.Text(card.Headline, 17, AccountsUi.Inline, FontWeight.Bold);
        plan.LetterSpacing = 0.17;
        plan.Margin = new Thickness(0, 6, 0, 0);
        plan.LineHeight = 17 * 1.4; // inherited --leading-body (arc tabs .content line-height)
        ToolTip.SetTip(plan, card.Headline);
        stack.Children.Add(plan);
        band.Child = stack;

        if (!card.HasEdge) return band;
        // Cooling / lapsed: a 3 px edge of the state's color down the band's left side.
        var edge = new Border { Width = 3, HorizontalAlignment = HorizontalAlignment.Left };
        edge.Token(Border.BackgroundProperty, AccountsUi.TokenOf(AccountRules.DotTone(card.State)));
        return new Panel { Children = { band, edge } };
    }

    private Control Body(AccountCardModel card, ProviderAccountUsageDto? usage, bool quotaPending)
    {
        var a = card.Account;
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto"), Margin = new Thickness(12, 10, 12, 12) };

        var name = AccountsUi.Text(card.Name, 13, AccountsUi.Inline, FontWeight.Medium);
        ToolTip.SetTip(name, card.Name);
        grid.Children.Add(name);

        // Kind line: ● current · kind · source, and the failover position on hover at the right.
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 2, 0, 0) };
        var parts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (a.IsCurrent)
        {
            parts.Children.Add(AccountsUi.Text("● " + Loc.T("providers.subscription.current"), 11, ArcDesignTokenKind.Success));
            parts.Children.Add(AccountsUi.Text("·", 11, ArcDesignTokenKind.TextMuted));
        }
        parts.Children.Add(AccountsUi.Text(card.KindText, 11, ArcDesignTokenKind.TextMuted));
        line.Children.Add(parts);
        if (card.SourceText is { } source)
        {
            var src = AccountsUi.Text("· " + source, 11, ArcDesignTokenKind.TextMuted);
            src.Margin = new Thickness(4, 0, 0, 0);
            Grid.SetColumn(src, 1);
            line.Children.Add(src);
        }
        var order = AccountsUi.Text(card.OrderText, 11, ArcDesignTokenKind.TextMuted);
        order.Margin = new Thickness(4, 0, 0, 0);
        _order = order;
        Grid.SetColumn(order, 2);
        line.Children.Add(order);
        Grid.SetRow(line, 1);
        grid.Children.Add(line);

        var meta = new Border { MinHeight = 28, Margin = new Thickness(0, 6, 0, 0) };
        if (card.ShowMeta && card.Meta is { } text)
        {
            var block = AccountsUi.Text(text, 11, AccountsUi.TokenOf(card.MetaTone), wrap: true);
            block.MaxLines = 2;
            block.TextTrimming = TextTrimming.CharacterEllipsis;
            block.LineHeight = 15.125; // leading-snug
            ToolTip.SetTip(block, text);
            meta.Child = block;
        }
        Grid.SetRow(meta, 2);
        grid.Children.Add(meta);

        var stats = new Border { Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(0, 8, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0) };
        stats.Token(Border.BorderBrushProperty, ArcDesignTokenKind.BorderSubtle);
        stats.Child = new AccountUsageStatsView(usage, compact: true, valueColor: AccountsUi.Inline);
        Grid.SetRow(stats, 3);
        grid.Children.Add(stats);

        Control? bottom = card.State == AccountState.Dead ? SignedOut(card) : QuotaStrip(card.Quota, quotaPending);
        if (bottom is not null)
        {
            Grid.SetRow(bottom, 5);
            grid.Children.Add(bottom);
        }
        return grid;
    }

    private Control SignedOut(AccountCardModel card)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(AccountsUi.Text(Loc.T("providers.accounts.signedOut"), 11, ArcDesignTokenKind.TextMuted));
        if (!card.IsKey)
        {
            var relogin = AccountsUi.Button(Loc.T("providers.subscription.relogin"), () => _ = _model.StartLoginAsync(card.Account.Id));
            Grid.SetColumn(relogin, 1);
            row.Children.Add(relogin);
        }
        return row;
    }

    // Up to two segmented bars; the reset time is the tooltip (web QuotaStrip).
    private static Control? QuotaStrip(IReadOnlyList<QuotaBar> bars, bool pending)
    {
        if (bars.Count == 0) return null;
        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Opacity = pending ? 0.6 : 1 };
        foreach (var bar in bars)
        {
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            head.Children.Add(AccountsUi.Text(bar.Label, 11, ArcDesignTokenKind.TextSecondary));
            var pct = AccountsUi.Text($"{bar.Percent}%", 11, ArcDesignTokenKind.TextSecondary, mono: true);
            Grid.SetColumn(pct, 1);
            head.Children.Add(pct);
            var item = new StackPanel { Background = Brushes.Transparent, Children = { head, AccountsUi.Segments(bar) } };
            if (bar.Reset is not null) ToolTip.SetTip(item, bar.Reset);
            stack.Children.Add(item);
        }
        return stack;
    }
}

/// <summary>The "more" menu of an account card (web <c>Menu iconOnly</c> with the card's entries).</summary>
internal static class AccountMenu
{
    /// <summary>The Lucide icon of a menu entry (web: the entries' icons; "relogin" has none).</summary>
    public static LucideIconKind? IconOf(string id) => id switch
    {
        "activate" => LucideIconKind.ArrowRightLeft,
        "toggle" => LucideIconKind.Power,
        "up" => LucideIconKind.ArrowUp,
        "down" => LucideIconKind.ArrowDown,
        "quota" => LucideIconKind.Gauge,
        "refresh" => LucideIconKind.RotateCcw,
        "remove" => LucideIconKind.Trash2,
        _ => null,
    };

    /// <summary>The card's ellipsis menu.</summary>
    public static DropdownMenu Create(SubscriptionAccountsModel model, AccountCardModel card)
    {
        var trigger = new TKCode.Arc.Button { Variant = ButtonVariant.Ghost, Size = ControlSize.Small, Width = 28, Padding = new Thickness(0), IsPressScaleEnabled = false };
        trigger.Content = AccountsUi.Icon(LucideIconKind.Ellipsis, 16);
        Avalonia.Automation.AutomationProperties.SetName(trigger, Loc.T("common.more"));
        var menu = new DropdownMenu { Label = Loc.T("common.more"), Alignment = MenuAlignment.End, Trigger = trigger, VerticalAlignment = VerticalAlignment.Center };
        foreach (var item in card.Menu)
        {
            var entry = new MenuEntry { Header = item.Label, IsEnabled = item.Enabled, IsDestructive = item.Destructive, HasSeparatorBefore = item.SeparatorBefore };
            if (IconOf(item.Id) is { } icon) entry.Icon = Lucide.Geometry(icon);
            var id = item.Id;
            entry.Click += (_, _) => _ = model.RunAsync(id, card.Account, card.Index);
            menu.Items.Add(entry);
        }
        return menu;
    }
}
