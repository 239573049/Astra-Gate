using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using static Astra.Desktop.Components.Overview.BillShapes;

namespace Astra.Desktop.Components.Overview;

/// <summary>
/// E · the comic page (web <c>MangaBill</c> in <c>components/bill/BillArt.tsx</c>): the Astra line-art face with a speech bubble, a black title
/// strip, one inked panel per provider, the total on a screen tone with a red "settled" seal. Every size and color mirrors <c>.bill-mg</c> in
/// <c>components/bill/bill.css</c> — change them together. The control is the 628 px frame (the 620 px page plus its 8 px hard shadow), which
/// is what "save" and "copy" render, like the web's <c>.bill-mg-frame</c>. Text is the app's sans at weight Black; amounts are italic with a
/// 3 px #111 offset copy for CSS's <c>text-shadow</c>.
/// </summary>
public sealed class MangaBillView : UserControl
{
    private static readonly IBrush Ink = Brush("#111111");
    private static readonly IBrush Page = Brush("#fffdf5");
    private static readonly IBrush Red = Brush("#d7261e");
    private static readonly IBrush Small = Brush("#444444");
    private static readonly IBrush White = Brushes.White;

    /// <summary>Draws <paramref name="view"/>.</summary>
    public MangaBillView(BillView view)
    {
        Width = 628;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        var page = new StackPanel { Spacing = 20 };
        page.Children.Add(Top(view));
        page.Children.Add(Strip(view));
        page.Children.Add(view.Providers.Count == 0 ? None() : Panels(view.Providers));
        page.Children.Add(TotalPanel(view));
        page.Children.Add(Row(Text(Loc.T("bill.estimate"), 11, FontWeight.Bold, Ink), Text("astra-gate.si", 11, FontWeight.Bold, Ink)));

        // .bill-mg: 620 px with a 3 px border and 28 px padding; box-shadow 8px 8px 0 #111 drawn inside the frame's 8 px.
        Content = new Panel
        {
            Children =
            {
                new Border { Background = Ink, Margin = new Thickness(8, 8, 0, 0) },
                new Border
                {
                    Margin = new Thickness(0, 0, 8, 8), Background = Page, BorderBrush = Ink, BorderThickness = new Thickness(3), Padding = new Thickness(28),
                    Child = page,
                },
            },
        };
    }

    // ---------- face + speech bubble ----------

    private static Control Top(BillView view)
    {
        var face = new Border
        {
            Width = 176,
            Height = 176,
            BorderBrush = Ink,
            BorderThickness = new Thickness(3),
            Background = White,
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new Image { Source = AppAssets.Logo, Stretch = Stretch.UniformToFill },
        };
        Avalonia.Automation.AutomationProperties.SetName(face, "Astra Gate");

        var top = Kicker(Loc.T("bill.manga.kicker"), "No." + view.Date.Replace("-", "", StringComparison.Ordinal));
        var bottom = Kicker($"{view.DateShort} {view.Weekday}", Loc.T("bill.manga.until", ("time", view.Until)));
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        var bubble = Bubble(view);
        bubble.VerticalAlignment = VerticalAlignment.Center;
        bubble.Margin = new Thickness(0, 12);
        var say = new DockPanel { Height = 176, Children = { top, bottom, bubble } };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("176,*"), ColumnSpacing = 26 };
        grid.Children.Add(face);
        Grid.SetColumn(say, 1);
        grid.Children.Add(say);
        return grid;
    }

    private static Control Kicker(string left, string right)
        => Row(Text(left, 12, FontWeight.Black, Ink, letterSpacing: 12 * 0.14), Text(right, 12, FontWeight.Black, Ink, letterSpacing: 12 * 0.14), 8);

    /// <summary>The bubble: "今天一共花掉 <em>$6.36</em>！" over the leader's line (or the empty-day line), 18 px bold, the amount 26 px red italic.</summary>
    private static Control Bubble(BillView view)
    {
        var lines = new StackPanel();
        if (view.Top is { } top)
        {
            // The translation is split around its {total} slot so the amount alone is red (web SayTotal).
            var template = Loc.T("bill.manga.say", ("total", "\u0000"));
            var cut = template.IndexOf('\u0000', StringComparison.Ordinal);
            var first = Text(null, 18, FontWeight.Bold, Ink, lineHeight: 26 * 1.5, wrap: true);
            first.Inlines!.Add(new Run(cut < 0 ? template : template[..cut]));
            first.Inlines.Add(new Run(view.Total) { FontSize = 26, FontWeight = FontWeight.Black, FontStyle = FontStyle.Italic, Foreground = Red });
            if (cut >= 0) first.Inlines.Add(new Run(template[(cut + 1)..]));
            lines.Children.Add(first);
            lines.Children.Add(Text(Loc.T("bill.manga.top", ("provider", top.Name), ("share", top.Share)), 18, FontWeight.Bold, Ink, wrap: true));
        }
        else lines.Children.Add(Text(Loc.T("bill.manga.sayEmpty"), 18, FontWeight.Bold, Ink, wrap: true));

        var box = new Border
        {
            BorderBrush = Ink,
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(28),
            Background = White,
            Padding = new Thickness(20, 14),
            Child = lines,
        };
        var tail = new BubbleTail(Ink, White)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(-23, 39, 0, 0),
        };
        return new Panel { Children = { box, tail } };
    }

    // ---------- title strip ----------

    private static Control Strip(BillView view)
    {
        var title = Text(Loc.T("bill.title"), 28, FontWeight.Black, Page, letterSpacing: 28 * 0.06);
        var facts = Text(Loc.T("bill.manga.strip", ("requests", view.Requests), ("tokens", view.TokensCompact)), 16, FontWeight.Black, Page,
            letterSpacing: 16 * 0.06, italic: true);
        title.VerticalAlignment = VerticalAlignment.Center;
        facts.VerticalAlignment = VerticalAlignment.Center;
        return new Border { Background = Ink, Padding = new Thickness(18, 10), Child = Row(title, facts) };
    }

    // ---------- provider panels ----------

    private static Control None()
    {
        var text = Text(Loc.T("bill.empty"), 14, FontWeight.Bold, Ink);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        return new DashedBox(Ink, 3, new Thickness(22), text);
    }

    /// <summary>Two columns 18 px apart, each panel as tall as it needs (web .grid, align-items: start).</summary>
    private static Control Panels(IReadOnlyList<BillProviderBlock> providers)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 18, RowSpacing = 18 };
        for (var r = 0; r < (providers.Count + 1) / 2; r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var i = 0; i < providers.Count; i++)
        {
            var card = Card(providers[i]);
            Grid.SetRow(card, i / 2);
            Grid.SetColumn(card, i % 2);
            grid.Children.Add(card);
        }
        return grid;
    }

    private static Control Card(BillProviderBlock p)
    {
        var name = Text(p.Name, 15, FontWeight.Black, White, wrap: true);
        var cost = Text(p.Cost, 18, FontWeight.Black, White, italic: true);
        name.VerticalAlignment = VerticalAlignment.Center;
        cost.VerticalAlignment = VerticalAlignment.Center;
        var body = new StackPanel();
        body.Children.Add(new Border { Background = Ink, Padding = new Thickness(12, 7), Child = Row(name, cost, 8) });
        for (var i = 0; i < p.Models.Count; i++)
        {
            var m = p.Models[i];
            var line = new StackPanel { Spacing = 2, Margin = new Thickness(12, 9) };
            line.Children.Add(Row(Text(m.Name, 13, FontWeight.Bold, Ink, wrap: true), Text(m.Cost, 13, FontWeight.Bold, Ink), 8));
            line.Children.Add(Text(Loc.T("bill.modelLine", ("requests", m.Requests), ("input", m.Input), ("output", m.Output)), 11, FontWeight.Medium, Small, wrap: true));
            body.Children.Add(line);
            if (i < p.Models.Count - 1) body.Children.Add(new BillRule(BillRule.Kind.Dashed, Ink, 2));
        }
        // box-shadow 5px 5px 0 #111: a black copy shifted under the panel, outside its layout box like the CSS shadow.
        return new Panel
        {
            VerticalAlignment = VerticalAlignment.Top,
            Children =
            {
                new Border { Background = Ink, RenderTransform = new TranslateTransform(5, 5) },
                new Border { BorderBrush = Ink, BorderThickness = new Thickness(3), Background = White, Child = body },
            },
        };
    }

    // ---------- total + seal ----------

    private static Control TotalPanel(BillView view)
    {
        var inner = new StackPanel { Spacing = 6 };
        inner.Children.Add(Text(Loc.T("bill.total"), 14, FontWeight.Black, Ink, letterSpacing: 1.4));
        inner.Children.Add(Shadowed(view.Total, 64));
        inner.Children.Add(Text(Loc.T("bill.manga.facts", ("requests", view.Requests), ("rate", view.SuccessRate)), 12, FontWeight.Bold, Ink));
        var card = new Border
        {
            Background = White,
            BorderBrush = Ink,
            BorderThickness = new Thickness(3),
            Padding = new Thickness(22, 14, 22, 16),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = inner,
        };
        // The seal is 34 px from the panel's padding box (web right: 34px), i.e. 16 px into its 18 px padding, centered on its height.
        var seal = Seal();
        seal.HorizontalAlignment = HorizontalAlignment.Right;
        seal.VerticalAlignment = VerticalAlignment.Center;
        seal.Margin = new Thickness(0, -60, 16, -60);
        return new Panel
        {
            Children =
            {
                new Halftone(Ink) { Margin = new Thickness(3) },
                new Border { BorderBrush = Ink, BorderThickness = new Thickness(3), Padding = new Thickness(18), Child = new Panel { Children = { card, seal } } },
            },
        };
    }

    /// <summary>The amount: 64 px italic Black red, over a #111 copy 3 px right and down (CSS text-shadow: 3px 3px 0 #111).</summary>
    private static Control Shadowed(string text, double size)
    {
        var shadow = Text(text, size, FontWeight.Black, Ink, lineHeight: size, letterSpacing: size * 0.01, italic: true);
        shadow.RenderTransform = new TranslateTransform(3, 3);
        var face = Text(text, size, FontWeight.Black, Red, lineHeight: size, letterSpacing: size * 0.01, italic: true);
        // The app's sans sits a little lower in its line than the web's system font: keep the shadow clear of the line below.
        return new Panel { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4), Children = { shadow, face } };
    }

    /// <summary>
    /// The 124 px "settled" seal turned -14°: a 4 px red ring, 4 px of page color, a 2 px red ring (the CSS border plus its two inset shadows), then
    /// ASTRA / GATE / 已结算.
    /// </summary>
    private static Control Seal()
    {
        var text = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var word in new[] { "ASTRA", "GATE" })
        {
            var w = Text(word, 22, FontWeight.Black, Red, lineHeight: 22, letterSpacing: 22 * 0.06, italic: true);
            w.HorizontalAlignment = HorizontalAlignment.Center;
            text.Children.Add(w);
        }
        var settled = Text(Loc.T("bill.manga.seal"), 12, FontWeight.Black, Red, letterSpacing: 12 * 0.2);
        settled.HorizontalAlignment = HorizontalAlignment.Center;
        settled.Margin = new Thickness(0, 0, -12 * 0.2, 0);
        text.Children.Add(settled);
        return new Panel
        {
            Width = 124,
            Height = 124,
            IsHitTestVisible = false,
            RenderTransform = new RotateTransform(-14),
            Children =
            {
                new Avalonia.Controls.Shapes.Ellipse { Fill = new SolidColorBrush(Color.FromArgb(235, 255, 253, 245)), Stroke = Red, StrokeThickness = 4 },
                new Avalonia.Controls.Shapes.Ellipse { Margin = new Thickness(8), Stroke = Red, StrokeThickness = 2 },
                text,
            },
        };
    }
}
