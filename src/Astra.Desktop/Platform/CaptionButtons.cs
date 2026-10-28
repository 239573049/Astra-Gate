using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using AtomWindow = AtomUI.Desktop.Controls.Window;

namespace Astra.Desktop.Platform;

/// <summary>
/// The Windows title-bar overlay of the Electron shell (<c>titleBarOverlay</c>: minimize, maximize / restore and close at the top right,
/// <see cref="ChromeSpec.Toolbar"/> tall, transparent, <c>symbolColor</c> by theme) for a window whose managed title bar is hidden
/// (<see cref="WindowChrome.WithOverlay"/>; plan avalonia-client §22). Drawn in Windows 11's shape: 46 px buttons, 10 px glyphs, a
/// light hover tint and the red close. The buttons carry Avalonia's decoration roles, so Windows treats them as the caption buttons
/// (Snap Layouts open on the maximize button, the Win32 hit test routes their presses back to them). Hidden in full screen, like
/// Electron's overlay.
/// </summary>
public sealed class CaptionButtons : StackPanel
{
    /// <summary>Which caption button.</summary>
    public enum Kind { Minimize, Maximize, Close }

    private readonly AtomWindow _window;
    private readonly CaptionButton _minimize, _maximize, _close;

    public CaptionButtons(AtomWindow window)
    {
        _window = window;
        Orientation = Orientation.Horizontal;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        _minimize = new CaptionButton(Kind.Minimize, () => Invoke(Kind.Minimize));
        _maximize = new CaptionButton(Kind.Maximize, () => Invoke(Kind.Maximize));
        _close = new CaptionButton(Kind.Close, () => Invoke(Kind.Close));
        WindowDecorationProperties.SetElementRole(_minimize, WindowDecorationsElementRole.MinimizeButton);
        WindowDecorationProperties.SetElementRole(_maximize, WindowDecorationsElementRole.MaximizeButton);
        WindowDecorationProperties.SetElementRole(_close, WindowDecorationsElementRole.CloseButton);
        Children.Add(_minimize);
        Children.Add(_maximize);
        Children.Add(_close);

        window.ActualThemeVariantChanged += (_, _) => ApplyTheme();
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty) ApplyState();
        };
        ApplyTheme();
        ApplyState();
    }

    /// <summary>The buttons, in their order: minimize, maximize / restore, close.</summary>
    internal IReadOnlyList<Control> Buttons => [_minimize, _maximize, _close];

    /// <summary>True while the maximize button shows the restore glyph (the window is maximized).</summary>
    internal bool ShowsRestore => _maximize.Restore;

    /// <summary>What a press on <paramref name="kind"/> does (Electron's overlay: minimize, zoom, close the window).</summary>
    internal void Invoke(Kind kind)
    {
        switch (kind)
        {
            case Kind.Minimize: _window.WindowState = WindowState.Minimized; break;
            case Kind.Maximize: _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; break;
            case Kind.Close: _window.Close(); break;
        }
    }

    private void ApplyTheme()
    {
        var dark = _window.ActualThemeVariant == ThemeVariant.Dark;
        var symbol = new SolidColorBrush(Color.Parse(ChromeSpec.OverlaySymbolColor(dark)));
        // Windows 11 caption buttons: a faint tint of the symbol colour on hover, fainter when pressed; the close button turns red.
        var hover = new SolidColorBrush(dark ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0x00, 0x00, 0x00));
        var pressed = new SolidColorBrush(dark ? Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0A, 0x00, 0x00, 0x00));
        foreach (var button in new[] { _minimize, _maximize }) button.SetPalette(symbol, hover, pressed, symbol, symbol);
        var closeHover = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
        var closePressed = new SolidColorBrush(Color.FromArgb(0xE6, 0xC4, 0x2B, 0x1C));
        _close.SetPalette(symbol, closeHover, closePressed, Brushes.White, Brushes.White);
    }

    private void ApplyState()
    {
        IsVisible = _window.WindowState != WindowState.FullScreen;
        _maximize.Restore = _window.WindowState == WindowState.Maximized;
    }

    /// <summary>One button: a hit-testable box with a 10 px stroked glyph; pointer state is tracked by hand (no focus, no template).</summary>
    private sealed class CaptionButton : Border
    {
        private const double GlyphSize = 10;
        private readonly Kind _kind;
        private readonly Action _click;
        private readonly Avalonia.Controls.Shapes.Path _glyph = new() { Width = GlyphSize, Height = GlyphSize, StrokeThickness = 1, StrokeLineCap = PenLineCap.Flat, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private IBrush? _symbol, _hoverBackground, _pressedBackground, _hoverSymbol, _pressedSymbol;
        private bool _over, _pressed, _restore;

        public CaptionButton(Kind kind, Action click)
        {
            _kind = kind;
            _click = click;
            Width = ChromeSpec.CaptionButtonWidth;
            Height = ChromeSpec.Toolbar;
            Background = Brushes.Transparent;
            Child = _glyph;
            UpdateGlyph();
        }

        /// <summary>The maximize button shows the restore glyph while the window is maximized.</summary>
        public bool Restore
        {
            get => _restore;
            set
            {
                if (_restore == value) return;
                _restore = value;
                UpdateGlyph();
            }
        }

        public void SetPalette(IBrush symbol, IBrush hoverBackground, IBrush pressedBackground, IBrush hoverSymbol, IBrush pressedSymbol)
        {
            _symbol = symbol;
            _hoverBackground = hoverBackground;
            _pressedBackground = pressedBackground;
            _hoverSymbol = hoverSymbol;
            _pressedSymbol = pressedSymbol;
            Paint();
        }

        private void UpdateGlyph()
        {
            // Windows 11's Segoe Fluent caption glyphs, drawn as 1 px strokes on half-pixel positions so horizontal and vertical lines stay crisp.
            _glyph.Data = StreamGeometry.Parse(_kind switch
            {
                Kind.Minimize => "M0,5.5 H10",
                Kind.Maximize when _restore => "M2.5,2.5 V0.5 H9.5 V7.5 H7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z",
                Kind.Maximize => "M0.5,0.5 H9.5 V9.5 H0.5 Z",
                _ => "M0.5,0.5 L9.5,9.5 M9.5,0.5 L0.5,9.5",
            });
        }

        private void Paint()
        {
            Background = _pressed && _over ? _pressedBackground : _over ? _hoverBackground : Brushes.Transparent;
            _glyph.Stroke = _pressed && _over ? _pressedSymbol : _over ? _hoverSymbol : _symbol;
        }

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            base.OnPointerEntered(e);
            _over = true;
            Paint();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _over = false;
            Paint();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _pressed = true;
            e.Pointer.Capture(this);
            e.Handled = true;
            Paint();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_pressed) return;
            _pressed = false;
            e.Pointer.Capture(null);
            var inside = Bounds.WithX(0).WithY(0).Contains(e.GetPosition(this));
            Paint();
            if (inside) _click();
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            _pressed = false;
            Paint();
        }
    }
}
