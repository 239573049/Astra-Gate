using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Tokens;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.Overview;

/// <summary>Where a rendered bill goes: the OS save dialog and clipboard in the app, a stand-in in tests.</summary>
internal interface IBillImageTarget
{
    /// <summary>Saves <paramref name="png"/> under a name the user confirms; false when they cancelled.</summary>
    Task<bool> SaveAsync(string suggestedName, Bitmap png);

    /// <summary>Puts <paramref name="png"/> on the clipboard; false when there is no clipboard.</summary>
    Task<bool> CopyAsync(Bitmap png);
}

/// <summary>The save dialog and clipboard of the window that holds <paramref name="owner"/>.</summary>
internal sealed class WindowBillImageTarget(Control owner) : IBillImageTarget
{
    private static readonly FilePickerFileType Png = new("PNG") { Patterns = ["*.png"], MimeTypes = ["image/png"] };

    /// <inheritdoc />
    public async Task<bool> SaveAsync(string suggestedName, Bitmap png)
    {
        if (TopLevel.GetTopLevel(owner)?.StorageProvider is not { } storage) return false;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName, DefaultExtension = "png", FileTypeChoices = [Png], ShowOverwritePrompt = true,
        });
        if (file is null) return false;
        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        png.Save(stream, PngBitmapEncoderOptions.Default);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The caller disposes <paramref name="png"/> as soon as this returns, while Avalonia's clipboard only hands an image over when
    /// something pastes (a promise on macOS, the selection owner on X11, delayed rendering on Windows): pasting then found a dead
    /// bitmap and the clipboard came up empty. So the picture goes over as its own bytes — written at once on macOS
    /// (<see cref="MacPasteboard"/>, which also outlives this process), elsewhere as a bitmap of the clipboard's own that is never
    /// disposed here (the data transfer holds it until it is replaced; disposing a transfer disposes nothing) and, on Windows, rendered
    /// at once by <c>FlushAsync</c>.
    /// </remarks>
    public async Task<bool> CopyAsync(Bitmap png)
    {
        using var encoded = new MemoryStream();
        png.Save(encoded, PngBitmapEncoderOptions.Default);
        if (OperatingSystem.IsMacOS() && MacPasteboard.SetImage(encoded.ToArray())) return true;
        if (TopLevel.GetTopLevel(owner)?.Clipboard is not { } clipboard) return false;
        encoded.Position = 0;
        await clipboard.SetBitmapAsync(new Bitmap(encoded));
        await clipboard.FlushAsync();
        return true;
    }
}

/// <summary>
/// The overview's "daily bill" (web <c>components/bill/DailyBillSheet.tsx</c>): today's spending per provider and model, printed as a receipt
/// (<see cref="ReceiptBillView"/>) or a comic page (<see cref="MangaBillView"/>), copied or saved as a 2x PNG named
/// <see cref="DailyBillShaping.FileName"/>. The style switch is remembered (<see cref="BillPrefs"/>, web localStorage <c>astra.bill.style</c>);
/// the bill is fetched again on every opening (the page invalidates it).
/// </summary>
public sealed class DailyBillSheet : UserControl
{
    private readonly Func<Query<DailyBillDto>> _query;
    private readonly IFeedback _feedback;
    private readonly IBillImageTarget _target;
    private readonly Sheet _sheet = new() { SheetWidth = 720 };
    private readonly Border _switchSlot = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _preview = new() { CornerRadius = new CornerRadius(12), Padding = new Thickness(20) };
    // Built once: the switch slot and the preview are kept controls, so a body made on every opening would add them to a
    // second panel while the first still holds them ("already has a visual parent", .agents/memory/desktop-rebuild-reparent-crash.md).
    private readonly StackPanel _body;
    private ArcButton _copy = new();
    private ArcButton _save = new();
    private QueryHost<DailyBillDto>? _host;
    private Control? _art;
    private bool _busy;

    /// <summary>Builds the (closed) sheet over the bill query <paramref name="query"/> returns.</summary>
    public DailyBillSheet(Func<Query<DailyBillDto>> query, IFeedback feedback) : this(query, feedback, null)
    {
    }

    internal DailyBillSheet(Func<Query<DailyBillDto>> query, IFeedback feedback, IBillImageTarget? target)
    {
        _query = query;
        _feedback = feedback;
        _target = target ?? new WindowBillImageTarget(this);
        // web .rounded-xl.bg-fill-2.p-5: rgb(120 120 128 / .07) light, .14 dark, a scroll box no taller than 62% of the window.
        void Tint() => _preview.Background = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Round((ActualThemeVariant == ThemeVariant.Dark ? 0.14 : 0.07) * 255), 120, 120, 128));
        Tint();
        ActualThemeVariantChanged += (_, _) => Tint();
        _body = new StackPanel { Spacing = 12, Children = { _switchSlot, _preview } };
        _sheet.Content = _body;
        Content = _sheet;
    }

    /// <summary>The style on show.</summary>
    public string Style => BillPrefs.Style;

    /// <summary>The bill control the buttons render (null until the bill arrived).</summary>
    public Control? Art => _art;

    /// <summary>Whether the sheet is open.</summary>
    public bool IsOpen => _sheet.IsOpen;

    /// <summary>Opens the sheet on the current bill query and the remembered style.</summary>
    public void Open()
    {
        // Texts are read at every opening, so a language switch in between shows.
        _sheet.Title = Loc.T("bill.title");
        _sheet.Description = Loc.T("bill.description");
        _copy = Button(Loc.T("bill.copy"), LucideIconKind.Copy, ButtonVariant.Secondary, () => _ = CopyAsync());
        _save = Button(Loc.T("bill.save"), LucideIconKind.Download, ButtonVariant.Primary, () => _ = SaveAsync());
        _sheet.Footer = [_copy, _save];
        _host = new QueryHost<DailyBillDto>(_query(), Render) { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _preview.Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _host,
        };
        BuildSwitch();
        _sheet.IsOpen = true;
    }

    /// <summary>Picks a style (the switch calls this; scenes and tests too): remembered, and the bill is drawn again.</summary>
    public void SetStyle(string style)
    {
        BillPrefs.Style = style;
        BuildSwitch();
        _host?.Rebuild();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            _preview.MaxHeight = Math.Max(240, top.ClientSize.Height * 0.62);
            top.PropertyChanged += OnTopLevelChanged;
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } top) top.PropertyChanged -= OnTopLevelChanged;
    }

    private void OnTopLevelChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TopLevel.ClientSizeProperty && sender is TopLevel top) _preview.MaxHeight = Math.Max(240, top.ClientSize.Height * 0.62);
    }

    private void BuildSwitch()
        => _switchSlot.Child = Pick.Segmented(
            BillStyles.All.Select(s => new SegmentedOption(s, Loc.T(BillStyles.LabelKey(s)))), BillPrefs.Style, SetStyle, Loc.T("bill.style"));

    private Control? Render(Query<DailyBillDto> q)
    {
        _art = null;
        if (q.IsError && !q.HasData)
        {
            UpdateButtons();
            return new Alert { Tone = AlertTone.Danger, Title = Loc.T("common.error"), Content = q.Error?.Message };
        }
        if (!q.HasData)
        {
            UpdateButtons();
            return new LoadingState { Lines = 6 };
        }
        var view = DailyBillShaping.Shape(q.Data!, Loc.Language);
        _art = BillStyles.Normalize(BillPrefs.Style) == BillStyles.Manga ? new MangaBillView(view) : new ReceiptBillView(view);
        UpdateButtons();
        return _art;
    }

    private void UpdateButtons()
    {
        var ready = _art is not null && !_busy;
        _copy.IsEnabled = ready;
        _save.IsEnabled = ready;
    }

    /// <summary>The bill as a 2x PNG (web <c>pixelRatio: 2</c>), or null when nothing is drawn yet.</summary>
    internal Bitmap? Snapshot()
    {
        if (_art is not { } art || art.Bounds.Width <= 0 || art.Bounds.Height <= 0) return null;
        var size = new PixelSize((int)Math.Ceiling(art.Bounds.Width * 2), (int)Math.Ceiling(art.Bounds.Height * 2));
        var bitmap = new RenderTargetBitmap(size, new Vector(192, 192));
        bitmap.Render(art);
        return bitmap;
    }

    /// <summary>Copies the bill as an image (the copy button).</summary>
    internal Task CopyAsync() => RunAsync("bill.copied", "bill.copyFailed", (png, _) => _target.CopyAsync(png));

    /// <summary>Saves the bill as a PNG (the save button).</summary>
    internal Task SaveAsync() => RunAsync("bill.saved", "bill.saveFailed", (png, name) => _target.SaveAsync(name, png));

    private async Task RunAsync(string done, string failed, Func<Bitmap, string, Task<bool>> deliver)
    {
        if (_busy || _query().Data is not { } bill) return;
        _busy = true;
        UpdateButtons();
        try
        {
            using var png = Snapshot();
            if (png is null) return;
            if (await deliver(png, DailyBillShaping.FileName(bill.Date, BillPrefs.Style))) _feedback.Success(Loc.T(done));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            _feedback.Error(Loc.T(failed, ("error", e.Message)));
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    /// <summary>A footer button with a leading 14 px icon (web <c>Button icon=…</c>).</summary>
    private static ArcButton Button(string text, LucideIconKind icon, ButtonVariant variant, Action onClick)
    {
        var button = new ArcButton
        {
            Variant = variant,
            IsEnabled = false,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { new LucideIcon { Kind = icon, Size = 14, VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
            },
        };
        button.Click += (_, _) => onClick();
        return button;
    }
}
