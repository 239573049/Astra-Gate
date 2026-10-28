using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Plugins;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.SmartRouting;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.SmartRouting;

/// <summary>
/// "Try it" (web <c>Tester</c>): judges a sample text with the settings as they are on screen (unsaved) and shows what the judge would
/// choose, how confident it was, and whether a real request would have waited that long. The text box is built once; the result below follows the model.
/// </summary>
internal sealed class RoutingTesterView : LiveView
{
    private readonly SmartRoutingTester _tester;
    private readonly Textarea _text;
    private readonly TKCode.Arc.Button _run;
    private readonly Border _outcome = new();

    /// <summary>Creates the tester of <paramref name="form"/>.</summary>
    public RoutingTesterView(SmartRoutingFormModel form)
    {
        _tester = form.Tester;
        _text = PageUi.Area(Loc.T("smartRouting.test.title"), _tester.Text, v => _tester.Text = v, 3, Loc.T("smartRouting.test.placeholder"));
        _run = PageUi.Button(Loc.T("smartRouting.test.run"), () => _ = _tester.RunAsync(), size: ControlSize.Small, enabled: _tester.CanRun);
        _run.HorizontalAlignment = HorizontalAlignment.Right;
        Watch(_tester);
    }

    /// <inheritdoc />
    protected override void Rebuild()
    {
        _run.Content = Loc.T("smartRouting.test.run");
        _run.IsEnabled = _tester.CanRun && !_tester.IsRunning;
        _run.IsLoading = _tester.IsRunning;
        _outcome.Child = Outcome();
        if (Content is null) Content = Layout();
    }

    private Control Layout()
    {
        var title = PageUi.Text(Loc.T("smartRouting.test.title"), ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground);
        var hint = PageUi.Text(Loc.T("smartRouting.test.hint"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted, wrap: true);
        return new StackPanel { Spacing = 8, Margin = new Thickness(16, 12), Children = { title, hint, _text, _run, _outcome } };
    }

    private Control? Outcome()
    {
        if (_tester.Errors.Count > 0) return PageUi.Alert(AlertTone.Danger, Loc.T("smartRouting.problems"), PageUi.Bullets(_tester.Errors));
        if (_tester.Result is not { } r) return null;
        return r.Ok ? Success(r) : Failure(r);
    }

    private Control Success(SmartRoutingTestDto r)
    {
        var slow = _tester.IsSlow;
        var confident = r.Confident == true;
        var lines = new StackPanel { Spacing = 2 };
        lines.Children.Add(RoutingUi.Paragraph(!string.IsNullOrEmpty(r.Model) ? Loc.T("smartRouting.test.model", ("model", r.Model)) : Loc.T("smartRouting.test.noModel"), size: 12));
        var percent = (int)Math.Floor((r.Confidence ?? 0) * 100 + 0.5);
        lines.Children.Add(RoutingUi.Paragraph($"{Loc.T("smartRouting.test.confidence", ("percent", percent))} · {(confident ? Loc.T("smartRouting.test.confident") : Loc.T("smartRouting.test.notConfident"))}", size: 12));
        if (slow)
        {
            var text = Loc.T("smartRouting.test.slow", ("ms", r.LatencyMs), ("limit", r.LimitMs)) + (r.NewConnection ? " " + Loc.T("smartRouting.test.connecting") : "");
            lines.Children.Add(RoutingUi.Paragraph(text, ArcDesignTokenKind.Warning, 12));
        }
        lines.Children.Add(RoutingUi.Paragraph(
            Loc.T("smartRouting.test.meta", ("ms", r.LatencyMs), ("input", r.InputTokens), ("output", r.OutputTokens), ("model", r.JudgeModel ?? "")), ArcDesignTokenKind.TextMuted, 12));
        var tone = confident && !slow ? AlertTone.Success : AlertTone.Warning;
        return PageUi.Alert(tone, Loc.T("smartRouting.test.result", ("grade", r.GradeName ?? r.Grade ?? "")), lines);
    }

    private static Control Failure(SmartRoutingTestDto r)
    {
        var body = new StackPanel { Spacing = 4 };
        if (!string.IsNullOrEmpty(r.Message)) body.Children.Add(RoutingUi.Paragraph(r.Message));
        if (r.Failure == "timeout" && r.NewConnection) body.Children.Add(RoutingUi.Paragraph(Loc.T("smartRouting.test.connecting")));
        return PageUi.Alert(AlertTone.Danger, Loc.T("smartRouting.test.failed", ("reason", RoutingTexts.ReasonText(r.Failure))), body);
    }
}
