using Astra.Api.Contracts;
using Astra.Core.Requests;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Traces;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcSwitch = TKCode.Arc.Switch;

namespace Astra.Desktop.Components.Traces;

/// <summary>What the activity feed asks of its page: names, the clock and what a click / hover / the content switch does.</summary>
/// <param name="NameOf">A provider's and an account's display names (the route line).</param>
/// <param name="NowMs">The page clock in Unix ms (a running request's elapsed time).</param>
/// <param name="Hover">The pointer entered a card (null: left the feed); the canvas highlights its path.</param>
/// <param name="Open">Opens a request's trace.</param>
/// <param name="SetContent">Turns conversation content recording on or off (the <c>traceContent</c> setting).</param>
/// <param name="ClientLabel">A client kind's display name (icon fallback).</param>
public sealed record ActivityRailActions(Func<string?, string?, (string Provider, string? Account)> NameOf, Func<double> NowMs,
    Action<ActivityItem?> Hover, Action<string> Open, Action<bool> SetContent, Func<string?, string> ClientLabel);

/// <summary>
/// The activity feed beside the topology (activity.tsx <c>ActivityRail</c>; option B of .plan/prototypes/trace-routing-options.html): one card
/// per request — why it was sent (the user's prompt or which tool results), the route it took (each account or provider, a refusal with its
/// status and why it moved on) and what came back (thinking time and size, the start of the reply, each tool call with its arguments as
/// key / value pairs). The header's 记录内容 switch flips the <c>traceContent</c> setting; a card under the pointer highlights its path on the
/// canvas. Cards are rebuilt only when what they show changes; a running card's elapsed time ticks with the page.
/// </summary>
public sealed class ActivityRailView : UserControl
{
    private readonly ActivityRailActions _actions;
    private readonly StackPanel _list = new() { Spacing = 8, Margin = new Thickness(10) };
    private readonly ArcSwitch _content = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<string, (string Signature, Border Card, TextBlock Elapsed)> _cards = new(StringComparer.Ordinal);
    private readonly HashSet<string> _closedTools = new(StringComparer.Ordinal);
    private readonly Border _frame;
    private TracePalette? _palette;
    private bool _quiet;
    private string? _highlighted;
    private IReadOnlyList<ActivityItem> _items = [];

    /// <summary>Builds an empty feed.</summary>
    public ActivityRailView(ActivityRailActions actions)
    {
        _actions = actions;
        Avalonia.Automation.AutomationProperties.SetName(_content, Loc.T("traces.activity.content"));
        ToolTip.SetTip(_content, Loc.T("traces.activity.contentHint"));
        _content.IsCheckedChanged += (_, _) =>
        {
            if (!_quiet) _actions.SetContent(_content.IsChecked == true);
        };
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(TraceUi.Text(Loc.T("traces.activity.title"), 13, ArcDesignTokenKind.Foreground, FontWeight.SemiBold));
        var hint = TraceUi.Text(Loc.T("traces.activity.hint"), 11, ArcDesignTokenKind.TextSecondary);
        hint.TextTrimming = TextTrimming.CharacterEllipsis;
        titles.Children.Add(hint);
        var switchRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        switchRow.Children.Add(TraceUi.Text(Loc.T("traces.activity.content"), 12, ArcDesignTokenKind.Foreground));
        switchRow.Children.Add(_content);
        ToolTip.SetTip(switchRow, Loc.T("traces.activity.contentHint"));
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(12, 8) };
        head.Children.Add(titles);
        Grid.SetColumn(switchRow, 1);
        head.Children.Add(switchRow);
        var divider = TraceUi.Divider();
        var scroll = new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        scroll.PointerExited += (_, _) => _actions.Hover(null);
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        grid.Children.Add(head);
        Grid.SetRow(divider, 1);
        grid.Children.Add(divider);
        Grid.SetRow(scroll, 2);
        grid.Children.Add(scroll);
        _frame = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), ClipToBounds = true, Child = grid };
        Requests.WebMaterial.Apply(_frame, edge: false);
        Content = _frame;
        PaletteWatch.Attach(grid, () =>
        {
            _cards.Clear();
            Update(_items, _contentOn, _contentReady, _highlighted);
        });
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // the page may have handed over its items before the feed was shown
        Update(_items, _contentOn, _contentReady, _highlighted);
    }

    private bool _contentOn;
    private bool _contentReady;

    /// <summary>Shows <paramref name="items"/>; <paramref name="highlighted"/> is the card whose path the canvas lights.</summary>
    public void Update(IReadOnlyList<ActivityItem> items, bool contentOn, bool contentReady, string? highlighted)
    {
        _items = items;
        _highlighted = highlighted;
        _contentOn = contentOn;
        _contentReady = contentReady;
        _quiet = true;
        _content.IsChecked = contentOn;
        _content.IsEnabled = contentReady;
        _quiet = false;
        if (VisualRoot is null) return;
        var p = _palette = TracePalette.For(this);
        _frame.BorderBrush = new SolidColorBrush(p.Separator);
        var now = _actions.NowMs();
        var children = new List<Control>();
        if (!contentOn && items.Any(i => !i.Live && i.Tools.Any(c => string.IsNullOrEmpty(c.Arguments))))
        {
            var note = TraceUi.Text(Loc.T("traces.activity.contentOff"), 11.5, ArcDesignTokenKind.TextSecondary, wrap: true);
            children.Add(new Border { Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(p.SurfaceMuted), Child = note });
        }
        if (items.Count == 0)
        {
            var empty = TraceUi.Text(Loc.T("traces.activity.empty"), 12.5, ArcDesignTokenKind.TextSecondary, wrap: true);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.TextAlignment = TextAlignment.Center;
            children.Add(new Border { Padding = new Thickness(4, 24), Child = empty });
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            seen.Add(item.Id);
            var signature = Signature(item, contentOn);
            if (!_cards.TryGetValue(item.Id, out var card) || card.Signature != signature)
            {
                var (border, elapsed) = BuildCard(item, p);
                _cards[item.Id] = card = (signature, border, elapsed);
            }
            var pending = item.Request.Status == "pending";
            card.Card.BorderBrush = new SolidColorBrush(item.Id == highlighted ? p.Accent : pending ? TracePalette.WithAlpha(p.Accent, 0.45) : p.Separator);
            card.Card.BorderThickness = new Thickness(item.Id == highlighted ? 2 : 1);
            card.Elapsed.Text = TraceFormat.Ms(pending ? Math.Max(0, now - item.Request.StartedAtUtc.ToUnixTimeMilliseconds()) : item.Request.TotalMs);
            children.Add(card.Card);
        }
        foreach (var gone in _cards.Keys.Where(k => !seen.Contains(k)).ToList()) _cards.Remove(gone);
        // re-adding the same controls in a new order keeps them (and the pointer over them) where they are when nothing moved
        if (!_list.Children.SequenceEqual(children))
        {
            _list.Children.Clear();
            foreach (var c in children) _list.Children.Add(c);
        }
    }

    /// <summary>Everything a card shows except its elapsed time and highlight: a change rebuilds it.</summary>
    private static string Signature(ActivityItem i, bool contentOn)
    {
        var r = i.Request;
        return string.Join('\u0001', i.Live, r.Status, r.HttpStatus, r.Stage, r.RequestedModel, i.Step, i.FinishReason, i.ErrorMessage, i.Reply,
            i.Cause?.Kind, i.Cause?.Prompt, string.Join(',', i.Cause?.ToolResults.Select(x => $"{x.Name}/{x.Error}/{x.Chars}") ?? []),
            string.Join(',', i.Attempts.Select(a => $"{a.ProviderId}/{a.AccountId}/{a.Status}/{a.EndMs}/{a.Retry}")),
            string.Join(',', i.Tools.Select(c => $"{c.Name}/{c.Arguments?.Length}")),
            Math.Round((i.ThinkMs ?? 0) / 1000), Math.Round((i.TextMs ?? 0) / 1000), i.ThinkChars, i.TextChars, r.TotalInputTokens, r.TotalOutputTokens, contentOn);
    }

    private (Border Card, TextBlock Elapsed) BuildCard(ActivityItem item, TracePalette p)
    {
        var r = item.Request;
        var t = TraceTexts.FromLoc;
        var pending = r.Status == "pending";
        var failed = !pending && r.Status != "success";
        var col = new StackPanel { Spacing = 8 };

        // header: client, model, step, state, elapsed, open
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto,Auto"), ColumnSpacing = 7 };
        if (!string.IsNullOrEmpty(r.ClientKind))
        {
            var icon = BrandIcon.Client(r.ClientKind, 16, fallbackName: _actions.ClientLabel(r.ClientKind));
            icon.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(icon);
        }
        var model = TraceUi.Text(TraceFormat.ShortModel(r.RequestedModel), 12, ArcDesignTokenKind.Foreground, FontWeight.SemiBold);
        model.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(model, 1);
        head.Children.Add(model);
        if (item.Step is { } step)
        {
            var s = TraceUi.Text("#" + step.ToString(System.Globalization.CultureInfo.InvariantCulture), 12, ArcDesignTokenKind.TextSecondary);
            Grid.SetColumn(s, 2);
            head.Children.Add(s);
        }
        Control? state = null;
        if (pending)
        {
            var live = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            live.Children.Add(TraceUi.LivePulse());
            var stage = TraceUi.Text(TraceAnalysis.StageText(r, t), 11, p.Accent);
            stage.TextTrimming = TextTrimming.CharacterEllipsis;
            live.Children.Add(stage);
            state = live;
        }
        else if (failed)
            state = new Border
            {
                Padding = new Thickness(6, 0), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(p.Red),
                VerticalAlignment = VerticalAlignment.Center,
                Child = TraceUi.Text(r.HttpStatus is { } h and not 0 ? h.ToString(System.Globalization.CultureInfo.InvariantCulture) : t("traces.live.failed"), 11, p.Red),
            };
        if (state is not null)
        {
            Grid.SetColumn(state, 3);
            state.HorizontalAlignment = HorizontalAlignment.Left;
            head.Children.Add(state);
        }
        var elapsed = TraceUi.Text("", 11.5, ArcDesignTokenKind.TextSecondary);
        elapsed.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(elapsed, 4);
        head.Children.Add(elapsed);
        if (!(item.Live && pending))
        {
            var open = new Border
            {
                Padding = new Thickness(2), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
                Child = new LucideIcon { Kind = LucideIconKind.ArrowUpRight, Size = 14, Foreground = new SolidColorBrush(p.Label2) }, VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(open, t("traces.activity.open"));
            Avalonia.Automation.AutomationProperties.SetName(open, t("traces.activity.open"));
            var id = item.Id;
            open.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(open).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                _actions.Open(id);
            };
            Grid.SetColumn(open, 5);
            head.Children.Add(open);
        }
        col.Children.Add(head);

        // cause
        if (item.Cause is { } cause)
        {
            var v = new StackPanel { Spacing = 6 };
            if (cause.Kind == "user")
            {
                v.Children.Add(Chip("user", t("traces.cause.user"), p));
                if (!string.IsNullOrEmpty(cause.Prompt)) v.Children.Add(Wrapped(cause.Prompt, 3, p));
            }
            else
            {
                var wrap = TraceUi.Wrap(6, 4);
                wrap.Children.Add(Chip("result", t("traces.cause.tool_result"), p));
                foreach (var x in cause.ToolResults)
                {
                    var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                    line.Children.Add(TraceUi.Text(x.Name ?? "?", 11.5, ArcDesignTokenKind.Foreground));
                    line.Children.Add(TraceUi.Text(x.Error == true ? "✕" : "✓", 11.5, x.Error == true ? p.Red : p.Green, FontWeight.Bold));
                    line.Children.Add(TraceUi.Text(t("traces.cause.chars", ("count", TraceFormat.Tokens(x.Chars, true))), 11.5, ArcDesignTokenKind.TextSecondary));
                    wrap.Children.Add(line);
                }
                v.Children.Add(wrap);
            }
            col.Children.Add(Line(t("traces.activity.cause"), v));
        }

        // route
        var attempts = item.Attempts.Count > 0 ? item.Attempts
            : !string.IsNullOrEmpty(r.ProviderId) ? [new RequestAttempt(r.ProviderId, null, r.AccountId, r.HttpStatus, null, 0, r.TotalMs, null)] : [];
        if (attempts.Count > 0)
        {
            var wrap = TraceUi.Wrap(6, 4);
            for (var i = 0; i < attempts.Count; i++)
            {
                var a = attempts[i];
                var prev = i > 0 ? attempts[i - 1] : null;
                var (provider, account) = _actions.NameOf(a.ProviderId ?? r.ProviderId, a.AccountId);
                var providerChanged = prev is null || (prev.ProviderId ?? r.ProviderId) != (a.ProviderId ?? r.ProviderId);
                var text = providerChanged || account is null ? string.Join(" · ", new[] { provider, account }.Where(x => !string.IsNullOrEmpty(x))) : account;
                var bad = TopologyBuilder.AttemptFailed(a) && (i < attempts.Count - 1 || !pending);
                var open = (a.Status ?? 0) == 0 && a.EndMs is null;
                if (prev is not null)
                    wrap.Children.Add(TraceUi.Text((TopologyBuilder.SwitchReason(prev.Retry, t) ?? "") + " →", 11, p.Orange));
                var hop = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                hop.Children.Add(open && pending ? TraceUi.LivePulse() : TraceUi.Dot(bad ? p.Red : p.Green, 6));
                var name = TraceUi.Text(text, 11.5, ArcDesignTokenKind.Foreground);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                hop.Children.Add(name);
                if (bad) hop.Children.Add(TraceUi.Text(a.Status is { } s and not 0 ? s.ToString(System.Globalization.CultureInfo.InvariantCulture) : t("traces.cause.failed"), 11.5, p.Red, FontWeight.SemiBold));
                wrap.Children.Add(new Border
                {
                    Padding = new Thickness(6, 1), CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), Child = hop,
                    BorderBrush = new SolidColorBrush(bad ? p.Red : open && pending ? p.Accent : p.Separator),
                });
            }
            col.Children.Add(Line(t("traces.activity.route"), wrap));
        }

        // output
        var output = new StackPanel { Spacing = 6 };
        if (item.ThinkMs is > 0 || item.ThinkChars is > 0)
            output.Children.Add(ChipLine("think", t("traces.output.think"),
                item.ThinkChars is > 0 ? t("traces.output.thinkLine", ("time", TraceFormat.Ms(item.ThinkMs ?? 0)), ("chars", TraceFormat.Tokens(item.ThinkChars, true))) : TraceFormat.Ms(item.ThinkMs ?? 0), p));
        if (!string.IsNullOrEmpty(item.Reply) || item.TextChars is > 0 || item.TextMs is > 0)
        {
            output.Children.Add(ChipLine("text", t("traces.output.text"),
                item.TextChars is > 0 ? t("traces.cause.chars", ("count", TraceFormat.Tokens(item.TextChars, true))) : item.TextMs is > 0 ? TraceFormat.Ms(item.TextMs) : null, p));
            if (!string.IsNullOrEmpty(item.Reply)) output.Children.Add(Wrapped(item.Reply, 4, p));
        }
        for (var k = 0; k < item.Tools.Count; k++) output.Children.Add(ToolCall(item, k, p));
        if (output.Children.Count == 0 && pending && TraceAnalysis.LivePhaseOf(r) != LivePhase.Arriving)
            output.Children.Add(TraceUi.Text(t("traces.output.waiting"), 11.5, ArcDesignTokenKind.TextSecondary));
        if (output.Children.Count > 0) col.Children.Add(Line(t("traces.activity.output"), output));

        // footer
        if (!pending)
        {
            var foot = new StackPanel { Spacing = 2 };
            var parts = new[]
            {
                item.FinishReason is { } f ? t("traces.finish." + f) : null,
                t("traces.activity.tokens", ("input", TraceFormat.Tokens(r.TotalInputTokens, true)), ("output", TraceFormat.Tokens(r.TotalOutputTokens, true))),
            };
            foot.Children.Add(TraceUi.Text(string.Join(" · ", parts.Where(x => !string.IsNullOrEmpty(x))), 11, ArcDesignTokenKind.TextSecondary));
            if (failed && !string.IsNullOrEmpty(item.ErrorMessage))
            {
                var error = TraceUi.Text(item.ErrorMessage, 11, p.Red);
                error.TextWrapping = TextWrapping.Wrap;
                error.MaxLines = 2;
                error.TextTrimming = TextTrimming.CharacterEllipsis;
                foot.Children.Add(error);
            }
            col.Children.Add(new Border { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = new SolidColorBrush(p.Separator), Padding = new Thickness(0, 6, 0, 0), Child = foot });
        }

        var card = new Border
        {
            Padding = new Thickness(10), CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), Background = new SolidColorBrush(p.SurfaceSolid),
            Child = col,
        };
        card.PointerEntered += (_, _) => _actions.Hover(item);
        return (card, elapsed);
    }

    /// <summary>A tool call: chip, name and its headline argument; click to fold the arguments (key / value pairs, or the raw text) away.</summary>
    private Control ToolCall(ActivityItem item, int index, TracePalette p)
    {
        var call = item.Tools[index];
        var t = TraceTexts.FromLoc;
        var args = ActivityFeed.Arguments(call.Arguments);
        var summary = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 6 };
        summary.Children.Add(Chip("tool", t("traces.output.tool"), p));
        var name = TraceUi.Text(call.Name, 12, ArcDesignTokenKind.Foreground, FontWeight.SemiBold);
        name.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(name, 1);
        summary.Children.Add(name);
        if (args is null)
        {
            // a live item's arguments arrive with its stored row: say nothing about them yet
            if (!item.Live)
            {
                var none = TraceUi.Text(t("traces.output.noArgs"), 11, ArcDesignTokenKind.TextSecondary);
                none.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(none, 2);
                summary.Children.Add(none);
            }
            return summary;
        }
        var headline = TraceUi.Mono(ActivityFeed.Headline(call.Arguments) ?? "", 11, ArcDesignTokenKind.TextSecondary);
        headline.TextTrimming = TextTrimming.CharacterEllipsis;
        headline.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(headline, 2);
        summary.Children.Add(headline);
        var key = item.Id + "#" + index;
        var chevron = TraceUi.Text(_closedTools.Contains(key) ? "▸" : "▾", 10, ArcDesignTokenKind.TextSecondary);
        chevron.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(chevron, 3);
        summary.Children.Add(chevron);

        Control body;
        if (args.Pairs is { } pairs)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10, RowSpacing = 2 };
            for (var i = 0; i < pairs.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var k = TraceUi.Mono(pairs[i].Key, 11, ArcDesignTokenKind.TextSecondary);
                Grid.SetRow(k, i);
                grid.Children.Add(k);
                var v = TraceUi.Mono(pairs[i].Value, 11, ArcDesignTokenKind.Foreground);
                v.TextWrapping = TextWrapping.Wrap;
                v.MaxLines = 10;
                v.TextTrimming = TextTrimming.CharacterEllipsis;
                Grid.SetRow(v, i);
                Grid.SetColumn(v, 1);
                grid.Children.Add(v);
            }
            body = grid;
        }
        else
        {
            var raw = TraceUi.Mono(args.Raw, 11, ArcDesignTokenKind.Foreground);
            raw.TextWrapping = TextWrapping.Wrap;
            raw.MaxLines = 10;
            body = raw;
        }
        var bodyBox = new Border
        {
            Margin = new Thickness(6, 0, 6, 6), Padding = new Thickness(8, 6), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(p.SurfaceSolid),
            Child = body, IsVisible = !_closedTools.Contains(key),
        };
        var head = new Border { Padding = new Thickness(6, 4), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Child = summary };
        head.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(head).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            if (!_closedTools.Remove(key)) _closedTools.Add(key);
            bodyBox.IsVisible = !_closedTools.Contains(key);
            chevron.Text = bodyBox.IsVisible ? "▾" : "▸";
        };
        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(bodyBox);
        return new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(p.SurfaceMuted), Child = stack };
    }

    private static Control Line(string label, Control content)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("30,*"), ColumnSpacing = 8 };
        var l = TraceUi.Text(label, 11, ArcDesignTokenKind.TextSecondary);
        l.Margin = new Thickness(0, 2, 0, 0);
        l.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(l);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    private static Control ChipLine(string tone, string chip, string? detail, TracePalette p)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(Chip(tone, chip, p));
        if (!string.IsNullOrEmpty(detail))
        {
            var d = TraceUi.Text(detail, 11.5, ArcDesignTokenKind.TextSecondary);
            d.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(d);
        }
        return row;
    }

    private static TextBlock Wrapped(string text, int lines, TracePalette p)
    {
        var block = TraceUi.Text(text, 12, p.Label);
        block.TextWrapping = TextWrapping.Wrap;
        block.MaxLines = lines;
        block.TextTrimming = TextTrimming.CharacterEllipsis;
        return block;
    }

    /// <summary>A coloured chip (activity.tsx <c>Chip</c>): user cyan, tool result green outline, thinking accent-2, text accent, tool green.</summary>
    private static Border Chip(string tone, string text, TracePalette p)
    {
        var (bg, ink, edge) = tone switch
        {
            "user" => (TracePalette.Cyan, TracePalette.Ink, Colors.Transparent),
            "result" => (Colors.Transparent, p.Green, p.Green),
            "think" => (p.Accent2, Colors.White, Colors.Transparent),
            "text" => (p.Accent, Colors.White, Colors.Transparent),
            _ => (p.Green, TracePalette.Ink, Colors.Transparent),
        };
        return new Border
        {
            Height = 18, Padding = new Thickness(6, 0), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(edge.A == 0 ? 0 : 1),
            Background = new SolidColorBrush(bg), BorderBrush = new SolidColorBrush(edge), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center, Child = TraceUi.Text(text, 11, ink, FontWeight.SemiBold),
        };
    }
}
