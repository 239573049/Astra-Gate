using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc.DesignTokens;
using static Astra.Desktop.Components.Overview.BillShapes;

namespace Astra.Desktop.Components.Overview;

/// <summary>
/// A · the thermal receipt (web <c>ReceiptBill</c> in <c>components/bill/BillArt.tsx</c>): 372 px of paper with torn edges, monospace text,
/// the providers and their models, the totals and a barcode of the bill number. Every size and color mirrors <c>.bill-rc</c> in
/// <c>components/bill/bill.css</c> — change them together. The control is what "save" and "copy" render, so it holds nothing but the
/// picture (no shadow: the zigzag teeth stay transparent in the PNG, like the web's).
/// </summary>
public sealed class ReceiptBillView : UserControl
{
    private static readonly IBrush Ink = Brush("#1d1d1b");
    private static readonly IBrush Paper = Brush("#fdfcf8");
    private static readonly IBrush Label = Brush("#5b5750");
    private static readonly IBrush Muted = Brush("#6b675f");
    private static readonly IBrush Filler = Brush("#8a857b");

    /// <summary>Draws <paramref name="view"/>.</summary>
    public ReceiptBillView(BillView view)
    {
        Width = 372;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        // The receipt is monospace like every mono text of the app (web: ui-monospace, SF Mono, Menlo, Consolas + the CJK fallbacks).
        this.Token(TemplatedControl.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        var paper = new StackPanel { Spacing = 16 };
        paper.Children.Add(Head());
        paper.Children.Add(Dash());
        paper.Children.Add(Pairs(
            (Loc.T("bill.field.date"), $"{view.Date} {view.Weekday}"),
            (Loc.T("bill.field.window"), view.Window),
            (Loc.T("bill.field.number"), view.Number),
            (Loc.T("bill.field.currency"), "USD")));
        paper.Children.Add(Dash());
        if (view.Providers.Count == 0)
        {
            var none = Text(Loc.T("bill.empty"), 12, FontWeight.Normal, Muted);
            none.HorizontalAlignment = HorizontalAlignment.Center;
            none.Margin = new Thickness(0, 6);
            paper.Children.Add(none);
        }
        foreach (var p in view.Providers) paper.Children.Add(Provider(p));
        paper.Children.Add(new BillRule(BillRule.Kind.Double, Ink, 4));
        paper.Children.Add(Pairs(
            (Loc.T("bill.field.requests"), Loc.T("bill.count", ("count", view.Requests))),
            (Loc.T("bill.field.successRate"), view.SuccessRate),
            (Loc.T("bill.field.input"), view.InputTokens),
            (Loc.T("bill.field.output"), view.OutputTokens)));
        paper.Children.Add(Dash());
        paper.Children.Add(Total(view.Total));
        paper.Children.Add(Note(Loc.T("bill.estimate")));
        paper.Children.Add(new BillBarcode(Ink) { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
        var code = Text(view.Number.Replace("-", " ", StringComparison.Ordinal), 11, FontWeight.Normal, Ink, letterSpacing: 3.3);
        code.HorizontalAlignment = HorizontalAlignment.Center;
        code.Margin = new Thickness(0, -8, -3.3, 0);
        paper.Children.Add(code);
        var thanks = Text(Loc.T("bill.thanks"), 12, FontWeight.ExtraBold, Ink, letterSpacing: 1.44);
        thanks.HorizontalAlignment = HorizontalAlignment.Center;
        paper.Children.Add(thanks);
        var site = Note("astra-gate.si");
        site.Margin = new Thickness(0, -10, 0, 0);
        paper.Children.Add(site);

        Content = new StackPanel
        {
            Children =
            {
                new ZigzagEdge(up: true, Paper),
                new Border { Background = Paper, Padding = new Thickness(26, 26, 26, 28), Child = paper },
                new ZigzagEdge(up: false, Paper),
            },
        };
    }

    private static Control Head()
    {
        // 60 px with a 2 px rim and 14 px corners (web .logo; the picture is already black-and-white line art, so its grayscale filter changes nothing).
        var logo = new Border
        {
            Width = 60,
            Height = 60,
            CornerRadius = new CornerRadius(14),
            BorderBrush = Ink,
            BorderThickness = new Thickness(2),
            ClipToBounds = true,
            Background = Brushes.White,
            Child = new Image { Source = AppAssets.Logo, Stretch = Stretch.UniformToFill },
        };
        Avalonia.Automation.AutomationProperties.SetName(logo, "Astra Gate");
        var brand = Text("ASTRA GATE", 21, FontWeight.ExtraBold, Ink, lineHeight: 21 * 1.3, letterSpacing: 21 * 0.32);
        brand.Margin = new Thickness(0, 0, -21 * 0.32, 0);
        var sub = Text(Loc.T("bill.subtitle"), 11, FontWeight.Normal, Label, letterSpacing: 11 * 0.06);
        var head = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var c in new Control[] { logo, brand, sub })
        {
            c.HorizontalAlignment = HorizontalAlignment.Center;
            head.Children.Add(c);
        }
        return head;
    }

    private static Control Dash() => new BillRule(BillRule.Kind.Dashed, Ink, 1.5);

    /// <summary>Label / value lines (web .kv: 12 px on 21 px lines, the label in the muted ink).</summary>
    private static Control Pairs(params (string Key, string Value)[] rows)
    {
        var stack = new StackPanel();
        foreach (var (key, value) in rows)
            stack.Children.Add(Row(Text(key, 12, FontWeight.Normal, Label, lineHeight: 21), Text(value, 12, FontWeight.Normal, Ink, lineHeight: 21)));
        return stack;
    }

    private static Control Provider(BillProviderBlock p)
    {
        var block = new StackPanel { Spacing = 8 };
        block.Children.Add(Row(
            Text("■ " + p.Upper, 13, FontWeight.ExtraBold, Ink, letterSpacing: 13 * 0.05, wrap: true),
            Text(Loc.T("bill.count", ("count", p.Requests)), 13, FontWeight.ExtraBold, Ink, letterSpacing: 13 * 0.05)));
        foreach (var m in p.Models)
        {
            var line = new StackPanel { Spacing = 2, Margin = new Thickness(14, 0, 0, 0) };
            line.Children.Add(Row(Text(m.Name, 12.5, FontWeight.Normal, Ink, wrap: true), Text(m.Cost, 12.5, FontWeight.SemiBold, Ink)));
            line.Children.Add(Text(Loc.T("bill.modelLine", ("requests", m.Requests), ("input", m.Input), ("output", m.Output)), 11, FontWeight.Normal, Muted, wrap: true));
            block.Children.Add(line);
        }
        // 小计 ······ $4.45: the dotted filler sits 3 px above the text's baseline (web .sum > i, translateY(-3px)).
        var sum = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 6, Margin = new Thickness(14, 0, 0, 0) };
        sum.Children.Add(Text(Loc.T("bill.subtotal"), 12, FontWeight.SemiBold, Ink));
        var filler = new BillRule(BillRule.Kind.Dotted, Filler, 1.5) { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 7.5) };
        Grid.SetColumn(filler, 1);
        sum.Children.Add(filler);
        var cost = Text(p.Cost, 12, FontWeight.SemiBold, Ink);
        Grid.SetColumn(cost, 2);
        sum.Children.Add(cost);
        block.Children.Add(sum);
        return block;
    }

    /// <summary>合计 TOTAL and the amount, on one baseline (web .total).</summary>
    private static Control Total(string total)
    {
        var label = Text(Loc.T("bill.total"), 13, FontWeight.ExtraBold, Ink, letterSpacing: 13 * 0.08);
        label.VerticalAlignment = VerticalAlignment.Bottom;
        label.Margin = new Thickness(0, 0, 0, 1.5);
        var amount = Text(total, 34, FontWeight.ExtraBold, Ink, lineHeight: 34 * 1.1, letterSpacing: 34 * -0.02);
        amount.VerticalAlignment = VerticalAlignment.Bottom;
        return Row(label, amount);
    }

    private static TextBlock Note(string text)
    {
        var note = Text(text, 10.5, FontWeight.Normal, Muted, lineHeight: 10.5 * 1.6, wrap: true);
        note.TextAlignment = TextAlignment.Center;
        note.HorizontalAlignment = HorizontalAlignment.Stretch;
        return note;
    }
}
