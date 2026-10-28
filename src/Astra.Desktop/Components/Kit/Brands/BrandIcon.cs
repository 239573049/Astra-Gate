using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Components.Kit;

/// <summary>What a <see cref="BrandIcon"/> stands for.</summary>
public enum BrandKind
{
    /// <summary>A provider: <c>Id</c> is the template's <c>icon</c> (or a plugin's own picture), <c>Label</c> the provider name (initials, region badge).</summary>
    Provider,

    /// <summary>A client: <c>Id</c> is the client kind (<c>claude-code</c>, <c>plugin-foo</c>, ...), <c>Label</c> an optional label for unknown kinds.</summary>
    Client,

    /// <summary>A model family: <c>Id</c> is the model id; the brand is guessed from its name.</summary>
    Model,

    /// <summary>A wire protocol: <c>Id</c> is <c>openai-chat</c> / <c>openai-responses</c> / <c>anthropic</c> / <c>gemini</c>.</summary>
    Protocol,
}

/// <summary>Whether a brand shows as a bare mark or on its tile.</summary>
public enum BrandAppearance
{
    /// <summary>The web's default per kind: providers and clients on a tile, models and protocols as a mark.</summary>
    Auto,

    /// <summary>The brand's tile (web <c>ProviderIcon</c> / <c>ClientIcon</c>).</summary>
    Tile,

    /// <summary>The bare mark in the foreground color (web <c>ClientGlyph</c> / <c>ModelGlyph</c> / <c>ProtocolGlyph</c>).</summary>
    Glyph,
}

/// <summary>
/// The web's provider / client / model / protocol icons (web/src/components/icons.tsx; plan inventory GAP-1). The marks are the
/// same path data @lobehub/icons ships, generated into <c>BrandMarkData.g.cs</c> by <c>scripts/gen-desktop-brand-icons.mjs</c>
/// and drawn as Avalonia geometry (AOT-safe, no SVG parser); vendors without a lobehub mark use the logo PNGs the web bundles.
/// Region and client badges, the initials tile for unknown providers and the puzzle tile of agent plugins follow the web.
/// Not supported: a plugin provider's <c>https:</c> icon (it needs a download; shows initials), data URLs of raster images are.
/// <code>
/// new BrandIcon { Kind = BrandKind.Provider, Id = "anthropic", Label = "Anthropic", Size = 28 }
/// BrandIcon.Client("claude-code", 28)   BrandIcon.Model("gpt-5")   BrandIcon.Protocol("anthropic")
/// </code>
/// </summary>
public sealed class BrandIcon : Decorator
{
    /// <summary>What the icon stands for.</summary>
    public static readonly StyledProperty<BrandKind> KindProperty = AvaloniaProperty.Register<BrandIcon, BrandKind>(nameof(Kind));

    /// <summary>Template icon id, client kind, model id or protocol (see <see cref="BrandKind"/>).</summary>
    public static readonly StyledProperty<string?> IdProperty = AvaloniaProperty.Register<BrandIcon, string?>(nameof(Id));

    /// <summary>Provider name (for initials and the CN / international badge) or a client's display name.</summary>
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<BrandIcon, string?>(nameof(Label));

    /// <summary>Edge length in px; 0 uses the web's default for the kind (28 tile, 18 client glyph, 16 model, 11 protocol).</summary>
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<BrandIcon, double>(nameof(Size));

    /// <summary>Tile or bare mark.</summary>
    public static readonly StyledProperty<BrandAppearance> AppearanceProperty = AvaloniaProperty.Register<BrandIcon, BrandAppearance>(nameof(Appearance));

    /// <summary>Color of mono marks; null uses the foreground token (the secondary text token for protocols).</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<BrandIcon, IBrush?>(nameof(Foreground));

    private static readonly Dictionary<string, BrandMark?> Marks = new();
    private static readonly Dictionary<string, Bitmap?> Logos = new();
    private static readonly string[] PaletteColors = ["#0a84ff", "#30b0c7", "#5e5ce6", "#ff9f0a", "#34c759", "#ff375f", "#af52de", "#8e8e93"];

    /// <inheritdoc cref="KindProperty"/>
    public BrandKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }

    /// <inheritdoc cref="IdProperty"/>
    public string? Id { get => GetValue(IdProperty); set => SetValue(IdProperty, value); }

    /// <inheritdoc cref="LabelProperty"/>
    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <inheritdoc cref="SizeProperty"/>
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    /// <inheritdoc cref="AppearanceProperty"/>
    public BrandAppearance Appearance { get => GetValue(AppearanceProperty); set => SetValue(AppearanceProperty, value); }

    /// <inheritdoc cref="ForegroundProperty"/>
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <summary>An empty icon; set the properties.</summary>
    public BrandIcon()
    {
        IsHitTestVisible = false;
        Focusable = false;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
    }

    /// <summary>
    /// The provider's color key (web <c>ProviderIcon colorKey</c>, usually the provider id). The web keeps the parameter for API compatibility
    /// only — a provider without a brand mark shows the neutral initials tile, not a colored one ("colorKey kept for API compatibility" in
    /// icons.tsx) — so it is accepted and stored here and changes nothing, like there.
    /// </summary>
    public static readonly StyledProperty<string?> ColorKeyProperty = AvaloniaProperty.Register<BrandIcon, string?>(nameof(ColorKey));

    /// <inheritdoc cref="ColorKeyProperty"/>
    public string? ColorKey { get => GetValue(ColorKeyProperty); set => SetValue(ColorKeyProperty, value); }

    /// <summary>A provider tile (web <c>ProviderIcon</c>; <paramref name="colorKey"/> as there: accepted, no visual effect).</summary>
    public static BrandIcon Provider(string name, string? icon, double size = 28, string? colorKey = null)
        => new() { Kind = BrandKind.Provider, Label = name, Id = icon, Size = size, ColorKey = colorKey };

    /// <summary>A client tile (web <c>ClientIcon</c>) or, with <paramref name="glyph"/>, its bare mark (<c>ClientGlyph</c>).</summary>
    public static BrandIcon Client(string kind, double size = 0, bool glyph = false, string? fallbackName = null)
        => new() { Kind = BrandKind.Client, Id = kind, Label = fallbackName, Size = size, Appearance = glyph ? BrandAppearance.Glyph : BrandAppearance.Tile };

    /// <summary>A model family's mark (web <c>ModelGlyph</c>).</summary>
    public static BrandIcon Model(string? model, double size = 16) => new() { Kind = BrandKind.Model, Id = model, Size = size };

    /// <summary>A wire protocol's vendor mark (web <c>ProtocolGlyph</c>).</summary>
    public static BrandIcon Protocol(string protocol, double size = 11) => new() { Kind = BrandKind.Protocol, Id = protocol, Size = size };

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        var p = change.Property;
        if (p == KindProperty || p == IdProperty || p == LabelProperty || p == SizeProperty || p == AppearanceProperty || p == ForegroundProperty) Rebuild();
    }

    private bool IsTile => Appearance switch
    {
        BrandAppearance.Tile => true,
        BrandAppearance.Glyph => false,
        _ => Kind is BrandKind.Provider or BrandKind.Client,
    };

    private double EffectiveSize => Size > 0 ? Size : Kind switch
    {
        BrandKind.Model => 16,
        BrandKind.Protocol => 11,
        BrandKind.Client => IsTile ? 28 : 18,
        _ => 28,
    };

    private void Rebuild()
    {
        var size = EffectiveSize;
        Child = Kind switch
        {
            BrandKind.Provider => BuildProvider(size),
            BrandKind.Client => BuildClient(size),
            BrandKind.Model => BuildModel(size),
            _ => BuildProtocol(size),
        };
    }

    // ---------- providers ----------

    private Control BuildProvider(double size)
    {
        var name = Label ?? string.Empty;
        var key = string.IsNullOrEmpty(Id) ? null : BrandCatalog.ProviderAlias(Id);
        var mark = BuildProviderMark(name, Id, key, size);
        var region = ProviderRegion(name, key);
        if (region is null) return mark;
        var box = new Grid { Width = size, Height = size };
        box.Children.Add(mark);
        box.Children.Add(RegionBadge(region, size));
        return box;
    }

    private Control BuildProviderMark(string name, string? icon, string? key, double size)
    {
        if (icon is not null && (icon.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) && DataUrlBitmap(icon) is { } picture)
            return ImageTile(picture, size);
        // https: plugin icons need a download: not loaded here (initials instead)
        if (key is not null)
        {
            if (BrandCatalog.ProviderAvatar(key) is { } avatar && BuildAvatar(avatar, size) is { } tile) return tile;
            if (BrandCatalog.ProviderLogo(key) is { } logo && LogoBitmap(logo) is { } bitmap) return ImageTile(bitmap, size);
        }
        return InitialsTile(name, size);
    }

    private static Control InitialsTile(string name, double size)
    {
        var text = new TextBlock
        {
            Text = Initials(name),
            FontSize = Math.Round(size * 0.38),
            FontWeight = FontWeight.Medium,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
        var tile = new Border { Width = size, Height = size, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(Math.Round(size * 0.28)), Child = text };
        tile.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted).Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border);
        return tile;
    }

    private static Control ImageTile(Bitmap bitmap, double size) => new Border
    {
        Width = size,
        Height = size,
        ClipToBounds = true,
        Background = Brushes.White,
        BorderThickness = new Thickness(0.5),
        BorderBrush = new ImmutableSolidColorBrush(Color.FromArgb(20, 0, 0, 0)),
        CornerRadius = new CornerRadius(Math.Round(size * 0.28)),
        Child = new Image { Source = bitmap, Stretch = Stretch.Uniform },
    };

    // ---------- clients ----------

    private Control BuildClient(double size)
    {
        var kind = Id ?? string.Empty;
        var brand = BrandCatalog.Client(kind);
        var tile = IsTile;
        Control body;
        LucideIconKind? badge = brand?.Badge;
        if (brand is { } known)
        {
            if (known.Logo is { } logo)
            {
                body = LogoBitmap(logo) is { } bitmap
                    ? (tile ? ImageTile(bitmap, size) : GlyphImage(bitmap, size))
                    : InitialsTile(Label ?? kind, size);
            }
            else if (tile) body = BuildAvatar(known.Avatar!, size) ?? InitialsTile(Label ?? kind, size);
            else body = BuildMark(known.Mark!, size, null) ?? InitialsTile(Label ?? kind, size);
        }
        else
        {
            // an agent plugin's client: a puzzle piece (on a tile colored by its id as an avatar)
            body = tile ? PuzzleTile(kind, size) : GlyphIcon(LucideIconKind.Puzzle, size);
            badge = null;
        }
        if (badge is null) return body;
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(body);
        grid.Children.Add(CornerBadge(badge.Value, tile ? Math.Round(size * 0.8) : size));
        return grid;
    }

    private static Control PuzzleTile(string kind, double size)
    {
        var icon = new LucideIcon { Kind = LucideIconKind.Puzzle, Size = size * 0.55, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(Math.Round(size * 0.28)),
            Background = new ImmutableSolidColorBrush(Color.Parse(HashColor(kind))),
            Child = icon,
        };
    }

    private static Control GlyphImage(Bitmap bitmap, double size) => new Border
    {
        Width = size,
        Height = size,
        ClipToBounds = true,
        CornerRadius = new CornerRadius(Math.Round(size * 0.22)),
        Child = new Image { Source = bitmap, Stretch = Stretch.Uniform },
    };

    private Control GlyphIcon(LucideIconKind kind, double size)
    {
        var icon = new LucideIcon { Kind = kind, Size = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (Foreground is { } fg) icon.Foreground = fg;
        else icon.Token(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, ArcDesignTokenKind.Foreground);
        return icon;
    }

    private static Control CornerBadge(LucideIconKind kind, double markSize)
    {
        var box = Math.Max(9, Math.Round(markSize * 0.5));
        var icon = new LucideIcon { Kind = kind, Size = box * 0.78, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.Token(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, ArcDesignTokenKind.Foreground);
        var badge = new Border
        {
            Width = box,
            Height = box,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -2, -2),
            Child = icon,
        };
        badge.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceRaised).Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border);
        return badge;
    }

    private static Control RegionBadge(string region, double size)
    {
        var box = Math.Max(9, Math.Round(size * 0.45));
        Control content;
        if (region == "cn")
        {
            var text = new TextBlock
            {
                Text = "CN",
                FontSize = Math.Max(6, Math.Round(box * 0.6)),
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            text.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Foreground);
            content = text;
        }
        else
        {
            var icon = new LucideIcon { Kind = LucideIconKind.Globe, Size = box * 0.78, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            icon.Token(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, ArcDesignTokenKind.Foreground);
            content = icon;
        }
        var badge = new Border
        {
            MinWidth = box,
            Height = box,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding = region == "cn" ? new Thickness(1, 0) : default,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -2, -2),
            Child = content,
        };
        badge.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceRaised).Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border);
        return badge;
    }

    // ---------- models and protocols ----------

    private Control BuildModel(double size)
    {
        if (BrandCatalog.ModelMark(Id) is { } key && BuildMark(key, size, null) is { } mark) return mark;
        var box = new LucideIcon { Kind = LucideIconKind.Box, Size = size * 0.85, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        box.Token(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
        return new Grid { Width = size, Height = size, Children = { box } };
    }

    private Control BuildProtocol(double size)
        => BrandCatalog.ProtocolMark(Id) is { } key && BuildMark(key, size, null, ArcDesignTokenKind.TextSecondary) is { } mark ? mark : new Border { Width = size, Height = size };

    // ---------- shared pieces ----------

    /// <summary>A brand's avatar tile: its background (color or gradient), radius 0.28 and the mark at lobehub's scale.</summary>
    private static Control? BuildAvatar(string brand, double size)
    {
        if (BrandMarkData.Avatar(brand) is not { } spec) return null;
        var mark = FindMark($"{brand}.{spec.Variant}");
        if (mark is null) return null;
        var view = new BrandMarkView(mark)
        {
            Width = size * spec.Multiple,
            Height = size * spec.Multiple,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new ImmutableSolidColorBrush(Color.Parse(spec.Foreground)),
        };
        IBrush background = spec.BackgroundGradient is { } g
            ? BrandMark.MakeGradient(g, new Rect(0, 0, 1, 1))
            : new ImmutableSolidColorBrush(Color.Parse(spec.Background));
        return new Border
        {
            Width = size,
            Height = size,
            Background = background,
            BorderThickness = new Thickness(0.5),
            BorderBrush = new ImmutableSolidColorBrush(Color.FromArgb(20, 0, 0, 0)),
            CornerRadius = new CornerRadius(Math.Round(size * 0.28)),
            Child = view,
        };
    }

    private Control? BuildMark(string key, double size, IBrush? fixedBrush, ArcDesignTokenKind token = ArcDesignTokenKind.Foreground)
    {
        var mark = FindMark(key);
        if (mark is null) return null;
        var view = new BrandMarkView(mark) { Width = size, Height = size };
        if (fixedBrush is not null) view.Foreground = fixedBrush;
        else if (Foreground is { } fg) view.Foreground = fg;
        else view.Token(BrandMarkView.ForegroundProperty, token);
        return view;
    }

    private static BrandMark? FindMark(string key)
    {
        if (Marks.TryGetValue(key, out var cached)) return cached;
        return Marks[key] = BrandMarkData.Create(key);
    }

    private static Bitmap? LogoBitmap(string key)
    {
        if (Logos.TryGetValue(key, out var cached)) return cached;
        Bitmap? bitmap = null;
        if (BrandLogoData.Png(key) is { } bytes)
        {
            using var stream = new MemoryStream(bytes);
            bitmap = new Bitmap(stream);
        }
        return Logos[key] = bitmap;
    }

    private static Bitmap? DataUrlBitmap(string url)
    {
        // data:image/png;base64,.... (SVG is not decoded: the bitmap loader reads raster formats only)
        var comma = url.IndexOf(',');
        if (comma < 0 || !url.AsSpan(0, comma).Contains(";base64", StringComparison.OrdinalIgnoreCase)) return null;
        if (url.AsSpan(0, comma).Contains("svg", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(url[(comma + 1)..]));
            return new Bitmap(stream);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    // ---------- ported helpers (icons.tsx) ----------

    /// <summary>Deterministic color from a key (web <c>hashColor</c>).</summary>
    public static string HashColor(string key)
    {
        uint h = 0;
        Span<char> units = stackalloc char[2];
        foreach (var rune in key.EnumerateRunes())
        {
            rune.EncodeToUtf16(units);
            h = unchecked(h * 31 + units[0]);
        }
        return PaletteColors[(int)(h % (uint)PaletteColors.Length)];
    }

    /// <summary>One or two letters standing for a name (web <c>initials</c>): the first Han character, else the first letters of two words.</summary>
    public static string Initials(string name)
    {
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) || c == ' ' ? c : ' ').ToArray());
        var words = cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "?";
        var first = words[0];
        if (IsHan(first[0])) return first[..1];
        return (words.Length > 1 ? $"{words[0][0]}{words[1][0]}" : first[..Math.Min(2, first.Length)]).ToUpperInvariant();
    }

    private static bool IsHan(char c) => c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿';

    private static readonly Regex CnVariant = new(@"(?:[\s-]|^)(?:cn|china)$|(?:国内|中国)版?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex IntlVariant = new(@"(?:[\s-]|^)(?:intl|international|global)$|国际版?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex WorkBuddyAi = new(@"workbuddy[\s-]?ai$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"cn" / "intl" when the provider is a regional build of a vendor (web <c>providerRegion</c>), else null.</summary>
    public static string? ProviderRegion(string name, string? key)
    {
        var n = name.Trim();
        if (CnVariant.IsMatch(n)) return "cn";
        if (IntlVariant.IsMatch(n) || (key == "workbuddy" && WorkBuddyAi.IsMatch(n))) return "intl";
        return null;
    }
}
