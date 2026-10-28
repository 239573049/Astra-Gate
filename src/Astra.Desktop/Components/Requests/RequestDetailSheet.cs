using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Requests;
using Astra.Desktop.Core.Traces;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.Requests;

/// <summary>
/// The request detail dialog (web <c>RequestDetailSheet</c>): summary, timing, token usage, the error, the smart-routing decision, the privacy
/// outcome, the per-token-type bill and the raw usage / pricing / captured bodies. It re-renders from the detail query as it changes (an in-flight
/// request refetches every second). The web's dialog is 780 px wide; Arc's <see cref="Dialog"/> has no free width (360 / 440 / 560), so the surface's
/// max width is widened locally (see <see cref="Widen"/>).
/// </summary>
public sealed class RequestDetailSheet : Sheet
{
    private readonly RequestDetailViewModel _vm;
    private readonly Action<string> _openTrace;
    private readonly RequestTableContext _tableContext;

    /// <summary>A sheet over <paramref name="vm"/>; <paramref name="openTrace"/> runs for the footer's "查看链路" with the request id.</summary>
    public RequestDetailSheet(RequestDetailViewModel vm, RequestTableContext tableContext, Action<string> openTrace)
    {
        _vm = vm;
        _tableContext = tableContext;
        _openTrace = openTrace;
        SheetWidth = 780;
        Render();
        _vm.Detail.PropertyChanged += OnDetail;
        Opened += (_, _) => Widen();
        Footer = FooterButtons();
    }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Sheet).BaseType!;

    private void OnDetail(object? sender, PropertyChangedEventArgs e) => Render();

    /// <summary>Stops listening (the page closes the sheet).</summary>
    public void Detach() => _vm.Detail.PropertyChanged -= OnDetail;

    private void Widen()
    {
        // Arc's panel width is a theme setter on the surface (MaxWidth); a local value on the surface wins over it.
        if (Surface is { } surface) surface.MaxWidth = SheetWidth;
    }

    private IEnumerable<Control> FooterButtons()
    {
        var trace = new ArcButton { Variant = ButtonVariant.Ghost, Content = Loc.T("traces.openTrace") };
        trace.Token(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, ArcDesignTokenKind.Accent);
        trace.Click += (_, _) =>
        {
            Close();
            _openTrace(_vm.Id);
        };
        var close = new ArcButton { Variant = ButtonVariant.Secondary, Content = Loc.T("common.close") };
        Dialog.SetResult(close, true);
        return [trace, close];
    }

    private void Render()
    {
        var q = _vm.Detail;
        var r = q.HasData ? q.Data : null;
        Title = RequestPresentation.DetailTitle(r?.RequestedModel, TraceTexts.FromLoc);
        Description = r?.Id;
        var stack = new StackPanel { Spacing = 24 };
        if (q.IsLoading || (!q.HasData && !q.IsError)) stack.Children.Add(new LoadingState(5));
        if (q.IsError) stack.Children.Add(new Alert { Tone = AlertTone.Danger, Title = Loc.T("common.error"), Content = q.Error?.Message });
        if (r is not null) Fill(stack, r);
        Content = stack;
    }

    private static TraceText T => TraceTexts.FromLoc;

    private void Fill(StackPanel stack, RequestDetailDto r)
    {
        var locale = Loc.Language;
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 32 };
        var summary = new DetailSection { Title = Loc.T("requests.detail.summary"), Action = RequestBadges.Status(r.Status, r.HttpStatus) };
        var rows = new StackPanel();
        rows.Children.Add(new DetailRow(Loc.T("requests.col.time"), RequestFormat.DateTime(r.StartedAtUtc, locale, _tableContext.TimeZone)));
        rows.Children.Add(new DetailRow(Loc.T("requests.col.client"), RequestPresentation.RequestClientName(r.ClientKind, r.TokenId, T)));
        rows.Children.Add(new DetailRow(Loc.T("requests.col.token"), r.TokenName ?? TraceFormat.Dash));
        rows.Children.Add(new DetailRow(Loc.T("requests.col.origin"), RequestPresentation.Origin(r.Remote, r.ClientIp, T)));
        rows.Children.Add(new DetailRow(Loc.T("requests.col.provider"), r.ProviderName ?? TraceFormat.Dash));
        if (!string.IsNullOrEmpty(r.AccountId)) rows.Children.Add(new DetailRow(Loc.T("requests.detail.account"), r.AccountName ?? r.AccountId));
        rows.Children.Add(new DetailRow(Loc.T("requests.detail.protocol"), RequestBadges.Protocol(r.InboundProtocol, r.UpstreamProtocol, r.Passthrough, KitStyle.Type12)));
        rows.Children.Add(new DetailRow(Loc.T("requests.detail.requestedModel"), r.RequestedModel ?? TraceFormat.Dash));
        rows.Children.Add(new DetailRow(Loc.T("requests.detail.upstreamModel"), r.UpstreamModel ?? TraceFormat.Dash));
        rows.Children.Add(new DetailRow(Loc.T("requests.detail.responseModel"), RequestBadges.ResponseModel(r.RequestedModel, r.UpstreamModel, r.ResponseModel, r.Status, labeled: false, fontSize: KitStyle.Type12)));
        rows.Children.Add(new DetailRow(Loc.T("requests.detail.systemModel"), r.SystemModelId ?? TraceFormat.Dash));
        rows.Children.Add(new DetailRow(Loc.T("requests.detail.reasoning"), RequestBadges.ReasoningValue(r.ReasoningEffort, r.ReasoningMode, r.ReasoningBudgetTokens)));
        rows.Children.Add(new DetailRow(Loc.T("requests.detail.stream"), r.Stream ? Loc.T("common.yes") : Loc.T("common.no")));
        if (!string.IsNullOrEmpty(r.ServiceTier)) rows.Children.Add(new DetailRow("service_tier", r.ServiceTier));
        if (!string.IsNullOrEmpty(r.UpstreamRequestId)) rows.Children.Add(new DetailRow(Loc.T("requests.detail.upstreamId"), r.UpstreamRequestId, mono: true));
        summary.Body = rows;
        var timing = new DetailSection { Title = Loc.T("requests.detail.timing") };
        var timingRows = new StackPanel();
        timingRows.Children.Add(new DetailRow("TTFB", TraceFormat.Ms(r.TtfbMs)));
        timingRows.Children.Add(new DetailRow("TTFT", TraceFormat.Ms(r.TtftMs)));
        timingRows.Children.Add(new DetailRow(Loc.T("requests.detail.generation"), TraceFormat.Ms(r.GenerationMs)));
        timingRows.Children.Add(new DetailRow(Loc.T("requests.detail.total"), TraceFormat.Ms(r.TotalMs)));
        timingRows.Children.Add(new DetailRow(Loc.T("requests.detail.tps"), RequestFormat.Tps(r.OutputTps)));
        timingRows.Children.Add(new DetailRow(Loc.T("requests.col.tokens"), $"{TraceFormat.Tokens(r.TotalInputTokens)} / {TraceFormat.Tokens(r.TotalOutputTokens)}"));
        timing.Body = timingRows;
        Grid.SetColumn(timing, 1);
        columns.Children.Add(summary);
        columns.Children.Add(timing);
        stack.Children.Add(columns);

        stack.Children.Add(TokenUsageSummaryView.Create(UsageSplit.Of(r)));

        if (!string.IsNullOrEmpty(r.ErrorType) || !string.IsNullOrEmpty(r.ErrorMessage))
            stack.Children.Add(new Alert { Tone = AlertTone.Danger, Title = r.ErrorType ?? Loc.T("requests.detail.error"), Content = r.ErrorMessage });

        if (r.Routing is { } routing) stack.Children.Add(RoutingSection(routing));
        if (r.Privacy is { } privacy) stack.Children.Add(PrivacySection(privacy));
        stack.Children.Add(BillingSection(r));
        var raw = RawItems(r);
        if (raw.Count > 0)
        {
            var accordion = new Accordion { ExpandedIndex = Math.Min(_vm.ExpandedRawIndex, raw.Count - 1) };
            accordion.PropertyChanged += (_, e) =>
            {
                if (e.Property == Accordion.ExpandedIndexProperty) _vm.ExpandedRawIndex = accordion.ExpandedIndex;
            };
            foreach (var (title, content) in raw) accordion.Items.Add(new AccordionItem { Header = title, Content = content }.FillWidth());
            stack.Children.Add(accordion);
        }
    }

    private static Control RoutingSection(Astra.Core.Routing.RoutingReport routing)
    {
        var section = new DetailSection { Title = Loc.T("traces.node.routing"), Action = RequestBadges.Routing(routing) };
        var rows = new StackPanel();
        rows.Children.Add(new DetailRow(Loc.T("requests.routing.grade"), $"{routing.Grade} · {routing.Model}"));
        rows.Children.Add(new DetailRow(Loc.T("traces.kv.routeSource"), RequestPresentation.RouteSource(routing.Source, T)));
        rows.Children.Add(new DetailRow(Loc.T("traces.kv.routeMode"), RequestPresentation.RouteMode(routing.Mode, T)));
        if (routing.Confidence is { } c) rows.Children.Add(new DetailRow(Loc.T("traces.kv.routeConfidence"), TraceFormat.Percent(c)));
        if (!string.IsNullOrEmpty(routing.FallbackReason)) rows.Children.Add(new DetailRow(Loc.T("traces.kv.routeFallback"), RequestPresentation.RouteReason(routing.FallbackReason, T)));
        if (!string.IsNullOrEmpty(routing.AdjustedFrom)) rows.Children.Add(new DetailRow(Loc.T("traces.kv.routeAdjusted"), $"{routing.AdjustedFrom} · {RequestPresentation.Unusable(routing.AdjustedReason, T)}"));
        if (routing.JudgeMs is { } ms) rows.Children.Add(new DetailRow(Loc.T("requests.routing.judgeMs"), TraceFormat.Ms(ms)));
        section.Body = rows;
        return section;
    }

    private static Control PrivacySection(Astra.Core.Privacy.PrivacyReport privacy)
    {
        var badge = new Badge
        {
            Size = BadgeSize.Small,
            Tone = privacy.Blocked ? BadgeTone.Danger : privacy.DryRun ? BadgeTone.Neutral : BadgeTone.Warning,
            Content = privacy.Blocked ? Loc.T("requests.detail.privacyBlocked") : privacy.DryRun ? Loc.T("requests.detail.privacyDryRun") : Loc.T("requests.detail.privacyRedacted", ("count", privacy.Redactions)),
        };
        var section = new DetailSection { Title = Loc.T("requests.detail.privacy"), Action = badge };
        if (privacy.Hits.Count == 0)
        {
            var none = KitStyle.Text(Loc.T("settings.privacy.noHits"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
            none.FontSize = KitStyle.Type12;
            section.Body = none;
            return section;
        }
        var list = new StackPanel { Spacing = 4 };
        foreach (var h in privacy.Hits)
        {
            var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 4 };
            var actionKey = RequestPresentation.PrivacyActionKey(h.Action);
            row.Children.Add(new Badge
            {
                Size = BadgeSize.Small,
                Tone = h.Action == "block" ? BadgeTone.Danger : h.Action == "redact" ? BadgeTone.Warning : BadgeTone.Neutral,
                Content = actionKey is null ? h.Action : Loc.T(actionKey),
            });
            var categoryKey = RequestPresentation.PrivacyCategoryKey(h.Category);
            var text = KitStyle.Text($"{(categoryKey is null ? h.Category : Loc.T(categoryKey))} · {h.RuleId} × {h.Count}", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Foreground);
            text.FontSize = KitStyle.Type12;
            row.Children.Add(text);
            if (h.Samples is { Count: > 0 } samples)
            {
                var sample = new SelectableTextBlock { Text = string.Join("  ", samples), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                sample.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily).Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextMuted);
                row.Children.Add(sample);
            }
            list.Children.Add(row);
        }
        section.Body = list;
        return section;
    }

    private Control BillingSection(RequestDetailDto r)
    {
        var (tone, text) = RequestPresentation.PricingSource(r.PricingSource ?? "none", r.PriceKey, T);
        var source = new Badge
        {
            Size = BadgeSize.Small,
            Tone = tone switch { "orange" => BadgeTone.Warning, "accent" => BadgeTone.Info, "red" => BadgeTone.Danger, _ => BadgeTone.Neutral },
            Content = text,
        };
        var section = new DetailSection { Title = Loc.T("requests.detail.billing"), Action = source };
        var body = new StackPanel();
        var cost = new SelectableTextBlock { Text = TraceFormat.Nanos(r.CostNanoUsd), FontSize = 22, FontWeight = FontWeight.Medium, FontFeatures = FontFeatureCollection.Parse("tnum"), Margin = new Thickness(0, 0, 0, 12) };
        cost.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Foreground);
        body.Children.Add(cost);
        if (r.UsageSource == "missing")
            body.Children.Add(new Alert { Tone = AlertTone.Warning, Title = Loc.T("requests.detail.usageMissing"), Margin = new Thickness(0, 0, 0, 12) });
        if (r.UsageItems.Count > 0) body.Children.Add(UsageTable(r.UsageItems));
        if (!string.IsNullOrEmpty(r.BillingDescription))
        {
            var description = new SelectableTextBlock { Text = r.BillingDescription, FontSize = KitStyle.Type12, TextWrapping = TextWrapping.Wrap, LineHeight = 19.5, Margin = new Thickness(0, 12, 0, 0) };
            description.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
            body.Children.Add(description);
        }
        section.Body = body;
        return section;
    }

    /// <summary>The per-token-type bill (web <c>usageColumns</c> over <c>SortableDataTable</c>).</summary>
    private static Control UsageTable(IReadOnlyList<UsageItemDto> items)
    {
        var table = new SortableDataTable { Caption = Loc.T("requests.detail.billing"), ItemsSource = items.ToList(), RowKeySelector = o => items.ToList().IndexOf((UsageItemDto)o) };
        DataTableColumn Col(string header, string key, Func<UsageItemDto, object?> value, Func<UsageItemDto, Control>? cell = null, bool numeric = false, double width = 0)
        {
            var column = new DataTableColumn { Header = header, Key = key, ValueSelector = o => value((UsageItemDto)o), IsNumeric = numeric ? true : null };
            if (width > 0) column.Width = new GridLength(width);
            if (cell is not null) column.CellTemplate = new FuncDataTemplate<UsageItemDto>((item, _) => item is null ? null : cell(item));
            return column;
        }
        table.Columns.Add(Col(Loc.T("billing.type"), "type", i => i.TokenType, TypeCell));
        table.Columns.Add(Col(Loc.T("billing.tokens"), "tokens", i => i.Tokens, i => Cell(i.IsPerCall ? $"{i.Tokens}×" : TraceFormat.Tokens(i.Tokens), right: true), numeric: true));
        table.Columns.Add(Col(Loc.T("billing.unitPrice"), "unitPrice", i => double.TryParse(i.UnitPrice, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 0,
            i => Cell(RequestFormat.UnitPrice(i.UnitPrice) + (i.Multipliers is { Count: > 0 } m ? $" ({string.Join(", ", m)})" : ""), right: true), numeric: true));
        table.Columns.Add(Col(Loc.T("billing.tier"), "tier", i => i.TierApplied, i => Cell(i.TierApplied)));
        table.Columns.Add(Col(Loc.T("billing.cost"), "cost", i => i.CostNanoUsd, i => Cell(TraceFormat.Nanos(i.CostNanoUsd), right: true), numeric: true));
        return table;
    }

    private static Control Cell(string text, bool right = false)
    {
        var block = KitStyle.Text(text, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground).Num();
        if (right) block.HorizontalAlignment = HorizontalAlignment.Right;
        return block;
    }

    /// <summary>A billed token type: its label over the raw type, tinted for cache reads / writes, with "→ priced as" when billed as another (web <c>TokenTypeLabel</c>).</summary>
    private static Control TypeCell(UsageItemDto item)
    {
        var label = RequestPresentation.TokenTypeLabel(item.TokenType, T);
        var color = item.TokenType == "cache_read" ? ArcDesignTokenKind.Success : item.TokenType.StartsWith("cache_write_", StringComparison.Ordinal) ? ArcDesignTokenKind.Warning : ArcDesignTokenKind.Foreground;
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(KitStyle.Text(label ?? item.TokenType, ArcDesignTokenKind.TypeSM, color));
        if (label is not null)
        {
            var raw = KitStyle.Text(item.TokenType, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
            raw.FontSize = 10.5;
            raw.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
            if (!string.IsNullOrEmpty(item.PricedAs) && item.PricedAs != item.TokenType) raw.Text += " → " + item.PricedAs;
            stack.Children.Add(raw);
        }
        return stack;
    }

    /// <summary>The accordion rows: raw usage, the price snapshot, then each captured body that exists (web <c>raw</c>).</summary>
    private List<(string Title, Control Content)> RawItems(RequestDetailDto r)
    {
        var items = new List<(string, Control)>();
        if (r.UsageRaw is not null) items.Add((Loc.T("requests.detail.usageRaw"), new CodeBlock { Code = RequestFormat.Pretty(r.UsageRaw), Language = "json", IsCollapsible = true, MaxLines = 19, HorizontalAlignment = HorizontalAlignment.Stretch, CopyLabel = Loc.T("common.copy") }));
        if (r.PricingSnapshot is not null) items.Add((Loc.T("requests.detail.pricingSnapshot"), new CodeBlock { Code = RequestFormat.Pretty(r.PricingSnapshot), Language = "json", IsCollapsible = true, MaxLines = 19, HorizontalAlignment = HorizontalAlignment.Stretch, CopyLabel = Loc.T("common.copy") }));
        if (r.Bodies is { } bodies)
        {
            string?[] previews = [bodies.ClientRequest, bodies.UpstreamRequest, bodies.UpstreamRequestHeaders, bodies.UpstreamResponse, bodies.ClientResponse];
            long?[] sizes = [bodies.Sizes?.ClientRequest, bodies.Sizes?.UpstreamRequest, bodies.Sizes?.UpstreamRequestHeaders, bodies.Sizes?.UpstreamResponse, bodies.Sizes?.ClientResponse];
            for (var i = 0; i < RequestPaths.BodyParts.Count; i++)
            {
                if (string.IsNullOrEmpty(previews[i])) continue;
                var (key, part) = RequestPaths.BodyParts[i];
                items.Add((Loc.T("requests.body." + key), new RequestBodyPanel(_vm.Body(key, part), previews[i]!, sizes[i], bodies.PreviewLimit)));
            }
        }
        return items;
    }
}
