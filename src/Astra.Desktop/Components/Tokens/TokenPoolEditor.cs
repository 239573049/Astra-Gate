using System.Collections.ObjectModel;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Tokens;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;
using ArcSwitch = TKCode.Arc.Switch;

namespace Astra.Desktop.Components.Tokens;

/// <summary>What the pool editor reads from the app: the providers, the saved pool's live health, accounts and the health wording.</summary>
/// <param name="Providers">Every provider (disabled ones included, so a disabled member still shows).</param>
/// <param name="Health">Live health of the saved pool's members, by provider id.</param>
/// <param name="Accounts">The accounts query of an account-based provider.</param>
/// <param name="HealthText">The health badge text for a state and its "until" instant.</param>
public sealed record PoolEditorContext(
    IReadOnlyList<ProviderDto> Providers,
    IReadOnlyDictionary<string, PoolMemberHealthDto> Health,
    Func<string, Query<List<ProviderAccountDto>>> Accounts,
    Func<string, DateTimeOffset?, string> HealthText);

/// <summary>
/// The provider pool of a token's direct calls (web <c>TokenPoolEditor</c>): strategy, members in order (drag a grip to reorder),
/// their weight / tier / account and live health, the expected traffic share, sticky sessions and failover. One member is a plain
/// direct provider. The logic (drafts, shares, tones) is <c>Astra.Desktop.Core.Tokens.PoolDrafts</c>; this draws it.
/// Reordering is Arc's <see cref="ReorderableList"/>: a row moves by its grip only, and the weight / tier / account controls in a row
/// stay usable (plan inventory GAP-5 / GAP-8: the list takes an item template with interactive controls). The list is rebuilt only
/// when the member set or the strategy changes, never while a value is being typed.
/// </summary>
public sealed class TokenPoolEditor : UserControl
{
    private static readonly string[] ShareColors = ["", "#34c759", "#ff9f0a", "#af52de", "#5ac8fa", "#ff2d55"];

    private readonly PoolEditorContext _ctx;
    private readonly Action<PoolDraft> _changed;
    private readonly StackPanel _root = new() { Spacing = 12 };
    private PoolDraft _draft;
    private Control? _shareSlot;
    private ObservableCollection<MemberRow> _rows = [];

    /// <summary>One editable row of the member list.</summary>
    public sealed class MemberRow(PoolDraftMember member)
    {
        /// <summary>The provider's id (the row's identity).</summary>
        public string ProviderId { get; } = member.ProviderId;

        /// <summary>The pinned account, null = the provider's default.</summary>
        public string? AccountId { get; set; } = member.AccountId;

        /// <summary>Relative weight (at least 1).</summary>
        public int Weight { get; set; } = member.Weight;

        /// <summary>Priority tier (1 first).</summary>
        public int Tier { get; set; } = member.Tier;
    }

    /// <summary>Builds the editor for <paramref name="draft"/>; <paramref name="changed"/> receives the new draft after every edit.</summary>
    public TokenPoolEditor(PoolDraft draft, PoolEditorContext context, Action<PoolDraft> changed)
    {
        _draft = draft;
        _ctx = context;
        _changed = changed;
        Content = _root;
        Rebuild();
    }

    /// <summary>The draft as edited so far.</summary>
    public PoolDraft Draft => _draft;

    private Translate T => (key, args) => Loc.T(key, args);

    private bool Pooled => _draft.Members.Count > 1;

    private ProviderDto? Provider(string id) => _ctx.Providers.FirstOrDefault(p => p.Id == id);

    private string NameOf(string id) => Provider(id)?.Name ?? id;

    private void Set(PoolDraft next, bool rebuild)
    {
        _draft = next;
        _changed(next);
        if (rebuild) Rebuild();
        else RefreshShares();
    }

    private void SyncMembers() => _draft = _draft with
    {
        Members = _rows.Select(r => new PoolDraftMember(r.ProviderId, r.AccountId, r.Weight, r.Tier)).ToList(),
    };

    private void Rebuild()
    {
        _root.Children.Clear();
        _rows = new ObservableCollection<MemberRow>(_draft.Members.Select(m => new MemberRow(m)));

        if (Pooled)
        {
            var strategy = Pick.Segmented(
                PoolStrategies.All.Select(s => new SegmentedOption(s, Loc.T(PoolStrategies.NameKey(s)))), _draft.Strategy,
                s => Set(_draft with { Strategy = s }, true), Loc.T("tokens.pool.strategy"));
            strategy.HorizontalAlignment = HorizontalAlignment.Stretch; // a grid item stretches: the web's track spans the editor
            _root.Children.Add(new StackPanel
            {
                Spacing = 6,
                Children = { strategy, Muted(Loc.T(PoolStrategies.HintKey(_draft.Strategy)), secondary: true) },
            });
        }

        if (_draft.Members.Count == 0) _root.Children.Add(Muted(Loc.T("tokens.pool.empty")));
        else _root.Children.Add(MemberList());

        var addable = _ctx.Providers.Where(p => p.Enabled && !_draft.Members.Any(m => m.ProviderId == p.Id)).ToList();
        if (addable.Count > 0)
        {
            var add = Pick.Select(addable.Select(p => (p.Id, p.Name)), null,
                id => Set(_draft with { Members = [.. _draft.Members, new PoolDraftMember(id, null, 1, 1)] }, true),
                Loc.T("tokens.pool.add"), Loc.T("tokens.pool.add"));
            _root.Children.Add(add);
        }

        var noModels = _draft.Members.Select(m => Provider(m.ProviderId)).Where(p => p is { ModelCount: 0 }).Select(p => p!.Name).ToList();
        if (noModels.Count > 0)
        {
            var warn = Muted(Loc.T("tokens.pool.noModels", ("providers", string.Join(", ", noModels))));
            warn.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Warning);
            _root.Children.Add(warn);
        }

        if (Pooled)
        {
            _shareSlot = new ContentControl();
            _root.Children.Add(_shareSlot);
            RefreshShares();
            _root.Children.Add(Toggles());
        }
    }

    // ---------- members ----------

    private Control MemberList()
    {
        var list = new ReorderableList
        {
            Label = Loc.T("tokens.pool.title"),
            ItemsSource = _rows,
            ItemTitleSelector = row => NameOf(((MemberRow)row).ProviderId),
            ItemTemplate = new FuncDataTemplate<MemberRow>((row, _) => row is null ? null : MemberView(row), supportsRecycling: false),
        };
        Pick.ContainBringIntoView(list);
        // ARC-GAP: Arc draws each row as a 56px raised card with a 40px handle tile and a "01" position; the web's rows are plain
        // dividers in one bordered box (TokenPoolEditor.tsx: border border-subtle rounded, rows px-2.5 py-2 border-b, a bare 16px grip).
        // The template parts are reset per row as it is templated, and the rows stack without Arc's 8px gap.
        list.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Spacing = 0 });
        list.ContainerPrepared += (_, e) =>
        {
            if (e.Container is Avalonia.Controls.Primitives.TemplatedControl row) row.TemplateApplied += (_, a) => CompactRow(a.NameScope);
        };
        list.Reordered += (_, _) =>
        {
            SyncMembers();
            _changed(_draft);
            // The primary / backup badges and the share order depend on the order: redraw once the drop has settled.
            Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
        };
        // The last row's divider is hidden under the box's own edge (web last:border-b-0).
        list.Margin = new Thickness(0, 0, 0, -1);
        var box = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = list };
        box.Token(Border.BorderBrushProperty, ArcDesignTokenKind.BorderSubtle);
        return box;
    }

    private static void CompactRow(Avalonia.Controls.INameScope scope)
    {
        if (scope.Find<Border>("PART_Row") is { } rowBorder)
        {
            rowBorder.Background = Brushes.Transparent;
            rowBorder.BorderThickness = new Thickness(0, 0, 0, 1);
            rowBorder.Token(Border.BorderBrushProperty, ArcDesignTokenKind.BorderSubtle);
            rowBorder.CornerRadius = default;
            rowBorder.BoxShadow = default;
            rowBorder.MinHeight = 0;
            rowBorder.Padding = new Thickness(10, 8);
            // the position counter ("01") is the Arc row's third column; the web has none
            if (rowBorder.Child is Grid grid)
                foreach (var child in grid.Children)
                    if (child is TKCode.Arc.Primitives.ArcContentSwap) child.IsVisible = false;
        }
        if (scope.Find<Decorator>("PART_Handle") is { } handle)
        {
            handle.Width = 16;
            handle.Height = 16;
            handle.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        }
        if (scope.Find<Control>("PART_ContentPresenter") is { } content) content.Margin = new Thickness(8, 0, 0, 0);
    }

    private Control MemberView(MemberRow row)
    {
        var provider = Provider(row.ProviderId);
        var name = provider?.Name ?? row.ProviderId;
        var index = _rows.IndexOf(row);

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (provider is not null) left.Children.Add(BrandIcon.Provider(provider.Name, provider.Icon, 20));
        var title = KitStyle.Text(name, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium);
        left.Children.Add(title);
        if (Pooled && _draft.Strategy == PoolStrategies.Failover)
            left.Children.Add(KitBadge.Create(index == 0 ? Loc.T("tokens.pool.primary") : Loc.T("tokens.pool.backup", ("n", index)), index == 0 ? Tone.Accent : Tone.Neutral));
        var health = PoolDrafts.EffectiveState(provider, _ctx.Health.GetValueOrDefault(row.ProviderId));
        if (health is not null)
        {
            var until = _ctx.Health.GetValueOrDefault(row.ProviderId)?.Until;
            left.Children.Add(KitBadge.Create(_ctx.HealthText(health, until), ToTone(PoolDrafts.ToneOf(health))));
        }

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        if (Pooled && _draft.Strategy is PoolStrategies.Weighted or PoolStrategies.Tiered)
        {
            var weight = new NumberField
            {
                Value = row.Weight, Minimum = 1, Step = 1, Width = 84, Size = ControlSize.Small, Label = Loc.T("tokens.pool.weight"), IsLabelVisible = false,
                Placeholder = Loc.T("tokens.pool.weight"),
            };
            weight.ValueChanged += (_, e) =>
            {
                row.Weight = (int)Math.Max(1, e.NewValue);
                SyncMembers();
                _changed(_draft);
                RefreshShares();
            };
            right.Children.Add(weight);
        }
        if (Pooled && _draft.Strategy == PoolStrategies.Tiered)
        {
            var tier = Pick.Select(new[] { "1", "2", "3" }.Select(n => (n, Loc.T("tokens.pool.tierN", ("n", n)))), row.Tier.ToString(System.Globalization.CultureInfo.InvariantCulture),
                v =>
                {
                    row.Tier = int.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                    SyncMembers();
                    _changed(_draft);
                    RefreshShares();
                }, Loc.T("tokens.pool.tier"));
            tier.Width = 96;
            right.Children.Add(tier);
        }
        if (provider is not null && PoolDrafts.IsAccountBased(provider.AuthScheme)) right.Children.Add(AccountSelect(row, provider));
        var remove = new ArcButton
        {
            Variant = ButtonVariant.Ghost, Size = ControlSize.Small, Content = new LucideIcon { Kind = LucideIconKind.Trash2, Size = 14 },
        };
        var removeName = Loc.T("tokens.pool.remove", ("provider", name));
        ToolTip.SetTip(remove, removeName);
        Avalonia.Automation.AutomationProperties.SetName(remove, removeName);
        remove.Click += (_, _) =>
        {
            SyncMembers();
            Set(_draft with { Members = _draft.Members.Where(m => m.ProviderId != row.ProviderId).ToList() }, true);
        };
        right.Children.Add(remove);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        grid.Children.Add(left);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>The account of an account-based provider (subscription / plugin sign-ins): "default" or one of its accounts.</summary>
    private Control AccountSelect(MemberRow row, ProviderDto provider)
        => new QueryHost<List<ProviderAccountDto>>(_ctx.Accounts(provider.Id), q =>
        {
            var options = new List<(string, string)> { ("", Loc.T("clients.accountDefault")) };
            options.AddRange((q.Data ?? []).Select(a => (a.Id, a.DisplayName.Length > 0 ? a.DisplayName : a.Id)));
            var select = Pick.Select(options, row.AccountId ?? "", v =>
            {
                row.AccountId = v.Length == 0 ? null : v;
                SyncMembers();
                _changed(_draft);
            }, Loc.T("tokens.directAccount"));
            select.Width = 150;
            return select;
        });

    // ---------- expected share ----------

    /// <summary>The expected-traffic bar and legend (rebuilt in place when weights, tiers or the order change).</summary>
    private void RefreshShares()
    {
        if (_shareSlot is not ContentControl slot) return;
        if (!Pooled)
        {
            slot.Content = null;
            return;
        }
        var shares = PoolDrafts.ExpectedShares(_draft);
        var members = _draft.Members;

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        var title = KitStyle.Text(Loc.T("tokens.pool.share"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, FontWeight.Medium);
        title.FontSize = KitStyle.Type12;
        var hint = KitStyle.Text(Loc.T("tokens.pool.shareHint"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
        hint.FontSize = KitStyle.Type12;
        hint.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(hint, 1);
        head.Children.Add(title);
        head.Children.Add(hint);

        var bar = new Grid { Height = 8 };
        var track = new Border { Height = 8, CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = bar };
        CardPanel.Themed(track, CardPanel.SurfaceMutedBrush);
        var col = 0;
        for (var i = 0; i < members.Count; i++)
        {
            if (shares[i] <= 0) continue;
            if (col > 0) bar.ColumnDefinitions.Add(new ColumnDefinition(2, GridUnitType.Pixel));
            bar.ColumnDefinitions.Add(new ColumnDefinition(shares[i], GridUnitType.Star));
            var piece = new Border { CornerRadius = new CornerRadius(2) };
            Colorize(piece, i);
            Grid.SetColumn(piece, bar.ColumnDefinitions.Count - 1);
            bar.Children.Add(piece);
            col++;
        }

        var legend = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 4 };
        for (var i = 0; i < members.Count; i++)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            var swatch = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
            Colorize(swatch, i);
            item.Children.Add(swatch);
            item.Children.Add(Muted(NameOf(members[i].ProviderId), secondary: true));
            var percent = KitStyle.Text(Math.Round(shares[i] * 100, MidpointRounding.AwayFromZero).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%",
                ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Foreground, FontWeight.SemiBold);
            percent.FontSize = KitStyle.Type12;
            item.Children.Add(percent);
            if (shares[i] == 0) item.Children.Add(Muted(Loc.T("tokens.pool.standby")));
            legend.Children.Add(item);
        }
        slot.Content = new StackPanel { Spacing = 6, Children = { head, track, legend } };
    }

    /// <summary>The web's share colors: the accent, then success, warning and three fixed hues.</summary>
    private static void Colorize(Border target, int index)
    {
        var i = index % ShareColors.Length;
        if (i == 0) target.Token(Border.BackgroundProperty, ArcDesignTokenKind.Accent);
        else target.Background = new SolidColorBrush(Color.Parse(ShareColors[i]));
    }

    // ---------- sticky / failover ----------

    private Control Toggles()
    {
        var box = new StackPanel { Spacing = 8 };
        box.Children.Add(SwitchRow(Loc.T("tokens.pool.sticky"), Loc.T("tokens.pool.stickyHint"), _draft.Sticky == "session",
            on => Set(_draft with { Sticky = on ? "session" : "off" }, false)));
        box.Children.Add(SwitchRow(Loc.T("tokens.pool.failover"), Loc.T("tokens.pool.failoverHint"), _draft.Failover,
            on => Set(_draft with { Failover = on }, true)));
        if (_draft.Failover)
        {
            var attempts = new NumberField
            {
                Value = _draft.MaxAttempts, Minimum = 1, Maximum = 5, Step = 1, Width = 84, Size = ControlSize.Small,
                Label = Loc.T("tokens.pool.maxAttempts"), IsLabelVisible = false,
            };
            attempts.ValueChanged += (_, e) => Set(_draft with { MaxAttempts = (int)e.NewValue }, false);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var label = KitStyle.Text(Loc.T("tokens.pool.maxAttempts"), ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground);
            Grid.SetColumn(attempts, 1);
            row.Children.Add(label);
            row.Children.Add(attempts);
            box.Children.Add(row);
        }
        return box;
    }

    private static Control SwitchRow(string label, string hint, bool on, Action<bool> changed)
    {
        var text = new StackPanel
        {
            Spacing = 2,
            Children = { KitStyle.Text(label, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground), Muted(hint) },
        };
        var toggle = new ArcSwitch { IsChecked = on, VerticalAlignment = VerticalAlignment.Top };
        Avalonia.Automation.AutomationProperties.SetName(toggle, label);
        toggle.IsCheckedChanged += (_, _) => changed(toggle.IsChecked == true);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        Grid.SetColumn(toggle, 1);
        row.Children.Add(text);
        row.Children.Add(toggle);
        return row;
    }

    // ---------- bits ----------

    private static TextBlock Muted(string text, bool secondary = false)
    {
        var block = KitStyle.Text(text, ArcDesignTokenKind.TypeXS, secondary ? ArcDesignTokenKind.TextSecondary : ArcDesignTokenKind.TextMuted);
        block.FontSize = KitStyle.Type12;
        block.TextWrapping = TextWrapping.Wrap;
        block.TextTrimming = TextTrimming.None;
        return block;
    }

    /// <summary>The kit tone of a health tone.</summary>
    public static Tone ToTone(HealthTone tone) => tone switch { HealthTone.Green => Tone.Green, HealthTone.Orange => Tone.Orange, _ => Tone.Red };
}
