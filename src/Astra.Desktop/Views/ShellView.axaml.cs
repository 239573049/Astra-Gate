using Astra.Desktop.Core.I18n;
using Astra.Desktop.Platform;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Views;

/// <summary>The shell's content (web AppShell + nav + Page header + ConnectionBanner); the window owns nothing else.</summary>
public partial class ShellView : UserControl
{
    private readonly ServiceStatusView _status = new();
    private ShellViewModel? _vm;
    private PageViewModel? _headerPage;
    private bool _syncing;

    public ShellView()
    {
        InitializeComponent();
        // No recycling: every page has its own view type, and a recycling template makes the presenter keep the first page's view and only
        // swap its DataContext (the "page body never changes after the first navigation" bug).
        PageHost.ContentTemplate = new FuncDataTemplate<PageViewModel>((vm, _) => vm is null ? null : (DataContext as ShellViewModel)?.Navigator.CreateView(vm), supportsRecycling: false);
        SidebarToggle.Content = new LucideIcon { Kind = LucideIconKind.PanelLeft, Size = 16 };
        BackButton.Content = new LucideIcon { Kind = LucideIconKind.ArrowLeft, Size = 16 };
        BuildSidebar();
        // The toolbar is the window's drag region (web Page header `.drag`); the sidebar's own header row registers itself.
        WindowChrome.AttachDragRegion(Toolbar);
        // Windows: the caption buttons overlay the toolbar's right end, which keeps that width free (web TOOLBAR_RIGHT_RESERVE).
        if (WindowChrome.Spec.HasAppDragRegion && WindowChrome.Spec.CaptionOverlayWidth > 0)
            Toolbar.Margin = new Thickness(Toolbar.Margin.Left, Toolbar.Margin.Top, Toolbar.Margin.Right + WindowChrome.Spec.CaptionOverlayWidth, Toolbar.Margin.Bottom);
        Loc.LanguageChanged += OnLanguageChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Loc.LanguageChanged -= OnLanguageChanged;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.Navigator.Changed -= SyncSelection;
            _vm.Navigator.Changed -= SyncPageMode;
            _vm.Navigator.Changed -= SyncPage;
        }
        _vm = DataContext as ShellViewModel;
        if (_vm is null) return;
        _status.DataContext = _vm.Connection;
        _status.Status = _vm.Status;
        _status.StartCommand = _vm.StartServiceCommand;
        var vm = _vm;
        _status.OpenRemote = () => vm.Navigator.Navigate(AppRoute.Settings, "remote");
        _vm.Navigator.Changed += SyncSelection;
        _vm.Navigator.Changed += SyncPageMode;
        _vm.Navigator.Changed += SyncPage;
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ShellViewModel.IsSidebarCollapsed)) SyncCollapsed();
            if (args.PropertyName == nameof(ShellViewModel.CollapseToggleTooltip)) ApplyTexts();
        };
        SyncCollapsed();
        SyncSelection();
        SyncPageMode();
        SyncPage();
        ApplyTexts();
    }

    /// <summary>Shows the navigator's current page (set here rather than bound: see the comment on PageHost in ShellView.axaml).</summary>
    private void SyncPage()
    {
        if (_vm is null) return;
        if (!ReferenceEquals(PageHost.Content, _vm.Navigator.Current)) PageHost.Content = _vm.Navigator.Current;
        SyncHeaderActions();
        // web: a page may pass a <code className="font-mono"> subtitle (a key); the token binding replaces the previous page's either way
        Components.Kit.KitStyle.Token(SubtitleText, TextBlock.FontFamilyProperty, _vm.Navigator.Current.SubtitleMono
            ? TKCode.Arc.DesignTokens.ArcDesignTokenKind.MonoFontFamily : TKCode.Arc.DesignTokens.ArcDesignTokenKind.BodyFontFamily);
    }

    /// <summary>
    /// Shows the current page's header actions (set here rather than bound, like the page host: the bound presenter stayed empty). A page
    /// sets them after it loads, so the page's own change notification is followed too.
    /// </summary>
    private void SyncHeaderActions()
    {
        if (_vm is null) return;
        var page = _vm.Navigator.Current;
        if (!ReferenceEquals(_headerPage, page))
        {
            if (_headerPage is not null) _headerPage.PropertyChanged -= OnHeaderPageChanged;
            _headerPage = page;
            page.PropertyChanged += OnHeaderPageChanged;
        }
        if (!ReferenceEquals(HeaderActionsHost.Content, page.HeaderActions)) HeaderActionsHost.Content = page.HeaderActions;
    }

    private void OnHeaderPageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PageViewModel.HeaderActions)) SyncHeaderActions();
    }

    /// <summary>
    /// A page that scrolls itself (<c>HostScrolls</c> false) is hosted in a plain, padded cell that gives it the body's height; the Arc
    /// scroll area has no non-scrolling mode, so the page host moves between the two cells.
    /// </summary>
    private void SyncPageMode()
    {
        if (_vm is null) return;
        var scrolls = _vm.Navigator.Current.HostScrolls;
        if (scrolls == (PageHost.Parent == PageScroll)) return;
        if (scrolls)
        {
            PageFixed.Child = null;
            PageScroll.Content = PageHost;
        }
        else
        {
            PageScroll.Content = null;
            PageFixed.Child = PageHost;
        }
        PageScroll.IsVisible = scrolls;
        PageFixed.IsVisible = !scrolls;
    }

    private void BuildSidebar()
    {
        Nav.Footer = _status;
        Nav.ItemInvoked += (_, entry) =>
        {
            if (!_syncing) _vm?.Navigator.Navigate(entry.Route);
        };
    }

    /// <summary>The web logo asset (linked into the assembly from web/src/assets).</summary>
    public static Bitmap LoadLogo() => Components.Kit.AppAssets.Logo;

    private void SyncCollapsed()
    {
        if (_vm is null) return;
        Nav.IsCollapsed = _vm.IsSidebarCollapsed;
    }

    /// <summary>Highlights the sidebar entry of the current page's list (a detail page keeps its list selected).</summary>
    private void SyncSelection()
    {
        if (_vm is null) return;
        var nav = NavTable.ForRoute(RouteInfo.Of(_vm.Navigator.Current.Route).NavRoute);
        _syncing = true;
        try
        {
            Nav.SelectedKey = nav?.Key;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnLanguageChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        Nav.RefreshTexts();
        _vm?.OnLanguageChanged();
        ApplyTexts();
    });

    private void ApplyTexts()
    {
        if (_vm is null) return;
        ToolTip.SetTip(SidebarToggle, _vm.CollapseToggleTooltip);
        _status.ApplyTexts();
        ToolTip.SetTip(BackButton, Loc.T("common.back"));
    }
}
