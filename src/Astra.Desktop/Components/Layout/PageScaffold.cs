using System.Windows.Input;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Metadata;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.Layout;

/// <summary>
/// A page (web <c>layout/Page.tsx</c>): a 52px toolbar holding the sidebar toggle, an optional back button and
/// <see cref="Leading"/>, the title with its subtitle, and <see cref="Actions"/> pushed to the right, over a scrolling content
/// area that fades out under the toolbar (Arc <see cref="ScrollArea"/>, web <c>scroll-edge</c>). Details, edits and additions
/// open in a <see cref="Sheet"/>, not on a page of their own.
/// <para>
/// The toolbar is marked <see cref="DragRegion.IsDragRegionProperty"/>: the window chrome turns an empty-area press on it into a
/// window drag on macOS and Windows (a marker only, see <see cref="DragRegion"/>). Buttons and inputs in it keep their clicks.
/// </para>
/// <para>
/// The shell already draws a header of the same measures from the page view model (<c>ShellView</c>); a page that hosts
/// itself in a <see cref="PageScaffold"/> is for hosts without that header (the gallery, tray panel, tests) or a shell that
/// delegates the header to the page, as the web does.
/// </para>
/// </summary>
public sealed class PageScaffold : UserControl
{
    /// <summary>Toolbar height (web <c>h-[52px]</c>).</summary>
    public const double ToolbarHeight = 52;

    /// <summary>Page title (web <c>title</c>).</summary>
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<PageScaffold, string?>(nameof(Title));

    /// <summary>Line under the title (web <c>subtitle</c>).</summary>
    public static readonly StyledProperty<string?> SubtitleProperty = AvaloniaProperty.Register<PageScaffold, string?>(nameof(Subtitle));

    /// <summary>Right side of the toolbar: usually an <see cref="ActionGroup"/> of buttons (web <c>actions</c>).</summary>
    public static readonly StyledProperty<object?> ActionsProperty = AvaloniaProperty.Register<PageScaffold, object?>(nameof(Actions));

    /// <summary>Left of the title, after the toggle and back button (web <c>leading</c>).</summary>
    public static readonly StyledProperty<object?> LeadingProperty = AvaloniaProperty.Register<PageScaffold, object?>(nameof(Leading));

    /// <summary>The scrolling content (web <c>children</c>).</summary>
    public static readonly StyledProperty<object?> BodyProperty = AvaloniaProperty.Register<PageScaffold, object?>(nameof(Body));

    /// <summary>Whether the sidebar toggle shows (web: always; hide it in hosts without a sidebar).</summary>
    public static readonly StyledProperty<bool> ShowSidebarToggleProperty = AvaloniaProperty.Register<PageScaffold, bool>(nameof(ShowSidebarToggle), true);

    /// <summary>Runs when the sidebar toggle is pressed.</summary>
    public static readonly StyledProperty<ICommand?> SidebarToggleCommandProperty = AvaloniaProperty.Register<PageScaffold, ICommand?>(nameof(SidebarToggleCommand));

    /// <summary>Tooltip / accessible name of the toggle; null shows <c>shell.collapseSidebar</c>.</summary>
    public static readonly StyledProperty<string?> SidebarToggleTextProperty = AvaloniaProperty.Register<PageScaffold, string?>(nameof(SidebarToggleText));

    /// <summary>Whether a back button shows after the toggle (a page reached from a list).</summary>
    public static readonly StyledProperty<bool> ShowBackProperty = AvaloniaProperty.Register<PageScaffold, bool>(nameof(ShowBack));

    /// <summary>Runs when the back button is pressed.</summary>
    public static readonly StyledProperty<ICommand?> BackCommandProperty = AvaloniaProperty.Register<PageScaffold, ICommand?>(nameof(BackCommand));

    /// <summary>
    /// Space kept free at the toolbar's right edge (web <c>TOOLBAR_RIGHT_RESERVE</c>): where Windows draws its caption buttons
    /// over the content. Set by the shell from the window chrome.
    /// </summary>
    public static readonly StyledProperty<double> ToolbarRightReserveProperty = AvaloniaProperty.Register<PageScaffold, double>(nameof(ToolbarRightReserve));

    /// <summary>Padding of the scrolling content (web <c>px-2 pt-5 pb-10</c> with a 16px right gutter).</summary>
    public static readonly StyledProperty<Thickness> ContentPaddingProperty = AvaloniaProperty.Register<PageScaffold, Thickness>(nameof(ContentPadding), new Thickness(8, 20, 16, 40));

    private readonly Grid _toolbar;
    private readonly ArcButton _toggle;
    private readonly ArcButton _back;
    private readonly ContentPresenter _leading;
    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly ContentPresenter _actions;
    private readonly ScrollArea _scroll;

    /// <summary>The toolbar element (the drag region); exposed for the shell's chrome logic and tests.</summary>
    public Control Toolbar => _toolbar;

    /// <inheritdoc cref="TitleProperty"/>
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <inheritdoc cref="SubtitleProperty"/>
    public string? Subtitle { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    /// <inheritdoc cref="ActionsProperty"/>
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }

    /// <inheritdoc cref="LeadingProperty"/>
    public object? Leading { get => GetValue(LeadingProperty); set => SetValue(LeadingProperty, value); }

    /// <inheritdoc cref="BodyProperty"/>
    [Content]
    public object? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

    /// <inheritdoc cref="ShowSidebarToggleProperty"/>
    public bool ShowSidebarToggle { get => GetValue(ShowSidebarToggleProperty); set => SetValue(ShowSidebarToggleProperty, value); }

    /// <inheritdoc cref="SidebarToggleCommandProperty"/>
    public ICommand? SidebarToggleCommand { get => GetValue(SidebarToggleCommandProperty); set => SetValue(SidebarToggleCommandProperty, value); }

    /// <inheritdoc cref="SidebarToggleTextProperty"/>
    public string? SidebarToggleText { get => GetValue(SidebarToggleTextProperty); set => SetValue(SidebarToggleTextProperty, value); }

    /// <inheritdoc cref="ShowBackProperty"/>
    public bool ShowBack { get => GetValue(ShowBackProperty); set => SetValue(ShowBackProperty, value); }

    /// <inheritdoc cref="BackCommandProperty"/>
    public ICommand? BackCommand { get => GetValue(BackCommandProperty); set => SetValue(BackCommandProperty, value); }

    /// <inheritdoc cref="ToolbarRightReserveProperty"/>
    public double ToolbarRightReserve { get => GetValue(ToolbarRightReserveProperty); set => SetValue(ToolbarRightReserveProperty, value); }

    /// <inheritdoc cref="ContentPaddingProperty"/>
    public Thickness ContentPadding { get => GetValue(ContentPaddingProperty); set => SetValue(ContentPaddingProperty, value); }

    /// <summary>Creates an empty page.</summary>
    public PageScaffold()
    {
        _toggle = IconButton(LucideIconKind.PanelLeft);
        _back = IconButton(LucideIconKind.ArrowLeft);
        _back.IsVisible = false;
        _leading = new ContentPresenter { VerticalAlignment = VerticalAlignment.Center };
        _title = KitStyle.Text(null, ArcDesignTokenKind.TypeLG, ArcDesignTokenKind.Foreground, FontWeight.Medium);
        _subtitle = KitStyle.Text(null, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
        _subtitle.IsVisible = false;
        _actions = new ContentPresenter { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };

        var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Children = { _title, _subtitle } };
        _toolbar = new Grid
        {
            Height = ToolbarHeight,
            Background = Brushes.Transparent, // a hit-testable surface: the drag region needs presses on empty space
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto"),
            Margin = new Thickness(8, 0, 16, 0),
        };
        Grid.SetColumn(_back, 1);
        Grid.SetColumn(_leading, 2);
        Grid.SetColumn(heading, 3);
        Grid.SetColumn(_actions, 4);
        _toolbar.Children.Add(_toggle);
        _toolbar.Children.Add(_back);
        _toolbar.Children.Add(_leading);
        _toolbar.Children.Add(heading);
        _toolbar.Children.Add(_actions);
        DragRegion.SetIsDragRegion(_toolbar, true);

        _scroll = new ScrollArea { FadeLength = 18, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(_scroll, 1);
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(_toolbar);
        root.Children.Add(_scroll);
        Content = root;

        _toggle.Click += (_, _) => SidebarToggleCommand?.Execute(null);
        _back.Click += (_, _) => BackCommand?.Execute(null);
        ApplyAll();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Loc.LanguageChanged += OnLanguageChanged;
        ApplyToggleText();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Loc.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(ApplyToggleText);

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        var p = change.Property;
        if (p == TitleProperty) _title.Text = Title;
        else if (p == SubtitleProperty) { _subtitle.Text = Subtitle; _subtitle.IsVisible = KitStyle.HasText(Subtitle); }
        else if (p == ActionsProperty) _actions.Content = Actions;
        else if (p == LeadingProperty) _leading.Content = Leading;
        else if (p == BodyProperty) _scroll.Content = Body;
        else if (p == ShowSidebarToggleProperty) _toggle.IsVisible = ShowSidebarToggle;
        else if (p == ShowBackProperty) _back.IsVisible = ShowBack;
        else if (p == SidebarToggleTextProperty) ApplyToggleText();
        else if (p == ToolbarRightReserveProperty) _toolbar.Margin = new Thickness(8, 0, 16 + ToolbarRightReserve, 0);
        else if (p == ContentPaddingProperty) _scroll.Padding = ContentPadding;
    }

    private void ApplyAll()
    {
        _scroll.Padding = ContentPadding;
        ApplyToggleText();
    }

    private void ApplyToggleText()
    {
        var text = SidebarToggleText ?? Loc.T("shell.collapseSidebar");
        ToolTip.SetTip(_toggle, text);
        Avalonia.Automation.AutomationProperties.SetName(_toggle, text);
        var back = Loc.T("common.back");
        ToolTip.SetTip(_back, back);
        Avalonia.Automation.AutomationProperties.SetName(_back, back);
    }

    private static ArcButton IconButton(LucideIconKind kind) => new()
    {
        Variant = ButtonVariant.Ghost,
        Width = 32,
        Height = 32,
        Padding = new Thickness(0),
        VerticalAlignment = VerticalAlignment.Center,
        Content = new LucideIcon { Kind = kind, Size = 16 },
    };
}
