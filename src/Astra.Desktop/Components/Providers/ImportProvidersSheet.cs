using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Providers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.Providers;

/// <summary>
/// "Import providers from other apps" (ImportProvidersSheet.tsx): a modal sheet (web: 680 px) that previews a read-only scan (the server
/// never sends keys, only masks), lets the user tick entries (only brand-new ones start ticked) and imports them; a 409 means a source
/// changed since the preview, so the server's message is shown and the scan repeats. Rules in <see cref="ImportLogic"/>.
/// </summary>
internal sealed class ImportProvidersSheet : UserControl
{
    private readonly IProviderSheetHost _host;
    private readonly Sheet _sheet = new() { SheetWidth = 680, Size = ControlSize.Large };
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly StackPanel _list = new() { Spacing = 16 };
    private readonly ContentControl _status = new();
    private readonly StackPanel _error = new();
    private readonly SearchField _search = new();
    private ArcButton _rescan = null!;
    private ArcButton _import = null!;
    private string _query = "";
    private string? _errorText;
    private bool _importing;

    /// <summary>Builds the (closed) sheet.</summary>
    public ImportProvidersSheet(IProviderSheetHost host)
    {
        _host = host;
        Content = _sheet;
        _sheet.Title = Loc.T("import.title");
        _sheet.Description = Loc.T("import.detail");
        _sheet.Closed += (_, _) => _host.Queries.ImportSources.Enabled = false;
        Build();
    }

    // The query is shared and outlives the page: follow it only while attached, or it keeps every Providers page alive.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _host.Queries.ImportSources.PropertyChanged += OnSourcesChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _host.Queries.ImportSources.PropertyChanged -= OnSourcesChanged;
    }

    // Every (re)scan resets the selection to the defaults: only brand-new, enabled entries are ticked.
    private void OnSourcesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => OnSources();

    /// <summary>Selected keys (for tests).</summary>
    public IReadOnlyCollection<string> Selected => _selected;

    /// <summary>Opens the sheet and scans.</summary>
    public void Open()
    {
        _query = "";
        _search.Text = "";
        _errorText = null;
        _selected.Clear();
        foreach (var k in ImportLogic.DefaultSelection(_host.Queries.ImportSources.Data)) _selected.Add(k);
        _host.Queries.ImportSources.Enabled = true;
        Render();
        _sheet.IsOpen = true;
    }

    private DateTimeOffset? _lastData;

    private void OnSources()
    {
        var q = _host.Queries.ImportSources;
        if (q.DataUpdatedAt != _lastData)
        {
            _lastData = q.DataUpdatedAt;
            _selected.Clear();
            foreach (var k in ImportLogic.DefaultSelection(q.Data)) _selected.Add(k);
        }
        Render();
    }

    private void Build()
    {
        _search.Placeholder = Loc.T("import.search");
        _search.TextChanged += (_, _) =>
        {
            if ((_search.Text ?? "") == _query) return;
            _query = _search.Text ?? "";
            RenderList();
        };
        _rescan = ProviderUi.Button(Loc.T("import.rescan"), LucideIconKind.RefreshCw, onClick: () => _ = _host.Queries.ImportSources.RefetchAsync());
        _import = ProviderUi.Button(Loc.T("import.selected", ("count", 0)), variant: ButtonVariant.Primary, onClick: Submit);
        _sheet.Actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { _rescan, _import } };
        var scroll = new ScrollViewer { MaxHeight = 460, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = new Border { Padding = new Thickness(0, 0, 14, 0), Child = _list } };
        var foot = ProviderUi.Hint(Loc.T("import.footnote"));
        _sheet.Content = new StackPanel { Spacing = 16, Children = { _search, _error, _status, scroll, foot } };
    }

    private void Render()
    {
        var q = _host.Queries.ImportSources;
        _error.Children.Clear();
        if (_errorText is not null) _error.Children.Add(new Alert { Tone = AlertTone.Danger, Title = Loc.T("common.error"), Content = _errorText });
        if (q.IsError) _error.Children.Add(new Alert { Tone = AlertTone.Danger, Title = Loc.T("common.error"), Content = q.Error?.Message });
        _status.Content = q.IsLoading || (!q.HasData && !q.IsError) ? new LoadingState(3) : null;
        _status.IsVisible = _status.Content is not null;
        _error.IsVisible = _error.Children.Count > 0;
        _rescan.IsEnabled = !q.IsFetching && !_importing;
        RenderList();
    }

    private void RenderList()
    {
        _list.Children.Clear();
        var sources = _host.Queries.ImportSources.Data ?? [];
        foreach (var s in sources) _list.Children.Add(SourceSection(s));
        var chosen = ImportLogic.Chosen(sources, _selected);
        _import.Content = Loc.T("import.selected", ("count", chosen.Count));
        _import.IsEnabled = chosen.Count > 0 && !_importing;
    }

    private Control SourceSection(ImportSourceDto source)
    {
        var items = source.Items.Where(c => ImportLogic.Matches(c, _query)).ToList();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(4, 0, 0, 6) };
        head.Children.Add(KitStyle.Text(source.Name, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium));
        if (!string.IsNullOrEmpty(source.Path))
        {
            var path = KitStyle.Text(source.Path, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
            path.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
            ToolTip.SetTip(path, Loc.T("import.path", ("path", source.Path)));
            head.Children.Add(path);
        }

        Control body;
        if (!string.IsNullOrEmpty(source.Error)) body = new Alert { Tone = AlertTone.Danger, Title = Loc.T("import.sourceError", ("error", source.Error)) };
        else if (!source.Found) body = Note(Loc.T("import.notFound"));
        else if (items.Count == 0) body = Note(_query.Trim().Length > 0 ? Loc.T("import.noMatch") : Loc.T("import.noItems"));
        else
        {
            var rows = new StackPanel();
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0) rows.Children.Add(Separator());
                rows.Children.Add(Row(items[i]));
            }
            var card = new Border { BorderThickness = new Thickness(0.5), ClipToBounds = true, Child = rows };
            card.Token(Border.BackgroundProperty, ArcDesignTokenKind.Surface).Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border).Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusPanel);
            body = card;
        }
        return new StackPanel { Children = { head, body } };
    }

    private static Control Note(string text) => ProviderUi.Secondary(text).With(t => t.Margin = new Thickness(4, 0, 0, 0));

    private static Control Separator()
    {
        var line = new Border { Height = 0.5 };
        line.Token(Border.BackgroundProperty, ArcDesignTokenKind.Border);
        return line;
    }

    private Control Row(ImportCandidateDto c)
    {
        var enabled = ImportLogic.Selectable(c);
        var key = ImportLogic.Key(c);
        var box = new Checkbox { IsChecked = _selected.Contains(key) && enabled, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        Avalonia.Automation.AutomationProperties.SetName(box, c.Name);
        box.IsCheckedChanged += (_, _) =>
        {
            if (box.IsChecked == true) _selected.Add(key); else _selected.Remove(key);
            var sources = _host.Queries.ImportSources.Data ?? [];
            var n = ImportLogic.Chosen(sources, _selected).Count;
            _import.Content = Loc.T("import.selected", ("count", n));
            _import.IsEnabled = n > 0 && !_importing;
        };

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var name = KitStyle.Text(c.Name, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium);
        ToolTip.SetTip(name, c.Name);
        title.Children.Add(name);
        title.Children.Add(KitBadge.Create(Loc.T("import.status." + c.Status), ToTone(ImportLogic.ToneOf(c.Status))));
        if (!string.IsNullOrEmpty(c.FromApp)) title.Children.Add(KitStyle.Text(c.FromApp, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted));

        var info = new StackPanel { Spacing = 2 };
        info.Children.Add(title);
        var protocols = ImportLogic.ProtocolsLine(c);
        if (protocols.Length > 0)
        {
            var p = KitStyle.Text(protocols, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
            p.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
            ToolTip.SetTip(p, protocols);
            info.Children.Add(p);
        }
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var keyText = KitStyle.Text(c.HasKey ? (c.KeyMasked ?? "••••") : Loc.T("import.noKey"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
        keyText.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        meta.Children.Add(keyText);
        if (c.Models.Count > 0) meta.Children.Add(KitStyle.Text(Loc.T("import.models", ("count", c.Models.Count)), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted));
        if (!string.IsNullOrEmpty(c.TemplateId)) meta.Children.Add(KitStyle.Text(Loc.T("import.template", ("id", c.TemplateId)), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted));
        info.Children.Add(meta);
        if (ImportLogic.Hint(c) is { } hint) info.Children.Add(ProviderUi.Hint(hint).Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary).With(t => t.Margin = new Thickness(0, 2, 0, 0)));
        if (c.IgnoredFields.Count > 0)
            info.Children.Add(ProviderUi.Hint(Loc.T("import.ignored", ("fields", string.Join(", ", c.IgnoredFields)))).Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Warning).With(t => t.Margin = new Thickness(0, 2, 0, 0)));

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, Margin = new Thickness(16, 12), Opacity = enabled ? 1 : 0.6 };
        row.Children.Add(box);
        Grid.SetColumn(info, 1);
        row.Children.Add(info);
        return row;
    }

    private static Tone ToTone(ImportTone t) => t switch { ImportTone.Green => Tone.Green, ImportTone.Orange => Tone.Orange, ImportTone.Red => Tone.Red, _ => Tone.Neutral };

    private async void Submit()
    {
        var chosen = ImportLogic.Chosen(_host.Queries.ImportSources.Data, _selected);
        if (chosen.Count == 0) return;
        _errorText = null;
        _importing = true;
        Render();
        try
        {
            var result = await _host.Queries.ImportAsync(chosen);
            if (result is not null) _host.Feedback.Success(ImportLogic.DoneText(result));
            _sheet.IsOpen = false;
        }
        catch (Exception ex)
        {
            // A 409 means a source changed since the preview: show the server's message and rescan.
            _errorText = ex.Message;
            _ = _host.Queries.ImportSources.RefetchAsync();
        }
        finally
        {
            _importing = false;
            Render();
        }
    }
}
