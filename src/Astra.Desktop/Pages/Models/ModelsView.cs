using System.ComponentModel;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Pricing;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Models;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Pages.Models;

/// <summary>
/// The body of the model catalog page (web <c>ModelsPage</c> content): a loading skeleton, an error alert, the empty state (also what a
/// search without a hit shows), or the sortable table; plus the "new model" and "sync" sheets the header buttons open.
/// </summary>
public sealed class ModelsView : UserControl
{
    private readonly ModelsPage _page;
    private readonly ContentControl _body = new();
    private readonly CreateModelSheet _create;
    private readonly SyncSheet _sync;
    private SortableDataTable? _table;

    /// <summary>Builds the view for <paramref name="page"/> and keeps it in step with it.</summary>
    public ModelsView(ModelsPage page)
    {
        _page = page;
        _create = new CreateModelSheet(page.Context!, id => page.Open(id));
        _sync = new SyncSheet(page.Context!);
        _create.Closed += (_, _) => page.IsCreating = false;
        _sync.Closed += (_, _) => page.IsSyncOpen = false;
        Content = new StackPanel { Children = { _body, _create, _sync } };
        RenderBody();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _page.PropertyChanged += OnPageChanged;
        Loc.LanguageChanged += OnLanguage;
        _table = null;
        RenderBody();
        _create.IsOpen = _page.IsCreating;
        _sync.IsOpen = _page.IsSyncOpen;
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _page.PropertyChanged -= OnPageChanged;
        Loc.LanguageChanged -= OnLanguage;
    }

    private void OnLanguage() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        _table = null;
        RenderBody();
    });

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ModelsPage.Models) or nameof(ModelsPage.IsLoading) or nameof(ModelsPage.IsError) or nameof(ModelsPage.ErrorText):
                RenderBody();
                break;
            case nameof(ModelsPage.IsCreating):
                _create.IsOpen = _page.IsCreating;
                break;
            case nameof(ModelsPage.IsSyncOpen):
                _sync.IsOpen = _page.IsSyncOpen;
                break;
        }
    }

    private void RenderBody()
    {
        if (_page.IsError)
        {
            _table = null;
            _body.Content = new Alert { Tone = AlertTone.Danger, Title = Loc.T("common.error"), Content = _page.ErrorText };
        }
        else if (_page.Models is null)
        {
            _table = null;
            _body.Content = new LoadingState(5);
        }
        else if (_page.Models.Count == 0)
        {
            _table = null;
            _body.Content = new EmptyStateView(Loc.T("models.empty"), icon: new LucideIcon { Kind = LucideIconKind.Boxes, Size = 24 });
        }
        else
        {
            _table ??= BuildTable();
            _table.ItemsSource = ModelRow.From(_page.Models);
            _body.Content = _table;
        }
    }

    private SortableDataTable BuildTable()
    {
        var table = new SortableDataTable { Caption = Loc.T("nav.models"), RowKeySelector = r => ((ModelRow)r).Id }.WithoutCompactLayout();
        Astra.Desktop.Components.Tokens.Tables.WebSurface(table);
        table.Columns.Add(new DataTableColumn(Loc.T("models.col.model"), r => ((ModelRow)r).Name)
        {
            Key = "name",
            CellTemplate = new FuncDataTemplate<ModelRow>((_, _) => new ModelNameCell(_page), supportsRecycling: true),
        });
        table.Columns.Add(new DataTableColumn(Loc.T("models.col.vendor"), r => ((ModelRow)r).Vendor) { Key = "vendor", Width = new GridLength(120) });
        table.Columns.Add(new DataTableColumn(Loc.T("models.col.context"), r => ((ModelRow)r).ContextText)
        {
            Key = "context", IsNumeric = true, Width = new GridLength(96), SortValueSelector = r => ((ModelRow)r).Context,
        });
        table.Columns.Add(new DataTableColumn(Loc.T("models.col.price"), r => ((ModelRow)r).PriceText)
        {
            Key = "input", IsNumeric = true, Width = new GridLength(150), SortValueSelector = r => ((ModelRow)r).Input,
        });
        table.Columns.Add(new DataTableColumn(Loc.T("models.col.providerPrices"), r => ((ModelRow)r).ProviderPricesText)
        {
            Key = "providerPrices", IsNumeric = true, Width = new GridLength(130), SortValueSelector = r => ((ModelRow)r).ProviderPrices,
        });
        return table;
    }
}

/// <summary>
/// The first cell of the catalog table: the model's name (muted when disabled) with its "custom" / "modified" badges, and the id in
/// mono beneath; the whole cell opens the detail page (web: a <c>button</c> filling the cell). Rows are recycled, so it reads its row from
/// the data context each time it changes.
/// </summary>
// ARC-GAP: SortableDataTable has no row activation event (inventory GAP-4); the web also opens the page from this cell's button, so
// the cell itself is the click target and the rest of the row is inert.
internal sealed class ModelNameCell : Border
{
    private readonly ModelsPage _page;
    private readonly TextBlock _name = new() { FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _badges = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBlock _id = KitStyle.Text(null, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
    private ModelRow? _row;
    private bool _hover;

    public ModelNameCell(ModelsPage page)
    {
        _page = page;
        _name.Token(TextBlock.FontSizeProperty, ArcDesignTokenKind.TypeSM);
        _id.FontSize = 11;
        _id.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        var first = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _name, _badges } };
        Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { first, _id } };
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = true;
        PointerEntered += (_, _) => { _hover = true; ApplyName(); };
        PointerExited += (_, _) => { _hover = false; ApplyName(); };
        PointerPressed += (_, e) =>
        {
            if (_row is not null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) _page.Open(_row.Id);
        };
        KeyDown += (_, e) =>
        {
            if (_row is not null && e.Key is Key.Enter or Key.Space) _page.Open(_row.Id);
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _row = DataContext as ModelRow;
        _name.Text = _row?.Name;
        _id.Text = _row?.Id;
        _badges.Children.Clear();
        if (_row is { IsUserModel: true }) _badges.Children.Add(KitBadge.Create(Loc.T("models.source.user"), Tone.Accent));
        if (_row is { IsModified: true }) _badges.Children.Add(KitBadge.Create(Loc.T("models.modified"), Tone.Orange));
        ApplyName();
    }

    private void ApplyName()
    {
        // text-[var(--text-muted)] when disabled; hover:text-[var(--accent)]
        var kind = _hover ? ArcDesignTokenKind.Accent : _row is { IsDisabled: true } ? ArcDesignTokenKind.TextMuted : ArcDesignTokenKind.Foreground;
        _name.Token(TextBlock.ForegroundProperty, kind);
    }
}
