using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Pricing;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Models;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Pages.Models;

/// <summary>
/// "Sync model catalog" (web <c>SyncSheet</c> / <c>SyncGroups</c> / <c>SyncDiff</c> of web/src/pages/ModelsPage.tsx): fetches the models.dev
/// preview, lists the pending changes grouped by model with tri-state checkboxes and a leaf diff per change, and applies the ticked
/// ones. Like the web, opening the sheet does not load anything: the person presses "Check for updates" (or Refresh) first. Changes that would
/// overwrite a user's edit are listed but start unticked and carry a "modified by you" badge.
/// </summary>
public sealed class SyncSheet : UserControl
{
    private readonly PageContext _ctx;
    private readonly WideSheet _sheet = new() { SheetWidth = 720 };
    private readonly ContentControl _body = new();
    private SyncPreviewDto? _preview;
    private string? _error;
    private bool _loading;
    private bool _applying;
    private SyncSelection _selection = new();
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private readonly List<(Checkbox Box, IReadOnlyCollection<string> Ids)> _checks = [];
    private bool _syncing;
    private TextBlock? _summary;
    private ArcButton _refresh = null!;
    private ArcButton _apply = null!;

    /// <summary>A closed sheet over the app context.</summary>
    public SyncSheet(PageContext context)
    {
        _ctx = context;
        Content = _sheet;
        _sheet.Title = Loc.T("models.sync");
        _sheet.Description = Loc.T("models.syncDetail");
        _sheet.Content = _body;
        _sheet.Closed += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
        BuildFooter();
        Render();
    }

    /// <summary>Opens or closes the sheet.</summary>
    public bool IsOpen
    {
        get => _sheet.IsOpen;
        set => _sheet.IsOpen = value;
    }

    /// <summary>Raised when the sheet closes.</summary>
    public event EventHandler? Closed;

    /// <summary>The underlying sheet (for the visual harness).</summary>
    public WideSheet Sheet => _sheet;

    /// <summary>The selection (for tests).</summary>
    public SyncSelection Selection => _selection;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Loc.LanguageChanged += OnLanguage;
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Loc.LanguageChanged -= OnLanguage;
    }

    private void OnLanguage() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        _sheet.Title = Loc.T("models.sync");
        _sheet.Description = Loc.T("models.syncDetail");
        BuildFooter();
        Render();
    });

    /// <summary>Fetches the preview (web <c>load</c>): every change that does not overwrite a user edit starts ticked.</summary>
    public async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        _error = null;
        Render();
        try
        {
            _preview = await _ctx.Api.PostAsync<SyncPreviewDto>("/api/models/sync/preview");
            _selection = new SyncSelection(_preview is null ? [] : ModelSync.InitialSelection(_preview));
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
        {
            // a failed mutation has no data (web: preview.isError, preview.data undefined): the old list and its ticks go
            _error = e.Message;
            _preview = null;
            _selection = new SyncSelection();
        }
        finally
        {
            _loading = false;
            Render();
        }
    }

    private void BuildFooter()
    {
        _refresh = PricingUi.Button(Loc.T("common.refresh"), PricingUi.Look.Plain, click: () => _ = LoadAsync());
        var cancel = PricingUi.Button(Loc.T("common.cancel"), click: () => IsOpen = false);
        _apply = PricingUi.Button(Loc.T("models.sync.apply", ("count", _selection.Count)), PricingUi.Look.Primary, click: () => _ = ApplyAsync());
        // web: Refresh has mr-auto, i.e. it is pushed to the left end of the footer (the kit's split footer widens Arc's actions presenter)
        _sheet.Actions = SheetFooter.Split(_refresh, [cancel, _apply]);
    }

    private void UpdateFooter()
    {
        _refresh.IsLoading = _loading;
        _apply.Content = Loc.T("models.sync.apply", ("count", _selection.Count));
        _apply.IsEnabled = _selection.Count > 0;
        _apply.IsLoading = _applying;
        if (_summary is not null && _preview is not null)
            _summary.Text = _selection.Summary(_preview.Changes.Count, ModelSync.Group(_preview.Changes).Count);
    }

    private async Task ApplyAsync()
    {
        if (_applying || _selection.Count == 0) return;
        _applying = true;
        UpdateFooter();
        try
        {
            var result = await _ctx.Api.PostAsync<SyncApplyRequest, SyncApplyResult>("/api/models/sync/apply", new SyncApplyRequest { ChangeIds = _selection.Ids() });
            ModelQueries.Invalidate(_ctx.Queries);
            _ctx.Feedback.Success(Loc.T("models.sync.applied", ("count", result?.Applied ?? 0)));
            IsOpen = false;
            // preview.reset(): the next opening starts from "Check for updates" again; the ticked set goes with it
            _preview = null;
            _error = null;
            _selection = new SyncSelection();
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
        {
            _ctx.Feedback.Error(e.Message);
        }
        finally
        {
            _applying = false;
            Render();
        }
    }

    // ---------- rendering ----------

    private void Render()
    {
        _checks.Clear();
        _summary = null;
        if (_loading) _body.Content = new LoadingState(4);
        else if (_error is not null) _body.Content = new Alert { Tone = AlertTone.Danger, Title = Loc.T("common.error"), Content = _error };
        else if (_preview is null)
        {
            var check = PricingUi.Button(Loc.T("models.sync.check"), PricingUi.Look.Primary, click: () => _ = LoadAsync());
            check.HorizontalAlignment = HorizontalAlignment.Center;
            _body.Content = new Border { Padding = new Thickness(0, 24), Child = check };
        }
        else if (_preview.Changes.Count == 0) _body.Content = new Alert { Tone = AlertTone.Success, Title = Loc.T("models.sync.none") };
        else _body.Content = BuildList(_preview.Changes);
        UpdateFooter();
    }

    private Control BuildList(List<SyncChangeDto> changes)
    {
        var groups = ModelSync.Group(changes);
        var allIds = changes.Select(c => c.Id).ToList();

        var list = new StackPanel();
        for (var i = 0; i < groups.Count; i++)
        {
            if (i > 0) list.Children.Add(Line());
            list.Children.Add(GroupRow(groups[i]));
        }

        // select-all header (web: sticky top row of the card)
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, Margin = new Thickness(16, 8) };
        var all = Check(allIds, Loc.T("models.sync.selectAll"));
        header.Children.Add(all);
        _summary = PricingUi.Muted12(_selection.Summary(allIds.Count, groups.Count));
        _summary.FontSize = 11;
        _summary.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_summary, 1);
        header.Children.Add(_summary);

        var scroll = new ScrollViewer { MaxHeight = 480, Content = list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var card = new Border { BorderThickness = new Thickness(0.5), ClipToBounds = true, Child = new StackPanel { Children = { header, Line(), scroll } } };
        Astra.Desktop.Components.Tokens.CardPanel.Themed(card, Astra.Desktop.Components.Tokens.CardPanel.SurfaceBrush);
        card.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border).Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusPanel);
        return card;
    }

    private static Border Line()
    {
        var line = new Border { Height = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        line.Token(Border.BackgroundProperty, ArcDesignTokenKind.Border);
        return line;
    }

    /// <summary>A checkbox over <paramref name="ids"/>: its state follows the selection and a click ticks or clears them all.</summary>
    private Checkbox Check(IReadOnlyCollection<string> ids, string name)
    {
        var box = new Checkbox { VerticalAlignment = VerticalAlignment.Top, MinHeight = 0, MinWidth = 0 };
        AutomationProperties.SetName(box, name);
        box.CheckedState = ToArc(_selection.StateOf(ids));
        box.CheckedStateChanged += (_, _) =>
        {
            if (_syncing) return;
            _selection.Toggle(ids);
            Resync();
        };
        _checks.Add((box, ids));
        return box;
    }

    private static CheckboxState ToArc(CheckState state) => state switch
    {
        CheckState.Checked => CheckboxState.Checked,
        CheckState.Mixed => CheckboxState.Indeterminate,
        _ => CheckboxState.Unchecked,
    };

    private void Resync()
    {
        _syncing = true;
        try
        {
            foreach (var (box, ids) in _checks) box.CheckedState = ToArc(_selection.StateOf(ids));
        }
        finally
        {
            _syncing = false;
        }
        UpdateFooter();
    }

    private Control GroupRow(SyncGroup group)
    {
        var ids = group.Ids;
        var open = !_collapsed.Contains(group.ModelId);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        var box = Check(ids, group.ModelId);
        box.Margin = new Thickness(0, 2, 0, 0);
        row.Children.Add(box);

        var column = new StackPanel { Spacing = 6 };
        if (group.IsSingle)
        {
            column.Children.Add(Model(group.ModelId));
            column.Children.Add(Body(group.Items[0]));
        }
        else
        {
            var chevron = PricingUi.Icon(LucideIconKind.ChevronRight, 14);
            chevron.Token(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, ArcDesignTokenKind.TextMuted);
            if (open) chevron.RenderTransform = new RotateTransform(90);
            var toggle = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 4, Background = Brushes.Transparent };
            toggle.Children.Add(chevron);
            var model = Model(group.ModelId);
            Grid.SetColumn(model, 1);
            toggle.Children.Add(model);
            var count = PricingUi.Muted12(Loc.T("models.sync.groupCount", ("n", group.Items.Count)));
            count.FontSize = 11;
            count.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(count, 2);
            toggle.Children.Add(count);
            toggle.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(toggle).Properties.IsLeftButtonPressed) return;
                if (!_collapsed.Remove(group.ModelId)) _collapsed.Add(group.ModelId);
                Render();
            };
            toggle.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            column.Children.Add(toggle);

        }
        Grid.SetColumn(column, 1);
        row.Children.Add(column);
        var item = new StackPanel { Children = { row } };
        if (!group.IsSingle && open)
        {
            var children = new StackPanel { Spacing = 8 };
            foreach (var change in group.Items)
            {
                var childRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
                var childBox = Check([change.Id], $"{group.ModelId} {ModelSync.Label(change)}");
                childBox.Margin = new Thickness(0, 2, 0, 0);
                childRow.Children.Add(childBox);
                var body = Body(change);
                Grid.SetColumn(body, 1);
                childRow.Children.Add(body);
                children.Children.Add(childRow);
            }
            // web: ul mt-2 ml-7 border-l pl-3
            var nested = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(12, 0, 0, 0), Margin = new Thickness(28, 8, 0, 0), Child = children };
            nested.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border);
            item.Children.Add(nested);
        }
        return new Border { Padding = new Thickness(16, 10), Child = item };
    }

    private static TextBlock Model(string id)
    {
        var text = PricingUi.Text(id, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Foreground, FontWeight.Medium, mono: true);
        text.FontSize = 12;
        ToolTip.SetTip(text, id);
        return text;
    }

    /// <summary>The badges and the diff of one change (web <c>body</c>).</summary>
    private static Control Body(SyncChangeDto change)
    {
        var column = new StackPanel { Spacing = 4 };
        var badges = new WrapPanel { ItemSpacing = 4, LineSpacing = 4 };
        badges.Children.Add(KitBadge.Create(ModelSync.Label(change), change.Kind == "new_model" ? Tone.Green : Tone.Neutral));
        if (change.SkippedBecauseUserModified) badges.Children.Add(KitBadge.Create(Loc.T("models.sync.userModified"), Tone.Orange));
        column.Children.Add(badges);
        if (change.Kind != "new_model") column.Children.Add(Diff(change.Before, change.After));
        return column;
    }

    private static Control Diff(System.Text.Json.Nodes.JsonNode? before, System.Text.Json.Nodes.JsonNode? after)
    {
        var lines = ModelSync.DiffLines(before, after);
        var column = new StackPanel { Spacing = 2 };
        foreach (var line in lines.Take(ModelSync.DiffLinesShown))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (line.Path.Length > 0) row.Children.Add(Mono(line.Path, ArcDesignTokenKind.TextSecondary));
            if (line.Before is not null)
            {
                var old = Mono(SyncDiffLine.LeafText(line.Before), ArcDesignTokenKind.Danger);
                old.TextDecorations = TextDecorations.Strikethrough;
                old.Opacity = 0.8;
                row.Children.Add(old);
            }
            if (line.Before is not null && line.After is not null) row.Children.Add(Mono("→", ArcDesignTokenKind.TextSecondary));
            if (line.After is not null) row.Children.Add(Mono(SyncDiffLine.LeafText(line.After), ArcDesignTokenKind.Success));
            column.Children.Add(row);
        }
        var hidden = lines.Count - ModelSync.DiffLinesShown;
        if (hidden > 0) column.Children.Add(Mono(Loc.T("models.sync.more", ("n", hidden)), ArcDesignTokenKind.TextSecondary));
        ToolTip.SetTip(column, ModelSync.Tooltip(before, after));
        return column;
    }

    private static TextBlock Mono(string text, ArcDesignTokenKind color)
    {
        var block = PricingUi.Text(text, ArcDesignTokenKind.TypeXS, color, mono: true);
        block.FontSize = 11;
        return block;
    }
}
