using Astra.Desktop.Components.Layout;
using Astra.Desktop.Core.I18n;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;
using Dialog = TKCode.Arc.Dialog;

namespace Astra.Desktop.Components.Kit;

/// <summary>
/// Every kit control with sample content on one page (plan avalonia-client §21 WP0b): what the visual-regression harness renders
/// (<c>KitGallerySmoke.Build()</c>, called directly) and a build-time proof that each control constructs and lays out. Texts use
/// real catalog keys so it follows the language; brand names are data.
/// </summary>
public static class KitGallerySmoke
{
    private static readonly string[] ProviderIcons =
    [
        "openai", "copilot", "anthropic", "gemini", "grok", "deepseek", "kimi", "glm", "qwen", "minimax", "volcengine",
        "openrouter", "siliconflow", "ollama", "lm-studio", "qoder", "nextcowork", "routin", "workbuddy", "custom",
    ];

    private static readonly string[] ClientKinds =
    [
        "codex", "claude-code", "gemini-cli", "opencode", "claude-desktop", "grok-build", "pi", "hermes-agent", "minimax-code",
        "copilot-cli", "vscode-copilot", "crush", "qwen-code", "droid", "kimi-code", "zed", "vscode-insiders", "vscodium", "omp",
        "mimo-code", "deepseek-harness", "workbuddy", "workbuddy-ai", "muse-code", "nextcowork", "paseo", "zcode", "tkcode", "plugin-demo",
    ];

    private static readonly string[] Models =
    [
        "claude-sonnet-4", "gemini-2.5-pro", "gpt-5", "deepseek-chat", "qwen3-coder", "kimi-k2", "glm-4.6", "minimax-m2", "grok-4",
        "llama-4", "mistral-large", "doubao-seed", "unknown-model",
    ];

    private static readonly string[] Protocols = ["openai-chat", "openai-responses", "anthropic", "gemini"];

    /// <summary>The whole kit on a <see cref="PageScaffold"/> (a fixed-width column; give it a window around 1000 px wide).</summary>
    public static Control Build()
    {
        var body = new StackPanel { Spacing = 0, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };

        body.Children.Add(Section("Settings", SettingsSample()));
        body.Children.Add(Section("Status / badges / keys", StatusSample()));
        body.Children.Add(Section("Brand icons", BrandSample()));
        body.Children.Add(Section("States", StatesSample()));
        body.Children.Add(Section("Form field, key-value, detail", FormSample()));
        body.Children.Add(OverlaySample());

        return new PageScaffold
        {
            Title = Loc.T("nav.providers"),
            Subtitle = Loc.T("common.search"),
            ShowBack = true,
            Actions = new ActionGroup(8,
                new ActionGroup(4, new ArcButton { Content = Loc.T("common.refresh"), Variant = ButtonVariant.Secondary }),
                new ArcButton { Content = Loc.T("common.confirm"), Variant = ButtonVariant.Primary }),
            Body = body,
        };
    }

    /// <summary>Brand keys the catalog tables ask for that the generated data lacks (empty when consistent); for the harness.</summary>
    public static IReadOnlyList<string> MissingBrandData()
    {
        var missing = new List<string>();
        foreach (var key in BrandCatalog.AllMarkKeys())
            if (BrandMarkData.Create(key) is null) missing.Add(key);
        foreach (var kind in ClientKinds)
        {
            if (BrandCatalog.Client(kind) is not { } brand) continue;
            if (brand.Mark is not null && BrandMarkData.Create(brand.Mark) is null) missing.Add(brand.Mark);
            if (brand.Avatar is not null && (BrandMarkData.Avatar(brand.Avatar) is null)) missing.Add("avatar:" + brand.Avatar);
            if (brand.Logo is not null && BrandLogoData.Png(brand.Logo) is null) missing.Add("logo:" + brand.Logo);
        }
        foreach (var icon in ProviderIcons)
        {
            if (BrandCatalog.ProviderAvatar(icon) is { } avatar && BrandMarkData.Avatar(avatar) is null) missing.Add("avatar:" + avatar);
            if (BrandCatalog.ProviderLogo(icon) is { } logo && BrandLogoData.Png(logo) is null) missing.Add("logo:" + logo);
        }
        return missing;
    }

    private static Control Section(string title, Control content) => new StackPanel { Margin = new Thickness(0, 0, 0, 24), Children = { new SectionTitle(title), content } };

    private static Control SettingsSample()
    {
        var group = new SettingsGroup { Title = Loc.T("nav.settings"), Footer = Loc.T("shell.expandSidebar") };
        group.Rows.Add(new SettingsRow
        {
            Label = Loc.T("common.copy"),
            Detail = Loc.T("common.search"),
            Icon = new LucideIcon { Kind = LucideIconKind.Copy, Size = 16 },
            Trailing = { new Switch { IsChecked = true } },
        });
        var clickable = new SettingsRow { Label = Loc.T("common.select"), Detail = Loc.T("common.noResults"), Trailing = { new StatusDot(Tone.Green) } };
        clickable.Invoked += (_, _) => { };
        group.Rows.Add(clickable);
        group.Rows.Add(new SettingsRow
        {
            Label = Loc.T("common.close"),
            IsSelected = true,
            Trailing = { KitBadge.Create("OK", Tone.Accent), new ArcButton { Content = Loc.T("common.retry"), Size = ControlSize.Small } },
        });
        return group;
    }

    private static Control StatusSample()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new StatusDot(Tone.Neutral));
        row.Children.Add(new StatusDot(Tone.Green));
        row.Children.Add(new StatusDot(Tone.Orange));
        row.Children.Add(new StatusDot(Tone.Red));
        row.Children.Add(new StatusDot(Tone.Accent) { IsPulsing = true });
        foreach (var tone in Enum.GetValues<Tone>()) row.Children.Add(KitBadge.Create(tone.ToString(), tone));
        row.Children.Add(new Kbd("⌘K"));
        row.Children.Add(new Kbd("Ctrl+Alt+S"));
        return row;
    }

    private static Control BrandSample()
    {
        var stack = new StackPanel { Spacing = 12 };
        var providers = new WrapPanel { ItemSpacing = 10, LineSpacing = 10 };
        foreach (var icon in ProviderIcons) providers.Children.Add(BrandIcon.Provider(icon, icon, 32));
        providers.Children.Add(BrandIcon.Provider("Qoder CN", "qoder", 32));
        providers.Children.Add(BrandIcon.Provider("WorkBuddy AI", "workbuddy", 32));
        providers.Children.Add(BrandIcon.Provider("My Gateway", null, 32));
        providers.Children.Add(BrandIcon.Provider("自定义", "custom", 32));
        stack.Children.Add(providers);

        var clients = new WrapPanel { ItemSpacing = 10, LineSpacing = 10 };
        foreach (var kind in ClientKinds) clients.Children.Add(BrandIcon.Client(kind, 32));
        stack.Children.Add(clients);

        var glyphs = new WrapPanel { ItemSpacing = 12, LineSpacing = 10 };
        foreach (var kind in ClientKinds) glyphs.Children.Add(BrandIcon.Client(kind, 18, glyph: true));
        stack.Children.Add(glyphs);

        var models = new WrapPanel { ItemSpacing = 12, LineSpacing = 10 };
        foreach (var model in Models) models.Children.Add(BrandIcon.Model(model, 20));
        foreach (var protocol in Protocols) models.Children.Add(BrandIcon.Protocol(protocol, 16));
        stack.Children.Add(models);
        return stack;
    }

    private static Control StatesSample()
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new EmptyStateView(Loc.T("common.noResults"), Loc.T("common.search"), action: new ArcButton { Content = Loc.T("common.refresh"), Variant = ButtonVariant.Secondary }));
        stack.Children.Add(new ErrorState { Message = Loc.T("tray.loadFailed"), RetryCommand = new NoopCommand() });
        stack.Children.Add(new LoadingState(3));
        return stack;
    }

    private static Control FormSample()
    {
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(new Input { Label = Loc.T("common.search"), Description = Loc.T("common.noResults") });
        stack.Children.Add(FormField.Wrap(Loc.T("common.select"), new Switch(), Loc.T("common.noResults"), Loc.T("common.error")));
        var card = new DetailSection { Title = Loc.T("common.copy"), Action = new ArcButton { Content = Loc.T("common.copy"), Variant = ButtonVariant.Ghost, Size = ControlSize.Small } };
        card.Body = new StackPanel
        {
            Children =
            {
                new KeyValueRow("id", "01JABCDEF123456", mono: true),
                new KeyValueRow(Loc.T("common.search"), "Astra"),
            },
        };
        stack.Children.Add(card);
        return stack;
    }

    private static Control OverlaySample()
    {
        // Declared, closed: they render nothing in place. The harness opens them with IsOpen = true.
        var sheet = new Sheet
        {
            Title = Loc.T("nav.tokens"),
            Description = Loc.T("common.search"),
            Content = new Input { Label = Loc.T("common.search") },
            Footer = [new ArcButton { Content = Loc.T("common.cancel"), Variant = ButtonVariant.Secondary }, new ArcButton { Content = Loc.T("common.confirm"), Variant = ButtonVariant.Primary }],
        };
        var side = new SideSheet { Title = Loc.T("nav.tokens"), Footer = [new ArcButton { Content = Loc.T("common.close") }] };
        var confirm = ConfirmDialog.Build(Loc.T("common.confirm"), Loc.T("common.noResults"), null, null, destructive: true);
        var open = new ArcButton { Content = Loc.T("common.confirm"), Variant = ButtonVariant.Secondary, HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += (_, _) => sheet.IsOpen = true;
        return new StackPanel { Children = { open, sheet, side, confirm } };
    }

    private sealed class NoopCommand : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }
}
