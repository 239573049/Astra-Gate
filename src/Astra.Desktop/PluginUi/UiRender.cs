using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Plugins.V2;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;
using ArcButton = TKCode.Arc.Button;
using Kbd = Astra.Desktop.Components.Kit.Kbd;

namespace Astra.Desktop.PluginUi;

/// <summary>
/// Maps a plugin view's nodes onto Arc / Avalonia controls (design/plugin-v2 04): the same vocabulary the web renders
/// (web/src/plugin-ui/render.tsx), props as tokens only. Inputs keep their value in the session's local bag and reconcile
/// with the host's value by sequence number; events go back as { action, args }.
/// </summary>
internal static class UiRender
{
    /// <summary>The components this shell renders (reported when a view opens); PluginUiRenderTests checks them against components.json.</summary>
    public static IReadOnlyList<string> Components => PluginUiProtocol.Components;

    public static Control Build(PluginViewHost view, NodeHost host)
    {
        var node = host.Node;
        var p = Props(node);
        var local = view.Session.Local;
        if (!PluginUiProtocol.Flag(p["visible"], local, true)) return new Panel { IsVisible = false };
        var type = node["type"]?.GetValue<string>() ?? "";
        Control control;
        try
        {
            control = type switch
            {
                "Page" => Page(view, host, p),
                "Stack" => Stack(view, host, p),
                "Grid" => GridOf(view, host, p),
                "Split" => Split(view, host, p),
                "Scroll" => new ScrollViewer { MaxHeight = Size(p["maxHeight"], 480), Content = OnlyChild(view, host) },
                "Box" => BoxOf(view, host, p),
                "Divider" => new Border { Height = 1, Margin = new Thickness(0, 4) }.Token(Border.BackgroundProperty, ArcDesignTokenKind.Border),
                "Spacer" => new Border { Width = PluginUiProtocol.Gap(S(p, "size") ?? "4"), Height = PluginUiProtocol.Gap(S(p, "size") ?? "4") },
                "Sticky" => OnlyChild(view, host),
                "Group" => Group(view, host, p),
                "Row" => Row(view, host, p, node),
                "Card" => CardOf(view, host, p, node),
                "Section" => Section(view, host, p),
                "Tabs" => TabsOf(view, host, p, node),
                "Tab" or "Boundary" => Column(view, host, 12),
                "Accordion" => Column(view, host, 0),
                "AccordionItem" => AccordionItemOf(view, host, p),
                "Disclosure" => AccordionItemOf(view, host, p),
                "Stepper" => StepperOf(p),
                "Timeline" => TimelineOf(view, p),
                "Breadcrumb" => BreadcrumbOf(view, p),
                "Toolbar" => ToolbarOf(view, p),
                "Text" => TextOf(view, p),
                "Heading" => Heading(p),
                "RichText" => RichText(view, p),
                "Markdown" => Wrap(S(p, "value") ?? ""),
                "Code" => new CodeBlock { Code = S(p, "value") ?? "", Language = S(p, "language") ?? "text", MaxLines = (int)(N(p, "maxHeight") / 18 is > 0 and var l ? l : 18) },
                "JsonView" => new CodeBlock { Code = p["value"]?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null", Language = "json", MaxLines = 24 },
                "DiffView" => Diff(p),
                "Link" => LinkOf(view, p, node),
                "Icon" => Icon(S(p, "name") ?? S(p, "brand"), S(p, "size") == "lg" ? 22 : S(p, "size") == "sm" ? 13 : 16, S(p, "tone")),
                "Image" => ImageOf(view, p),
                "Avatar" => AvatarOf(p),
                "Badge" => KitBadge.Create(S(p, "label") ?? "", ToneOf(S(p, "tone"))),
                "StatusDot" => Inline(6, new StatusDot(ToneOf(S(p, "tone"))), Small(S(p, "label") ?? "")),
                "Kbd" => Inline(2, [.. (p["keys"] as JsonArray ?? []).Select(k => (Control)new Kbd(k?.ToString() ?? ""))]),
                "KV" => KvOf(view, p),
                "Alert" or "Banner" => AlertOf(view, p, node),
                "EmptyState" => EmptyOf(view, p),
                "Skeleton" or "Spinner" => Ui.Spinner(),
                "Progress" => ProgressOf(p, N(p, "value"), S(p, "label"), S(p, "tone")),
                "Meter" => ProgressOf(p, N(p, "value") / (N(p, "max") is var m && m > 0 ? m : 100), S(p, "label") ?? $"{N(p, "value")} / {N(p, "max")}", null),
                "Counter" => KitStyle.Text(PluginUiProtocol.Format(S(p, "format") ?? "number", p["value"], view.Locale), ArcDesignTokenKind.Type2XL, ArcDesignTokenKind.Foreground, FontWeight.SemiBold),
                "QRCode" => DrawnControl.Qr(S(p, "value") ?? "", S(p, "size") == "lg" ? 220 : S(p, "size") == "sm" ? 120 : 160),
                "Button" => ButtonOf(view, p, node),
                "IconButton" => IconButtonOf(view, p, node),
                "ButtonGroup" => Inline(8, view, host),
                "SplitButton" => Inline(4, ButtonOf(view, p, node), MenuOf(view, p["items"] as JsonArray, S(p, "label") ?? "")),
                "CopyButton" => Ui.Button(S(p, "label"), () => PluginViewHost.Copy(view, S(p, "value") ?? ""), ButtonVariant.Ghost, LucideIconKind.Copy),
                "ConfirmButton" => ConfirmButtonOf(view, p, node),
                "Menu" => MenuOf(view, p["items"] as JsonArray, S(p, "label") ?? ""),
                "ContextMenu" => ContextMenuOf(view, host, p),
                "TextField" or "SearchField" => TextInput(view, node, p, false),
                "PasswordField" => TextInput(view, node, p, true),
                "TextArea" or "CodeField" => TextAreaOf(view, node, p),
                "NumberField" or "MoneyField" => NumberOf(view, node, p, type == "MoneyField"),
                "Switch" => SwitchOf(view, node, p),
                "Checkbox" => CheckboxOf(view, node, p),
                "RadioGroup" or "Segmented" or "RadioCards" => SegmentedOf(view, node, p),
                "Select" or "Combobox" => SelectOf(view, node, p, Options(view, p)),
                "MultiSelect" or "ChipGroup" => ChipsOf(view, node, p, Options(view, p), type == "MultiSelect" || B(p, "multiple") || p["multiple"] is null),
                "TagInput" => TagsOf(view, node, p),
                "Slider" => SliderOf(view, node, p),
                "DatePicker" => DateOf(view, node, p),
                "DateRangePicker" => RangeOf(view, node, p),
                "TimePicker" => TimeOf(view, node, p),
                "KeyValueEditor" => KeyValueOf(view, node, p),
                "ListEditor" => ListEditorOf(view, node, p),
                "FileDrop" => FileDropOf(view, node, p),
                "Form" => FormOf(view, host, p, node),
                "Field" => FieldOf(view, host, p),
                "FieldGroup" => Column(view, host, 12),
                "Table" => TableOf(view, node, p),
                "List" => ListOf(view, node, p),
                "Tree" => TreeOf(view, node, p),
                "Pagination" => PagerOf(view, node, p, N(p, "page") is > 0 and var pg ? (int)pg : 1, (int)Math.Max(1, Math.Ceiling(N(p, "total") / (N(p, "pageSize") is > 0 and var ps ? ps : 20)))),
                "LogView" => LogOf(view, p),
                "MetricCard" => MetricOf(view, p),
                "Sparkline" => DrawnControl.Sparkline([.. (p["data"] as JsonArray ?? []).Select(v => Num(v))], S(p, "tone"), B(p, "area")),
                "Chart" => ChartOf(view, node, p),
                "Heatmap" => HeatmapOf(p),
                "Gauge" => DrawnControl.Gauge(N(p, "value"), N(p, "max") is > 0 and var gm ? gm : 100, S(p, "label"), S(p, "tone"), view.Locale),
                "Canvas" => DrawnControl.Canvas(p["draw"] as JsonArray ?? [], p["viewBox"] as JsonArray, Size(p["height"], 240), hit => Fire(view, hit["action"]?.ToString(), hit["args"], null, node)),
                "Sheet" or "Dialog" => B(p, "open") ? CardOf(view, host, p, node) : new Panel { IsVisible = false },
                "Popover" => PopoverOf(view, host, p),
                "ModelPicker" or "ProviderPicker" or "AccountPicker" or "ClientPicker" or "TokenPicker" or "ProtocolPicker" => DomainPickerOf(view, node, p, type),
                "ModelLabel" or "ProviderLabel" or "ClientLabel" => Small(p["id"]?.ToString() ?? "", mono: type == "ModelLabel"),
                "Money" => Small(PluginUiProtocol.Format("money", p["value"], view.Locale)),
                "RequestLink" => LinkTo(view, S(p, "label") ?? p["id"]?.ToString() ?? "", "/requests?id=" + Uri.EscapeDataString(p["id"]?.ToString() ?? "")),
                "TraceLink" => LinkTo(view, S(p, "label") ?? p["id"]?.ToString() ?? "", "/traces/requests/" + Uri.EscapeDataString(p["id"]?.ToString() ?? "")),
                "UsageBar" => ProgressOf(p, N(p, "limit") > 0 ? N(p, "used") / N(p, "limit") : double.NaN,
                    $"{S(p, "label")} {PluginUiProtocol.Format(S(p, "format") ?? "number", p["used"], view.Locale)}{(N(p, "limit") > 0 ? " / " + PluginUiProtocol.Format(S(p, "format") ?? "number", p["limit"], view.Locale) : "")}", null),
                _ => p["fallback"] is JsonObject fb ? view.Detached(fb) : Small(Loc.T("pluginUi.unsupported", ("type", type))),
            };
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or JsonException or ArgumentException)
        {
            control = Small($"{type}: {e.Message}");
        }
        if (PluginUiProtocol.Flag(p["disabled"], local, false)) control.IsEnabled = false;
        if (S(p, "tooltip") is { } tip) ToolTip.SetTip(control, tip);
        if (S(p, "a11yLabel") is { } name) Avalonia.Automation.AutomationProperties.SetName(control, name);
        var width = PluginUiProtocol.Responsive(p["width"], view.WidthClass);
        if (width is JsonValue wv && wv.TryGetValue<string>(out var wt))
        {
            if (wt == "fill") control.HorizontalAlignment = HorizontalAlignment.Stretch;
            else if (!double.IsNaN(PluginUiProtocol.Width(wt))) control.Width = PluginUiProtocol.Width(wt);
        }
        else if (width is JsonValue wn && wn.TryGetValue<double>(out var px)) control.Width = px;
        if (S(p, "maxWidth") is { } mw && !double.IsNaN(PluginUiProtocol.Width(mw))) control.MaxWidth = PluginUiProtocol.Width(mw);
        if (S(p, "minWidth") is { } minw && !double.IsNaN(PluginUiProtocol.Width(minw))) control.MinWidth = PluginUiProtocol.Width(minw);
        if (node["key"] is { } key) host.Wrapper.Tag = "key:" + key;
        if (p["grow"] is not null) host.Wrapper.HorizontalAlignment = HorizontalAlignment.Stretch;
        return control;
    }

    // ---------------------------------------------------------------- helpers

    private static JsonObject Props(JsonObject node) => node["props"] as JsonObject ?? [];
    private static string? S(JsonObject p, string k) => p[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : p[k] is JsonValue n && n.GetValueKind() == JsonValueKind.Number ? n.ToString() : null;
    private static double N(JsonObject p, string k) => Num(p[k]);
    private static double Num(JsonNode? n) => PluginUiProtocol.ToDouble(n);
    private static bool B(JsonObject p, string k) => p[k] is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    private static double Size(JsonNode? v, double fallback)
    {
        if (v is JsonValue n && n.TryGetValue<double>(out var d)) return d;
        return (v as JsonValue)?.ToString() switch { "xs" => 120, "sm" => 160, "md" => 240, "lg" => 320, "xl" => 420, _ => fallback };
    }

    private static Tone ToneOf(string? tone) => tone switch
    {
        "accent" => Tone.Accent,
        "success" => Tone.Green,
        "warning" => Tone.Orange,
        "danger" => Tone.Red,
        _ => Tone.Neutral,
    };

    private static ArcDesignTokenKind ToneToken(string? tone) => tone switch
    {
        "accent" => ArcDesignTokenKind.Accent,
        "success" => ArcDesignTokenKind.Success,
        "warning" => ArcDesignTokenKind.Warning,
        "danger" => ArcDesignTokenKind.Danger,
        _ => ArcDesignTokenKind.TextSecondary,
    };

    internal static Control Icon(string? name, double size = 16, string? tone = null)
    {
        if (string.IsNullOrEmpty(name)) return new Panel { Width = 0 };
        var pascal = string.Concat(name.Split('-', '_').Where(x => x.Length > 0).Select(x => char.ToUpperInvariant(x[0]) + x[1..]));
        if (!Enum.TryParse<LucideIconKind>(pascal, true, out var kind)) kind = LucideIconKind.Puzzle;
        var icon = new LucideIcon { Kind = kind, Size = size, VerticalAlignment = VerticalAlignment.Center };
        icon.Token(TemplatedControl.ForegroundProperty, tone is null ? ArcDesignTokenKind.TextSecondary : ToneToken(tone));
        return icon;
    }

    private static TextBlock Small(string text, bool mono = false, ArcDesignTokenKind color = ArcDesignTokenKind.Foreground)
    {
        var t = KitStyle.Text(text, ArcDesignTokenKind.TypeSM, color);
        t.TextTrimming = TextTrimming.None;
        t.TextWrapping = TextWrapping.Wrap;
        if (mono) t.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        return t;
    }

    private static TextBlock Muted(string text) => Small(text, color: ArcDesignTokenKind.TextMuted);

    private static TextBlock Wrap(string text) => Small(text);

    private static StackPanel Inline(double spacing, params Control[] children)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    private static StackPanel Inline(double spacing, PluginViewHost view, NodeHost host)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
        view.Children(host, s.Children);
        return s;
    }

    private static StackPanel Column(PluginViewHost view, NodeHost host, double spacing)
    {
        var s = new StackPanel { Spacing = spacing };
        view.Children(host, s.Children);
        return s;
    }

    private static Control OnlyChild(PluginViewHost view, NodeHost host) => Column(view, host, 0);

    private static Control Nodes(PluginViewHost view, JsonNode? list, double spacing = 8)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
        foreach (var n in (list as JsonArray ?? []).OfType<JsonObject>()) s.Children.Add(view.Detached(n));
        return s;
    }

    private static Border Surface(Control child, double padding = 16)
    {
        var b = new Border { Child = child, Padding = new Thickness(padding), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };
        return b.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceRaised).Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border);
    }

    // ---------------------------------------------------------------- events

    /// <summary>Fires an event prop: its action with its args merged under the event's payload.</summary>
    internal static bool Fire(PluginViewHost view, JsonObject node, string prop, JsonObject? payload = null)
    {
        var v = Props(node)[prop];
        if (v is not JsonObject a || a["action"]?.GetValue<string>() is not { } action) return false;
        JsonNode? args = a["args"]?.DeepClone();
        if (payload is not null)
        {
            if (args is JsonObject merged) foreach (var (k, val) in payload) merged[k] = val?.DeepClone();
            else args = payload;
        }
        view.Session.Send(action, args ?? new JsonObject(), node["key"]?.ToString());
        return true;
    }

    private static bool Fire(PluginViewHost view, string? action, JsonNode? args, JsonObject? payload, JsonObject node)
    {
        if (action is null) return false;
        var merged = args?.DeepClone() as JsonObject ?? new JsonObject();
        if (payload is not null) foreach (var (k, v) in payload) merged[k] = v?.DeepClone();
        view.Session.Send(action, merged, node["key"]?.ToString());
        return true;
    }

    private static bool Has(JsonObject p, string prop) => p[prop] is JsonObject;

    // ---------------------------------------------------------------- input values (03 §6.1)

    private static JsonNode? Value(PluginViewHost view, JsonObject node, JsonNode? fallback = null)
    {
        var p = Props(node);
        var server = p["value"] ?? p["checked"] ?? fallback;
        if (S(p, "name") is not { } name) return server;
        var s = view.Session;
        if (!s.ServerWins(name) && s.Local.TryGetValue(name, out var mine)) return mine;
        s.Local[name] = server?.DeepClone();
        return server;
    }

    private static void Set(PluginViewHost view, JsonObject node, JsonNode? value, bool commitNow)
    {
        var p = Props(node);
        if (S(p, "name") is { } name) view.Session.SetLocal(name, value);
        if (!Has(p, "onChange")) return;
        if (S(p, "commit") == "blur" && !commitNow) return;
        var debounce = commitNow ? 0 : double.IsNaN(N(p, "debounce")) ? (value is JsonValue sv && sv.GetValueKind() == JsonValueKind.String ? 300 : 0) : N(p, "debounce");
        var payload = new JsonObject { ["value"] = value?.DeepClone() };
        if (debounce <= 0) Fire(view, node, "onChange", payload);
        else Debounce(node, TimeSpan.FromMilliseconds(debounce), () => Fire(view, node, "onChange", payload));
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JsonObject, DispatcherTimer> Timers = new();

    private static void Debounce(JsonObject node, TimeSpan delay, Action action)
    {
        if (Timers.TryGetValue(node, out var old)) old.Stop();
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        Timers.AddOrUpdate(node, timer);
        timer.Start();
    }

    private static T Tagged<T>(T control, JsonObject node) where T : Control
    {
        if (S(Props(node), "name") is { } name) control.Tag = "input:" + name;
        return control;
    }

    private static List<(string Value, string Label, string? Description)> Options(PluginViewHost view, JsonObject p)
    {
        IEnumerable<JsonNode?> raw = p["source"] is JsonObject src && src["collection"]?.GetValue<string>() is { } c && view.Session.Collections.TryGetValue(c, out var col)
            ? col.Items
            : p["options"] as JsonArray ?? [];
        return raw.Select(o => o is JsonValue v ? (v.ToString(), v.ToString(), (string?)null)
                : o is JsonObject obj ? (obj["value"]?.ToString() ?? "", obj["label"]?.ToString() ?? obj["title"]?.ToString() ?? obj["value"]?.ToString() ?? "", obj["description"]?.ToString())
                : ("", "", null))
            .Where(o => o.Item1.Length > 0).ToList();
    }

    // ---------------------------------------------------------------- page & layout

    private static Control Page(PluginViewHost view, NodeHost host, JsonObject p)
    {
        var column = new StackPanel { Spacing = 20, MaxWidth = S(p, "width") switch { "narrow" => 640, "wide" => 1280, "full" => double.PositiveInfinity, _ => 960 } };
        if (S(p, "title") is { } title || p["actions"] is JsonArray)
        {
            var head = new DockPanel();
            var actions = Nodes(view, p["actions"]);
            DockPanel.SetDock(actions, Dock.Right);
            head.Children.Add(actions);
            var titles = new StackPanel { Spacing = 2 };
            if (S(p, "title") is { } t) titles.Children.Add(Inline(8, Icon(S(p, "icon"), 18, "accent"), KitStyle.Text(t, ArcDesignTokenKind.TypeXL, ArcDesignTokenKind.Foreground, FontWeight.SemiBold)));
            if (S(p, "subtitle") is { } sub) titles.Children.Add(Muted(sub));
            head.Children.Add(titles);
            column.Children.Add(head);
        }
        var body = new StackPanel { Spacing = 16 };
        view.Children(host, body.Children);
        column.Children.Add(body);
        return column;
    }

    private static Control Stack(PluginViewHost view, NodeHost host, JsonObject p)
    {
        var dir = PluginUiProtocol.Responsive(p["direction"], view.WidthClass)?.ToString() ?? "column";
        var gap = PluginUiProtocol.Gap(PluginUiProtocol.Responsive(p["gap"], view.WidthClass)?.ToString() ?? (dir == "row" ? "2" : "3"));
        Panel panel = dir == "row" && B(p, "wrap")
            ? new WrapPanel { ItemSpacing = gap, LineSpacing = gap }
            : new StackPanel { Orientation = dir == "row" ? Orientation.Horizontal : Orientation.Vertical, Spacing = gap };
        if (dir == "row") panel.VerticalAlignment = S(p, "align") switch { "start" => VerticalAlignment.Top, "end" => VerticalAlignment.Bottom, _ => VerticalAlignment.Center };
        view.Children(host, panel.Children);
        var pad = PluginUiProtocol.Gap(S(p, "padding") ?? "0");
        if (S(p, "justify") == "between" && dir == "row" && panel.Children.Count == 2)
        {
            // two children pushed apart: a dock keeps them as wrappers of the same host list
            var dock = new DockPanel { LastChildFill = false };
            host.Kids.Clear();
            panel.Children.Clear();
            view.Children(host, dock.Children);
            if (dock.Children.Count == 2)
            {
                DockPanel.SetDock(dock.Children[0], Dock.Left);
                DockPanel.SetDock(dock.Children[1], Dock.Right);
            }
            host.ChildHost = null;
            return new Border { Padding = new Thickness(pad), Child = dock };
        }
        return pad > 0 ? new Border { Padding = new Thickness(pad), Child = panel } : panel;
    }

    private static Control GridOf(PluginViewHost view, NodeHost host, JsonObject p)
    {
        var cols = PluginUiProtocol.Responsive(p["columns"], view.WidthClass);
        var count = cols is JsonValue n && n.TryGetValue<int>(out var c) ? c : view.WidthClass switch { "compact" => 1, "wide" => 4, _ => 2 };
        var grid = new UniformGrid { Columns = Math.Max(1, count) };
        var wrap = new Border { Child = grid };
        view.Children(host, grid.Children);
        var gap = PluginUiProtocol.Gap(S(p, "gap") ?? "3");
        foreach (var k in host.Kids) k.Wrapper.Margin = new Thickness(0, 0, gap, gap);
        return wrap;
    }

    private static Control Split(PluginViewHost view, NodeHost host, JsonObject p)
    {
        var stacked = view.WidthClass == "compact" || (S(p, "stackBelow") == "regular" && view.WidthClass != "wide");
        if (stacked) return Column(view, host, 16);
        var ratio = N(p, "ratio") is > 0 and < 1 and var r ? r : 0.35;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{ratio.ToString(CultureInfo.InvariantCulture)}*,16,{(1 - ratio).ToString(CultureInfo.InvariantCulture)}*") };
        var kids = (host.Node["children"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (kids.Count > 0) grid.Children.Add(view.Detached(kids[0]));
        if (kids.Count > 1)
        {
            var second = view.Detached(kids[1]);
            Grid.SetColumn(second, 2);
            grid.Children.Add(second);
        }
        return grid;
    }

    private static Control BoxOf(PluginViewHost view, NodeHost host, JsonObject p)
    {
        var b = new Border { Padding = new Thickness(PluginUiProtocol.Gap(S(p, "padding") ?? "3")), CornerRadius = new CornerRadius(S(p, "radius") == "lg" ? 12 : S(p, "radius") == "none" ? 0 : 8), Child = Column(view, host, 8) };
        switch (S(p, "surface"))
        {
            case "card": b.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceRaised); break;
            case "sunken": b.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted); break;
            case "accent": b.Token(Border.BackgroundProperty, ArcDesignTokenKind.AccentSubtle); break;
        }
        if (B(p, "border")) b.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border).BorderThickness = new Thickness(1);
        return b;
    }

    // ---------------------------------------------------------------- structure

    private static Control Group(PluginViewHost view, NodeHost host, JsonObject p)
    {
        var g = new SettingsGroup { Title = S(p, "title"), Footer = S(p, "footer") ?? S(p, "description") };
        view.Children(host, g.Rows);
        return g;
    }

    private static Control Row(PluginViewHost view, NodeHost host, JsonObject p, JsonObject node)
    {
        var row = new SettingsRow { Label = S(p, "label"), Detail = S(p, "detail"), Hint = S(p, "hint"), IsSelected = B(p, "selected") };
        if (S(p, "icon") is { } icon) row.Icon = Icon(icon);
        var trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        view.Children(host, trailing.Children);
        row.Trailing.Add(trailing);
        if (Has(p, "onPress")) row.Invoked += (_, _) => Fire(view, node, "onPress");
        return row;
    }

    private static Control CardOf(PluginViewHost view, NodeHost host, JsonObject p, JsonObject node)
    {
        var column = new StackPanel { Spacing = 12 };
        if (S(p, "title") is { } title)
        {
            var head = new DockPanel();
            var actions = Nodes(view, p["actions"]);
            DockPanel.SetDock(actions, Dock.Right);
            head.Children.Add(actions);
            var t = new StackPanel { Spacing = 2 };
            t.Children.Add(Inline(8, Icon(S(p, "icon"), 16, "accent"), KitStyle.Text(title, ArcDesignTokenKind.TypeBase, ArcDesignTokenKind.Foreground, FontWeight.Medium)));
            if (S(p, "subtitle") is { } sub) t.Children.Add(Muted(sub));
            head.Children.Add(t);
            column.Children.Add(head);
        }
        var body = new StackPanel { Spacing = 12 };
        view.Children(host, body.Children);
        column.Children.Add(body);
        if (p["footer"] is JsonArray) column.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Right, Child = Nodes(view, p["footer"]) });
        var card = Surface(column);
        if (Has(p, "onPress"))
        {
            card.Cursor = new Cursor(StandardCursorType.Hand);
            card.PointerReleased += (_, _) => Fire(view, node, "onPress");
        }
        return card;
    }

    private static Control Section(PluginViewHost view, NodeHost host, JsonObject p)
    {
        var column = new StackPanel { Spacing = 12 };
        var head = new DockPanel();
        var actions = Nodes(view, p["actions"]);
        DockPanel.SetDock(actions, Dock.Right);
        head.Children.Add(actions);
        head.Children.Add(KitStyle.Text(S(p, "title") ?? "", ArcDesignTokenKind.TypeBase, ArcDesignTokenKind.Foreground, FontWeight.SemiBold));
        column.Children.Add(head);
        if (S(p, "description") is { } d) column.Children.Add(Muted(d));
        var body = new StackPanel { Spacing = 12 };
        view.Children(host, body.Children);
        if (B(p, "collapsible"))
            column.Children.Add(new AccordionItem { Header = Loc.T("pluginUi.details"), Content = body, IsExpanded = p["defaultOpen"] is not JsonValue o || o.GetValueKind() != JsonValueKind.False });
        else column.Children.Add(body);
        return column;
    }


    private static Control TabsOf(PluginViewHost view, NodeHost host, JsonObject p, JsonObject node)
    {
        var items = (p["items"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var values = items.Select(i => i["value"]?.ToString() ?? "").ToList();
        var stateKey = "tabs:" + (node["key"]?.ToString() ?? S(p, "name") ?? string.Join("|", values));
        // like the web: the tab chosen here stays until the plugin sets a different value
        var controlled = S(p, "value");
        string current;
        if (controlled is not null && !(view.UiState.TryGetValue(stateKey + "#prop", out var seen) && seen as string == controlled))
        {
            view.UiState[stateKey + "#prop"] = controlled;
            view.UiState[stateKey] = current = controlled;
        }
        else current = view.UiState.TryGetValue(stateKey, out var chosen) && chosen is string c ? c : controlled ?? values.FirstOrDefault() ?? "";
        var pill = S(p, "variant") == "pill";
        var column = new StackPanel { Spacing = 12 };
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var stripHost = new Border { Child = strip, BorderThickness = new Thickness(0, 0, 0, pill ? 0 : 1) }.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border);
        var pane = new ContentControl();
        void Show(string value)
        {
            // lazy: only the open tab's content is built
            var tab = (host.Node["children"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(t => t["props"]?["value"]?.ToString() == value);
            pane.Content = tab is null ? null : view.Detached(tab);
        }
        void DrawStrip()
        {
            strip.Children.Clear();
            foreach (var item in items)
            {
                var value = item["value"]?.ToString() ?? "";
                var on = value == current;
                var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                if (item["icon"]?.ToString() is { Length: > 0 } icon) content.Children.Add(Icon(icon, 14));
                content.Children.Add(KitStyle.Text(item["label"]?.ToString() ?? value, ArcDesignTokenKind.TypeSM,
                    on ? (pill ? ArcDesignTokenKind.AccentForeground : ArcDesignTokenKind.Foreground) : ArcDesignTokenKind.TextSecondary));
                if (item["badge"]?.ToString() is { Length: > 0 } badge)
                    content.Children.Add(new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(6, 0), Child = KitStyle.Text(badge, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary) }
                        .Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted));
                var tab = new Border
                {
                    Child = content,
                    Padding = new Thickness(12, 6),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Focusable = true,
                    Background = Brushes.Transparent,
                };
                Avalonia.Automation.AutomationProperties.SetName(tab, item["label"]?.ToString() ?? value);
                if (pill)
                {
                    tab.CornerRadius = new CornerRadius(999);
                    if (on) tab.Token(Border.BackgroundProperty, ArcDesignTokenKind.Accent);
                }
                else
                {
                    tab.BorderThickness = new Thickness(0, 0, 0, 2);
                    tab.Margin = new Thickness(0, 0, 0, -1);
                    if (on) tab.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Accent);
                    else tab.BorderBrush = Brushes.Transparent;
                }
                tab.PointerReleased += (_, _) => Choose(value);
                tab.KeyDown += (_, e) =>
                {
                    if (e.Key is not (Key.Enter or Key.Space)) return;
                    e.Handled = true;
                    Choose(value);
                };
                strip.Children.Add(tab);
            }
        }
        void Choose(string value)
        {
            if (value == current) return;
            current = value;
            view.UiState[stateKey] = value;
            if (S(p, "name") is { } name) view.Session.SetLocal(name, JsonValue.Create(value));
            DrawStrip();
            Show(value);
            Fire(view, node, "onChange", new JsonObject { ["value"] = value });
        }
        DrawStrip();
        Show(current);
        column.Children.Add(stripHost);
        column.Children.Add(pane);
        return column;
    }

    private static Control AccordionItemOf(PluginViewHost view, NodeHost host, JsonObject p)
        => new AccordionItem { Header = S(p, "title"), Content = Column(view, host, 8), IsExpanded = B(p, "defaultOpen") };

    private static Control StepperOf(JsonObject p)
    {
        var current = (int)(double.IsNaN(N(p, "current")) ? 0 : N(p, "current"));
        var panel = new StackPanel { Orientation = S(p, "direction") == "vertical" ? Orientation.Vertical : Orientation.Horizontal, Spacing = 16 };
        var i = 0;
        foreach (var step in (p["steps"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var done = i < current || step["status"]?.ToString() == "done";
            var badge = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(i == current ? 2 : 1), Child = new TextBlock { Text = done ? "✓" : (i + 1).ToString(CultureInfo.InvariantCulture), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            badge.Token(Border.BorderBrushProperty, i <= current ? ArcDesignTokenKind.Accent : ArcDesignTokenKind.Border);
            if (done) badge.Token(Border.BackgroundProperty, ArcDesignTokenKind.AccentSubtle);
            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(Small(step["title"]?.ToString() ?? ""));
            if (step["description"]?.ToString() is { } d) text.Children.Add(Muted(d));
            panel.Children.Add(Inline(8, badge, text));
            i++;
        }
        return panel;
    }

    private static Control TimelineOf(PluginViewHost view, JsonObject p)
    {
        var panel = new StackPanel { Spacing = 12 };
        foreach (var item in (p["items"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 4, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            dot.Token(Border.BackgroundProperty, ToneToken(item["tone"]?.ToString() ?? "accent"));
            var text = new StackPanel { Spacing = 1 };
            if (item["time"] is { } t) text.Children.Add(Muted(PluginUiProtocol.Format("datetime", t, view.Locale)));
            text.Children.Add(KitStyle.Text(item["title"]?.ToString() ?? "", ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium));
            if (item["detail"]?.ToString() is { } d) text.Children.Add(Small(d, color: ArcDesignTokenKind.TextSecondary));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(dot);
            row.Children.Add(text);
            panel.Children.Add(row);
        }
        return panel;
    }

    private static Control BreadcrumbOf(PluginViewHost view, JsonObject p)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var first = true;
        foreach (var item in (p["items"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (!first) panel.Children.Add(Muted("/"));
            first = false;
            var label = item["label"]?.ToString() ?? "";
            var action = item["action"] is JsonObject a ? a["action"]?.ToString() : item["action"]?.ToString();
            panel.Children.Add(action is null ? Small(label) : Ui.Button(label, () => view.Session.Send(action, (item["action"] as JsonObject)?["args"] ?? new JsonObject()), ButtonVariant.Ghost));
        }
        return panel;
    }

    private static Control ToolbarOf(PluginViewHost view, JsonObject p)
    {
        var dock = new DockPanel { LastChildFill = false };
        var start = Nodes(view, p["start"]);
        var end = Nodes(view, p["end"]);
        DockPanel.SetDock(start, Dock.Left);
        DockPanel.SetDock(end, Dock.Right);
        dock.Children.Add(start);
        dock.Children.Add(end);
        return dock;
    }

    // ---------------------------------------------------------------- text & display

    private static Control TextOf(PluginViewHost view, JsonObject p)
    {
        var text = S(p, "format") is { } f ? PluginUiProtocol.Format(f, p["value"], view.Locale) : p["value"]?.ToString() ?? "";
        var variant = S(p, "variant");
        var size = variant is "caption" or "label" ? ArcDesignTokenKind.TypeXS : ArcDesignTokenKind.TypeSM;
        var color = S(p, "tone") is { } tone ? ToneToken(tone) : variant == "caption" ? ArcDesignTokenKind.TextMuted : variant == "label" ? ArcDesignTokenKind.TextSecondary : ArcDesignTokenKind.Foreground;
        var weight = S(p, "weight") switch { "bold" => FontWeight.Bold, "semibold" => FontWeight.SemiBold, "medium" => FontWeight.Medium, _ => variant == "label" ? FontWeight.Medium : FontWeight.Normal };
        var block = KitStyle.Text(text, size, color, weight);
        var lines = N(p, "truncate");
        if (double.IsNaN(lines) || lines > 1)
        {
            block.TextWrapping = TextWrapping.Wrap;
            block.TextTrimming = double.IsNaN(lines) ? TextTrimming.None : TextTrimming.CharacterEllipsis;
            if (!double.IsNaN(lines)) block.MaxLines = (int)lines;
        }
        if (variant == "mono") block.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        return B(p, "selectable") ? new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 } : block;
    }

    private static Control Heading(JsonObject p)
    {
        var level = double.IsNaN(N(p, "level")) ? 2 : (int)Math.Clamp(N(p, "level"), 1, 4);
        var size = level switch { 1 => ArcDesignTokenKind.Type2XL, 2 => ArcDesignTokenKind.TypeXL, 3 => ArcDesignTokenKind.TypeLG, _ => ArcDesignTokenKind.TypeBase };
        var block = KitStyle.Text(p["value"]?.ToString() ?? "", size, ArcDesignTokenKind.Foreground, FontWeight.SemiBold);
        block.TextWrapping = TextWrapping.Wrap;
        block.TextTrimming = TextTrimming.None;
        return block;
    }

    /// <summary>RichText (and parsed Markdown): spans with bold / italic / strike / code / links as inlines.</summary>
    private static Control RichText(PluginViewHost view, JsonObject p)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = S(p, "variant") == "caption" ? 12 : 13 };
        block.Token(TextBlock.ForegroundProperty, S(p, "variant") == "caption" ? ArcDesignTokenKind.TextSecondary : ArcDesignTokenKind.Foreground);
        block.Inlines = [];
        var links = new List<(int Start, int End, JsonObject Span)>();
        var at = 0;
        foreach (var span in (p["spans"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var text = span["text"]?.ToString() ?? "";
            var run = new Avalonia.Controls.Documents.Run(text);
            if (span["bold"] is not null) run.FontWeight = FontWeight.SemiBold;
            if (span["italic"] is not null) run.FontStyle = FontStyle.Italic;
            if (span["strike"] is not null) run.TextDecorations = TextDecorations.Strikethrough;
            if (span["code"] is not null) run.FontFamily = new FontFamily("Menlo, Consolas, monospace");
            if (span["link"] is not null)
            {
                run.TextDecorations = TextDecorations.Underline;
                run.Foreground = new SolidColorBrush(Color.Parse("#0a84ff"));
                links.Add((at, at + text.Length, span));
            }
            block.Inlines.Add(run);
            at += text.Length;
        }
        if (links.Count > 0)
        {
            block.Cursor = new Cursor(StandardCursorType.Hand);
            block.PointerReleased += (_, e) =>
            {
                var hit = block.TextLayout.HitTestPoint(e.GetPosition(block));
                var index = hit.TextPosition;
                var link = links.FirstOrDefault(l => index >= l.Start && index < l.End);
                if (link.Span is null) return;
                if (link.Span["action"]?.ToString() is { } action) view.Session.Send(action, new JsonObject { ["href"] = link.Span["link"]?.DeepClone() });
                else view.Link(link.Span["link"]?.ToString() ?? "");
            };
        }
        return block;
    }

    private static Control Diff(JsonObject p)
    {
        var before = (S(p, "before") ?? "").Split('\n');
        var after = (S(p, "after") ?? "").Split('\n');
        var set = new HashSet<string>(before);
        var lines = before.Where(l => !after.Contains(l)).Select(l => "- " + l).Concat(after.Select(l => (set.Contains(l) ? "  " : "+ ") + l));
        return new CodeBlock { Code = string.Join('\n', lines), Language = "diff", MaxLines = 30 };
    }

    private static Control LinkOf(PluginViewHost view, JsonObject p, JsonObject node)
        => Ui.Button(S(p, "label") ?? S(p, "href") ?? "", () =>
        {
            if (!Fire(view, node, "onPress") && S(p, "href") is { } href) view.Link(href);
        }, ButtonVariant.Ghost);

    private static Control LinkTo(PluginViewHost view, string label, string href) => Ui.Button(label, () => view.Link(href), ButtonVariant.Ghost);

    private static Control ImageOf(PluginViewHost view, JsonObject p)
    {
        var dark = view.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        var src = p["src"] is JsonObject themed ? (dark ? themed["dark"]?.ToString() : null) ?? themed["light"]?.ToString() : S(p, "src");
        var fit = S(p, "fit");
        var image = new Image
        {
            Stretch = fit == "cover" ? Stretch.UniformToFill : fit == "fill" ? Stretch.Fill : Stretch.Uniform,
            // like an <img>: never drawn larger than it is, only scaled down to fit
            StretchDirection = fit is "cover" or "fill" ? StretchDirection.Both : StretchDirection.DownOnly,
            MaxHeight = 480,
        };
        if (S(p, "alt") is { Length: > 0 } alt) Avalonia.Automation.AutomationProperties.SetName(image, alt);
        var frame = new Border { Child = image, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left };
        if (S(p, "radius") == "lg") frame.Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusPanel);
        else if (S(p, "radius") == "md") frame.Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusControl);
        if (N(p, "ratio") is > 0 and var ratio)
        {
            // aspect-ratio: the frame takes the width it gets and a height from the ratio
            frame.HorizontalAlignment = HorizontalAlignment.Stretch;
            frame.SizeChanged += (_, e) =>
            {
                var h = Math.Min(480, e.NewSize.Width / ratio);
                if (Math.Abs(image.Height - h) > 0.5 || double.IsNaN(image.Height)) image.Height = h;
            };
            image.StretchDirection = StretchDirection.Both;
        }
        if (src is null) return frame;
        _ = LoadImageAsync(view, image, src, dark);
        return frame;
    }

    private static async Task LoadImageAsync(PluginViewHost view, Image image, string src, bool dark)
    {
        try
        {
            var svg = SvgDocument.IsSvg(src);
            byte[]? bytes = null;
            if (src.StartsWith("data:image/", StringComparison.Ordinal))
            {
                if (!svg) bytes = Convert.FromBase64String(src[(src.IndexOf(',') + 1)..]);
            }
            else if (svg && !src.StartsWith("https://", StringComparison.Ordinal))
            {
                // a plugin's own SVG asset is text: it comes through the app's API client like the rest of the console
                var text = await view.Api.Client.GetTextAsync($"/api/plugins/v2/{Uri.EscapeDataString(view.Session.Target.Plugin)}/assets/{AssetPath(src)}");
                bytes = System.Text.Encoding.UTF8.GetBytes(text);
            }
            else
            {
                // https images are shown as the web shows them; anything else is one of the plugin's own assets
                var url = src.StartsWith("https://", StringComparison.Ordinal)
                    ? src
                    : view.Api.Client.BaseUrl + $"/api/plugins/v2/{Uri.EscapeDataString(view.Session.Target.Plugin)}/assets/{AssetPath(src)}";
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                using var response = await http.GetAsync(url);
                response.EnsureSuccessStatusCode();
                bytes = await response.Content.ReadAsByteArrayAsync();
                svg = svg || response.Content.Headers.ContentType?.MediaType == "image/svg+xml";
            }
            if (svg)
            {
                var doc = SvgDocument.TextOf(src, bytes) is { } text ? SvgDocument.TryParse(text) : null;
                if (doc is null) return;
                // currentColor follows the theme's text colour
                image.Source = SvgImage.Create(doc, dark ? Color.FromRgb(0xF2, 0xF2, 0xF2) : Color.FromRgb(0x1A, 0x1A, 0x1A));
                return;
            }
            using var ms = new MemoryStream(bytes!);
            image.Source = new Avalonia.Media.Imaging.Bitmap(ms);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or FormatException or ArgumentException or InvalidOperationException or IOException or Core.Api.ApiException)
        {
        }
    }

    // "./img/a.png" or "/img/a.png" → "img/a.png" (the web's assetUrl); the server refuses anything outside the plugin
    private static string AssetPath(string src) => src.StartsWith("./", StringComparison.Ordinal) ? src[2..] : src.TrimStart('/');

    private static Control AvatarOf(JsonObject p)
    {
        var size = S(p, "size") switch { "lg" => 40.0, "sm" => 20.0, _ => 28.0 };
        var name = S(p, "name") ?? "";
        var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Child = S(p, "icon") is { } icon ? Icon(icon, size / 2, "accent") : new TextBlock { Text = name.Length > 0 ? name[..1].ToUpperInvariant() : "", FontSize = size * 0.4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        return b.Token(Border.BackgroundProperty, ArcDesignTokenKind.AccentSubtle);
    }

    private static Control KvOf(PluginViewHost view, JsonObject p)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,16,*"), RowSpacing = 6 };
        var r = 0;
        foreach (var item in (p["items"] as JsonArray ?? []).OfType<JsonObject>())
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = Muted(item["label"]?.ToString() ?? "");
            Grid.SetRow(label, r);
            grid.Children.Add(label);
            var value = item["value"]?.ToString() ?? "—";
            Control v = item["copy"] is not null ? Inline(4, Small(value, item["mono"] is not null), Ui.IconButton(LucideIconKind.Copy, "copy", () => PluginViewHost.Copy(view, value))) : Small(value, item["mono"] is not null);
            Grid.SetRow(v, r);
            Grid.SetColumn(v, 2);
            grid.Children.Add(v);
            r++;
        }
        return grid;
    }

    private static Control AlertOf(PluginViewHost view, JsonObject p, JsonObject node)
    {
        var body = new StackPanel { Spacing = 8 };
        if (S(p, "message") is { } m) body.Children.Add(Small(m));
        if (p["actions"] is JsonArray) body.Children.Add(Nodes(view, p["actions"]));
        var alert = new Alert
        {
            Tone = S(p, "tone") switch { "danger" => AlertTone.Danger, "warning" => AlertTone.Warning, "success" => AlertTone.Success, _ => AlertTone.Info },
            Title = S(p, "title"),
            Content = body,
        };
        if (Has(p, "onDismiss") || B(p, "dismissible"))
        {
            var wrap = new DockPanel();
            var close = Ui.IconButton(LucideIconKind.X, Loc.T("common.close"), () =>
            {
                wrap.IsVisible = false;
                Fire(view, node, "onDismiss");
            });
            DockPanel.SetDock(close, Dock.Right);
            wrap.Children.Add(close);
            wrap.Children.Add(alert);
            return wrap;
        }
        return alert;
    }

    private static Control EmptyOf(PluginViewHost view, JsonObject p)
    {
        var column = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24) };
        column.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Center, Child = Icon(S(p, "icon") ?? "inbox", 24) });
        column.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Center, Child = KitStyle.Text(S(p, "title") ?? "", ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium) });
        if (S(p, "detail") is { } d) column.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Center, Child = Muted(d) });
        if (p["action"] is JsonObject a) column.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Center, Child = view.Detached(a) });
        return column;
    }

    private static Control ProgressOf(JsonObject p, double value, string? label, string? tone)
    {
        var column = new StackPanel { Spacing = 4 };
        if (label is not null) column.Children.Add(Small(label, color: ArcDesignTokenKind.TextSecondary));
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, IsIndeterminate = double.IsNaN(value), Value = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1) };
        bar.Token(TemplatedControl.ForegroundProperty, ToneToken(tone ?? (value > 0.9 ? "danger" : value > 0.75 ? "warning" : "accent")));
        column.Children.Add(bar);
        _ = p;
        return column;
    }

    // ---------------------------------------------------------------- actions

    private static Control ButtonOf(PluginViewHost view, JsonObject p, JsonObject node)
    {
        var variant = S(p, "variant") switch
        {
            "primary" => ButtonVariant.Primary,
            "danger" => ButtonVariant.Danger,
            "ghost" or "link" => ButtonVariant.Ghost,
            _ => ButtonVariant.Secondary,
        };
        var busy = B(p, "loading") || (view.Session.BusyKey is { } k && k == node["key"]?.ToString());
        LucideIconKind? icon = S(p, "icon") is { } name && Enum.TryParse<LucideIconKind>(string.Concat(name.Split('-').Select(x => x.Length > 0 ? char.ToUpperInvariant(x[0]) + x[1..] : x)), true, out var kind) ? kind : null;
        return Ui.Button(S(p, "label"), () => Fire(view, node, "onPress"), variant, icon, busy, size: S(p, "size") == "lg" ? ControlSize.Medium : ControlSize.Small);
    }

    private static Control IconButtonOf(PluginViewHost view, JsonObject p, JsonObject node)
    {
        var name = S(p, "icon") ?? "circle";
        var kind = Enum.TryParse<LucideIconKind>(string.Concat(name.Split('-').Select(x => x.Length > 0 ? char.ToUpperInvariant(x[0]) + x[1..] : x)), true, out var k) ? k : LucideIconKind.Circle;
        return Ui.IconButton(kind, S(p, "a11yLabel") ?? name, () => Fire(view, node, "onPress"));
    }

    private static Control ConfirmButtonOf(PluginViewHost view, JsonObject p, JsonObject node)
    {
        var armed = false;
        ArcButton? button = null;
        button = Ui.Button(S(p, "label"), () =>
        {
            if (armed)
            {
                Fire(view, node, "onPress");
                armed = false;
                button!.Content = S(p, "label");
                button.Variant = ButtonVariant.Secondary;
                return;
            }
            armed = true;
            button!.Content = S(p, "confirmLabel") ?? S(p, "label");
            button.Variant = ButtonVariant.Danger;
            DispatcherTimer.RunOnce(() =>
            {
                armed = false;
                button.Content = S(p, "label");
                button.Variant = ButtonVariant.Secondary;
            }, TimeSpan.FromSeconds(4));
        });
        return button;
    }

    private static Control MenuOf(PluginViewHost view, JsonArray? items, string label)
        => Ui.Menu(label, LucideIconKind.Ellipsis, (items ?? []).OfType<JsonObject>().Where(i => i["separator"] is null).Select(i =>
            new Ui.MenuItem(i["label"]?.ToString() ?? "", null, () => SendAction(view, i["action"]), i["tone"]?.ToString() == "danger")));

    private static void SendAction(PluginViewHost view, JsonNode? action)
    {
        if (action is JsonValue v && v.TryGetValue<string>(out var name)) view.Session.Send(name, new JsonObject());
        else if (action is JsonObject o && o["action"]?.ToString() is { } a) view.Session.Send(a, o["args"]?.DeepClone() ?? new JsonObject());
    }

    private static Control ContextMenuOf(PluginViewHost view, NodeHost host, JsonObject p)
    {
        var body = Column(view, host, 0);
        var menu = new Avalonia.Controls.ContextMenu();
        foreach (var item in (p["items"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (item["separator"] is not null)
            {
                menu.Items.Add(new Separator());
                continue;
            }
            var entry = new Avalonia.Controls.MenuItem { Header = item["label"]?.ToString() };
            var action = item["action"];
            entry.Click += (_, _) => SendAction(view, action);
            menu.Items.Add(entry);
        }
        body.ContextMenu = menu;
        return body;
    }

    // ---------------------------------------------------------------- inputs

    private static Control TextInput(PluginViewHost view, JsonObject node, JsonObject p, bool secret)
    {
        var input = new Input
        {
            Text = Value(view, node)?.ToString() ?? "",
            PlaceholderText = S(p, "placeholder") ?? (secret && B(p, "hasValue") ? Loc.T("pluginUi.secretSet") : null),
            Size = ControlSize.Small,
            MinWidth = 200,
        };
        if (secret) input.PasswordChar = '•';
        if (!double.IsNaN(N(p, "maxLength"))) input.MaxLength = (int)N(p, "maxLength");
        var name = S(p, "name");
        if (name is not null && view.Session.FieldErrors.TryGetValue(name, out var error)) ToolTip.SetTip(input, error);
        input.TextChanged += (_, _) => Set(view, node, JsonValue.Create(input.Text ?? ""), false);
        input.LostFocus += (_, _) =>
        {
            if (S(p, "commit") == "blur") Set(view, node, JsonValue.Create(input.Text ?? ""), true);
        };
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            if (node["type"]?.ToString() == "SearchField") Fire(view, node, "onSearch", new JsonObject { ["query"] = input.Text ?? "" });
            else if (S(p, "commit") == "blur") Set(view, node, JsonValue.Create(input.Text ?? ""), true);
        };
        if (node["type"]?.ToString() == "SearchField" && Has(p, "onSearch"))
            input.TextChanged += (_, _) => Debounce(node, TimeSpan.FromMilliseconds(300), () => Fire(view, node, "onSearch", new JsonObject { ["query"] = input.Text ?? "" }));
        return Tagged(input, node);
    }

    private static Control TextAreaOf(PluginViewHost view, JsonObject node, JsonObject p)
    {
        var area = new Textarea { Text = Value(view, node)?.ToString() ?? "", AcceptsReturn = true, MinHeight = (double.IsNaN(N(p, "rows")) ? 4 : N(p, "rows")) * 20, TextWrapping = TextWrapping.Wrap };
        if (B(p, "mono") || node["type"]?.ToString() == "CodeField") area.Token(TextBox.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        area.TextChanged += (_, _) => Set(view, node, JsonValue.Create(area.Text ?? ""), false);
        area.LostFocus += (_, _) =>
        {
            if (S(p, "commit") == "blur") Set(view, node, JsonValue.Create(area.Text ?? ""), true);
        };
        return Tagged(area, node);
    }

    private static Control NumberOf(PluginViewHost view, JsonObject node, JsonObject p, bool money)
    {
        var raw = Num(Value(view, node));
        var field = new NumberField { Width = 160, HorizontalAlignment = HorizontalAlignment.Left, MaximumFractionDigits = money ? 6 : B(p, "integer") ? 0 : 6 };
        if (!double.IsNaN(raw)) field.Value = (decimal)(money ? raw / 1e9 : raw);
        if (!double.IsNaN(N(p, "min"))) field.Minimum = (decimal)N(p, "min");
        if (!double.IsNaN(N(p, "max"))) field.Maximum = (decimal)N(p, "max");
        if (!double.IsNaN(N(p, "step"))) field.Step = (decimal)N(p, "step");
        field.ValueChanged += (_, _) =>
        {
            var v = (double)field.Value;
            Set(view, node, money ? JsonValue.Create((long)Math.Round(v * 1e9)) : B(p, "integer") ? JsonValue.Create((long)Math.Round(v)) : JsonValue.Create(v), false);
        };
        return S(p, "unit") is { } unit ? Tagged(Inline(6, field, Muted(unit)), node) : Tagged(field, node);
    }

    private static Control SwitchOf(PluginViewHost view, JsonObject node, JsonObject p)
    {
        var on = Value(view, node) is JsonValue v && v.GetValueKind() == JsonValueKind.True;
        var toggle = new ControlledSwitch { IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
        toggle.ToggleRequested += want =>
        {
            toggle.SetValue(want); // optimistic: shown at once, a later patch puts it back if the plugin refused it
            Set(view, node, JsonValue.Create(want), true);
        };
        return S(p, "label") is { } label ? Tagged(Inline(8, toggle, Small(label)), node) : Tagged(toggle, node);
    }

    private static Control CheckboxOf(PluginViewHost view, JsonObject node, JsonObject p)
    {
        var box = new Checkbox { IsChecked = Value(view, node) is JsonValue v && v.GetValueKind() == JsonValueKind.True, Content = S(p, "label") };
        if (B(p, "indeterminate")) box.IsChecked = null;
        box.IsCheckedChanged += (_, _) => Set(view, node, JsonValue.Create(box.IsChecked == true), true);
        return Tagged(box, node);
    }

    private static Control SegmentedOf(PluginViewHost view, JsonObject node, JsonObject p)
    {
        var options = Options(view, p);
        var seg = new ControlledSegmented(options.Select(o => (o.Value, o.Label)), Value(view, node)?.ToString()) { HorizontalAlignment = HorizontalAlignment.Left };
        seg.Chose += v =>
        {
            seg.SetValue(v);
            Set(view, node, JsonValue.Create(v), true);
        };
        return Tagged(seg, node);
    }

    private static Control SelectOf(PluginViewHost view, JsonObject node, JsonObject p, List<(string Value, string Label, string? Description)> options)
    {
        var select = new ControlledSelect(options.Select(o => (o.Value, o.Label)), Value(view, node)?.ToString(), 220);
        select.Chose += v =>
        {
            select.SetValue(v);
            Set(view, node, JsonValue.Create(v), true);
        };
        return Tagged(select, node);
    }

    private static Control ChipsOf(PluginViewHost view, JsonObject node, JsonObject p, List<(string Value, string Label, string? Description)> options, bool multiple)
    {
        var value = Value(view, node, multiple ? new JsonArray() : null);
        var chosen = value is JsonArray a ? a.Select(x => x?.ToString() ?? "").ToHashSet() : value?.ToString() is { } one ? [one] : [];
        var max = N(p, "max");
        var panel = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        void Draw()
        {
            panel.Children.Clear();
            foreach (var (v, label, _) in options)
                panel.Children.Add(Ui.Button(label, () =>
                {
                    if (multiple)
                    {
                        if (!chosen.Remove(v) && (double.IsNaN(max) || chosen.Count < max)) chosen.Add(v);
                        Set(view, node, new JsonArray([.. chosen.Select(x => (JsonNode?)JsonValue.Create(x))]), true);
                    }
                    else
                    {
                        chosen = [v];
                        Set(view, node, JsonValue.Create(v), true);
                    }
                    Draw();
                }, chosen.Contains(v) ? ButtonVariant.Primary : ButtonVariant.Secondary));
        }
        Draw();
        return Tagged(panel, node);
    }

    private static Control TagsOf(PluginViewHost view, JsonObject node, JsonObject p)
    {
        var tags = new TagInput { Placeholder = S(p, "placeholder"), Value = (Value(view, node, new JsonArray()) as JsonArray ?? []).Select(x => x?.ToString() ?? "").ToList() };
        if (!double.IsNaN(N(p, "max"))) tags.MaxTags = (int)N(p, "max");
        tags.ValueChanged += (_, _) => Set(view, node, new JsonArray([.. tags.Value.Select(x => (JsonNode?)JsonValue.Create(x))]), true);
        return Tagged(tags, node);
    }

    private static Control SliderOf(PluginViewHost view, JsonObject node, JsonObject p)
    {
        var slider = new TKCode.Arc.Slider
        {
            Minimum = double.IsNaN(N(p, "min")) ? 0 : N(p, "min"),
            Maximum = double.IsNaN(N(p, "max")) ? 100 : N(p, "max"),
            Width = 240,
        };
        var v = Num(Value(view, node));
        slider.Value = double.IsNaN(v) ? slider.Minimum : v;
        if (!double.IsNaN(N(p, "step"))) slider.SmallChange = N(p, "step");
        slider.ValueCommitted += (_, _) => Set(view, node, JsonValue.Create(slider.Value), true);
        return Tagged(slider, node);
    }

    private static Control DateOf(PluginViewHost view, JsonObject node, JsonObject p)
    {
        var withTime = B(p, "withTime");
        var value = PluginUiProtocol.DateOf(Value(view, node));
        var picker = new TKCode.Arc.DatePicker { SelectedDate = value is { } d ? DateOnly.FromDateTime(d) : null, FirstDayOfWeek = view.Locale == "zh" ? DayOfWeek.Monday : DayOfWeek.Sunday };
        if (PluginUiProtocol.ResolveDate(S(p, "min")) is { } min) picker.MinDate = min;
        if (PluginUiProtocol.ResolveDate(S(p, "max")) is { } max) picker.MaxDate = max;
        if (!withTime)
        {
            picker.SelectedDateChanged += (_, _) => Set(view, node, picker.SelectedDate is { } day ? JsonValue.Create(PluginUiProtocol.Iso(day)) : null, true);
            return Tagged(picker, node);
        }
        var time = new TKCode.Arc.TimePicker { SelectedTime = value is { } t ? TimeOnly.FromDateTime(t) : new TimeOnly(9, 0), MinuteStep = double.IsNaN(N(p, "minuteStep")) ? 5 : (int)N(p, "minuteStep") };
        void Emit()
        {
            if (picker.SelectedDate is not { } day) return;
            var at = day.ToDateTime(time.SelectedTime ?? new TimeOnly(9, 0), DateTimeKind.Local);
            Set(view, node, JsonValue.Create(at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)), true);
        }
        picker.SelectedDateChanged += (_, _) => Emit();
        time.SelectedTimeChanged += (_, _) => Emit();
        return Tagged(Inline(8, picker, time), node);
    }

    private static Control RangeOf(PluginViewHost view, JsonObject node, JsonObject p)
    {
        var picker = new DateRangePicker { FirstDayOfWeek = view.Locale == "zh" ? DayOfWeek.Monday : DayOfWeek.Sunday };
        if (Value(view, node) is JsonObject r && PluginUiProtocol.ResolveDate(r["from"]?.ToString()) is { } from && PluginUiProtocol.ResolveDate(r["to"]?.ToString()) is { } to)
            picker.SelectedRange = new DateRange(from, to);
        if (PluginUiProtocol.ResolveDate(S(p, "min")) is { } min) picker.MinDate = min;
        if (PluginUiProtocol.ResolveDate(S(p, "max")) is { } max) picker.MaxDate = max;
        var presets = new List<DateRangePreset>();
        foreach (var preset in p["presets"] as JsonArray ?? [])
        {
            if (preset is JsonValue pv && pv.TryGetValue<string>(out var name) && PluginUiProtocol.Preset(name) is { } range)
                presets.Add(new DateRangePreset(Loc.T("pluginUi.preset." + name), _ => new DateRange(range.From, range.To)));
            else if (preset is JsonObject po && PluginUiProtocol.ResolveDate(po["from"]?.ToString()) is { } pf && PluginUiProtocol.ResolveDate(po["to"]?.ToString()) is { } pt)
                presets.Add(new DateRangePreset(po["label"]?.ToString() ?? "", _ => new DateRange(pf, pt)));
        }
        if (presets.Count > 0) picker.Presets = presets;
        picker.SelectedRangeChanged += (_, _) =>
        {
            if (picker.SelectedRange is not { } sel) return;
            var (start, end) = (sel.Start, sel.End);
            if (!double.IsNaN(N(p, "maxSpanDays")) && end.DayNumber - start.DayNumber + 1 > N(p, "maxSpanDays")) start = end.AddDays(1 - (int)N(p, "maxSpanDays"));
            Set(view, node, new JsonObject { ["from"] = PluginUiProtocol.Iso(start), ["to"] = PluginUiProtocol.Iso(end) }, true);
        };
        return Tagged(picker, node);
    }

    private static Control TimeOf(PluginViewHost view, JsonObject node, JsonObject p)
    {
        var picker = new TKCode.Arc.TimePicker { MinuteStep = double.IsNaN(N(p, "minuteStep")) ? 5 : (int)N(p, "minuteStep") };
        if (TimeOnly.TryParseExact(Value(view, node)?.ToString(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) picker.SelectedTime = t;
        picker.SelectedTimeChanged += (_, _) => Set(view, node, picker.SelectedTime is { } st ? JsonValue.Create(st.ToString("HH:mm", CultureInfo.InvariantCulture)) : null, true);
        return Tagged(picker, node);
    }

    private static Control KeyValueOf(PluginViewHost view, JsonObject node, JsonObject p)
    {
        var rows = (Value(view, node, new JsonArray()) as JsonArray ?? []).OfType<JsonObject>().Select(r => (Key: r["key"]?.ToString() ?? "", Val: r["value"]?.ToString() ?? "")).ToList();
        var column = new StackPanel { Spacing = 6 };
        void Emit() => Set(view, node, new JsonArray([.. rows.Select(r => (JsonNode?)new JsonObject { ["key"] = r.Key, ["value"] = r.Val })]), false);
        void Draw()
        {
            column.Children.Clear();
            for (var i = 0; i < rows.Count; i++)
            {
                var index = i;
                var k = new Input { Text = rows[i].Key, PlaceholderText = S(p, "keyPlaceholder"), Width = 160, Size = ControlSize.Small };
                k.Token(TextBox.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
                var v = new Input { Text = rows[i].Val, PlaceholderText = S(p, "valuePlaceholder"), Width = 220, Size = ControlSize.Small };
                if (B(p, "secretValues")) v.PasswordChar = '•';
                k.TextChanged += (_, _) =>
                {
                    rows[index] = (k.Text ?? "", rows[index].Val);
                    Emit();
                };
                v.TextChanged += (_, _) =>
                {
                    rows[index] = (rows[index].Key, v.Text ?? "");
                    Emit();
                };
                column.Children.Add(Inline(6, k, v, Ui.IconButton(LucideIconKind.X, Loc.T("common.delete"), () =>
                {
                    rows.RemoveAt(index);
                    Emit();
                    Draw();
                })));
            }
            column.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = Ui.Button(Loc.T("pluginUi.add"), () =>
                {
                    rows.Add(("", ""));
                    Draw();
                }, ButtonVariant.Ghost, LucideIconKind.Plus),
            });
        }
        Draw();
        return Tagged(column, node);
    }

    private static Control ListEditorOf(PluginViewHost view, JsonObject node, JsonObject p)
    {
        var fields = (p["item"] as JsonArray ?? [(JsonNode?)new JsonObject { ["name"] = "value" }]).OfType<JsonObject>().Select(f => (Name: f["name"]?.ToString() ?? "value", Placeholder: f["placeholder"]?.ToString() ?? f["label"]?.ToString(), Number: f["type"]?.ToString() == "number")).ToList();
        var rows = (Value(view, node, new JsonArray()) as JsonArray ?? []).OfType<JsonObject>().Select(r => (JsonObject)r.DeepClone()).ToList();
        var column = new StackPanel { Spacing = 6 };
        void Emit() => Set(view, node, new JsonArray([.. rows.Select(r => (JsonNode?)r.DeepClone())]), false);
        void Draw()
        {
            column.Children.Clear();
            for (var i = 0; i < rows.Count; i++)
            {
                var index = i;
                var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                foreach (var f in fields)
                {
                    var input = new Input { Text = rows[i][f.Name]?.ToString() ?? "", PlaceholderText = f.Placeholder, Width = 180, Size = ControlSize.Small };
                    input.TextChanged += (_, _) =>
                    {
                        rows[index][f.Name] = f.Number && double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d) : JsonValue.Create(input.Text ?? "");
                        Emit();
                    };
                    line.Children.Add(input);
                }
                line.Children.Add(Ui.IconButton(LucideIconKind.X, Loc.T("common.delete"), () =>
                {
                    rows.RemoveAt(index);
                    Emit();
                    Draw();
                }));
                column.Children.Add(line);
            }
            if (double.IsNaN(N(p, "max")) || rows.Count < N(p, "max"))
                column.Children.Add(new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = Ui.Button(S(p, "addLabel") ?? Loc.T("pluginUi.add"), () =>
                    {
                        var row = new JsonObject();
                        foreach (var f in fields) row[f.Name] = f.Number ? JsonValue.Create(0) : JsonValue.Create("");
                        rows.Add(row);
                        Emit();
                        Draw();
                    }, ButtonVariant.Ghost, LucideIconKind.Plus),
                });
        }
        Draw();
        return Tagged(column, node);
    }

    private static Control FileDropOf(PluginViewHost view, JsonObject node, JsonObject p)
    {
        var label = Muted(S(p, "label") ?? Loc.T("pluginUi.dropFiles"));
        var zone = new Border { Padding = new Thickness(16, 24), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2), Child = new Border { HorizontalAlignment = HorizontalAlignment.Center, Child = label } };
        zone.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border);
        DragDrop.SetAllowDrop(zone, true);
        async Task Take(IEnumerable<IStorageItem> items)
        {
            var refs = new JsonArray();
            foreach (var file in items.OfType<IStorageFile>().Take(B(p, "multiple") ? 20 : 1))
                if (await view.UploadAsync(file) is { } r) refs.Add(r);
            Fire(view, node, "onDrop", new JsonObject { ["files"] = refs });
        }
        zone.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (e.DataTransfer.TryGetFiles() is { } files) _ = Take(files);
        });
        zone.PointerReleased += async (_, _) =>
        {
            if (PluginViewHost.Top(zone)?.StorageProvider is not { } storage) return;
            await Take(await storage.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = B(p, "multiple") }));
        };
        return zone;
    }

    private static Control FormOf(PluginViewHost view, NodeHost host, JsonObject p, JsonObject node)
    {
        var column = new StackPanel { Spacing = 14 };
        var body = new StackPanel { Spacing = 14 };
        view.Children(host, body.Children);
        column.Children.Add(body);
        if (Has(p, "onSubmit"))
        {
            var save = Ui.Button(Loc.T("pluginUi.save"), () =>
            {
                var values = new JsonObject();
                foreach (var (k, v) in view.Session.Local) values[k] = v?.DeepClone();
                Fire(view, node, "onSubmit", new JsonObject { ["values"] = values });
            }, ButtonVariant.Primary);
            column.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Right, Child = save });
        }
        return column;
    }

    private static Control FieldOf(PluginViewHost view, NodeHost host, JsonObject p)
    {
        var column = new StackPanel { Spacing = 6 };
        column.Children.Add(KitStyle.Text((S(p, "label") ?? "") + (B(p, "required") ? " *" : ""), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, FontWeight.Medium));
        var body = new StackPanel();
        view.Children(host, body.Children);
        column.Children.Add(body);
        var child = (host.Node["children"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        var name = child?["props"]?["name"]?.ToString();
        var error = S(p, "error") ?? (name is not null && view.Session.FieldErrors.TryGetValue(name, out var e) ? e : null);
        if (error is not null) column.Children.Add(Small(error, color: ArcDesignTokenKind.Danger));
        else if ((S(p, "hint") ?? S(p, "info")) is { } hint) column.Children.Add(Muted(hint));
        return column;
    }

    // ---------------------------------------------------------------- data

    private static List<JsonObject> Rows(PluginViewHost view, JsonObject p, string inline)
    {
        if (p["source"] is JsonObject src && src["collection"]?.GetValue<string>() is { } c)
            return view.Session.Collections.TryGetValue(c, out var col) ? col.Items : [];
        return (p[inline] as JsonArray ?? []).OfType<JsonObject>().ToList();
    }

    /// <summary>What a table keeps between renders: its sort and selection, reset when the plugin sets new ones.</summary>
    private sealed class TableState
    {
        public TableSort? Sort;
        public string? SortProp;
        public List<string> Selected = [];
        public string? SelectedProp;
    }

    private static Control TableOf(PluginViewHost view, JsonObject node, JsonObject p)
    {
        var rows = Rows(view, p, "rows");
        var columns = (p["columns"] as JsonArray ?? []).OfType<JsonObject>().Where(c => !(view.WidthClass == "compact" && c["priority"]?.ToString() == "low")).ToList();
        var sourceName = (p["source"] as JsonObject)?["collection"]?.ToString();
        var collection = sourceName is not null && view.Session.Collections.TryGetValue(sourceName, out var col) ? col : null;
        var stateKey = "table:" + (node["key"]?.ToString() ?? sourceName ?? string.Join("|", columns.Select(c => c["key"]?.ToString())));
        var state = view.UiState.TryGetValue(stateKey, out var kept) && kept is TableState ts ? ts : new TableState();
        view.UiState[stateKey] = state;
        var sortProp = p["sort"]?.ToJsonString();
        if (sortProp != state.SortProp)
        {
            state.SortProp = sortProp;
            state.Sort = TableSort.From(p["sort"]);
        }
        var selectedProp = p["selected"]?.ToJsonString();
        if (selectedProp != state.SelectedProp)
        {
            state.SelectedProp = selectedProp;
            state.Selected = [.. (p["selected"] as JsonArray ?? []).Select(k => k?.ToString() ?? "")];
        }
        var total = N(p, "total");
        if (double.IsNaN(total) && collection is not null) total = Num(collection.Meta["total"]);
        var page = N(p, "page");
        if (double.IsNaN(page) && collection is not null) page = Num(collection.Meta["page"]);
        if (double.IsNaN(page)) page = 1;
        var pageSize = N(p, "pageSize") is > 0 and var ps ? ps : 20;
        var serverSide = !double.IsNaN(total);

        if (B(p, "loading") && rows.Count == 0) return Ui.Spinner();
        if (rows.Count == 0) return p["empty"] is JsonObject empty ? view.Detached(empty) : Muted(Loc.T("pluginUi.empty"));

        var rowKey = S(p, "rowKey") ?? "id";
        var selection = S(p, "selection") ?? "none";
        var multiple = selection == "multiple";
        var pressable = Has(p, "onRowPress") || selection == "single";
        var actions = (p["rowActions"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var bulk = (p["bulkActions"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var dense = B(p, "dense");
        var pad = new Thickness(12, dense ? 5 : 8);

        var column = new StackPanel { Spacing = 8 };
        var bulkHost = new ContentControl();
        var tableHost = new ContentControl();
        column.Children.Add(bulkHost);
        column.Children.Add(tableHost);
        if (serverSide)
            column.Children.Add(PagerOf(view, node, p, (int)page, TableLogic.Pages(total, pageSize)));

        void SendSelect(JsonObject? row)
        {
            var payload = new JsonObject { ["keys"] = new JsonArray([.. state.Selected.Select(k => (JsonNode?)JsonValue.Create(k))]) };
            if (row is not null) payload["row"] = row.DeepClone();
            Fire(view, node, "onSelect", payload);
        }

        void Draw()
        {
            var sorted = serverSide ? [.. rows] : TableLogic.Sort(rows, state.Sort);
            var keys = sorted.Select((r, i) => TableLogic.KeyOf(r, rowKey, i)).ToList();

            // bulk actions while something is selected
            if (bulk.Count > 0 && state.Selected.Count > 0)
            {
                var bar = Inline(8, Small(Loc.T("pluginUi.selected", ("count", state.Selected.Count.ToString(CultureInfo.InvariantCulture))), color: ArcDesignTokenKind.TextSecondary));
                foreach (var b in bulk)
                {
                    var action = b["action"]?.ToString() ?? "";
                    bar.Children.Add(Ui.Button(b["label"]?.ToString() ?? action, () => view.Session.Send(action, new JsonObject
                    {
                        ["keys"] = new JsonArray([.. state.Selected.Select(k => (JsonNode?)JsonValue.Create(k))]),
                    }), b["tone"]?.ToString() == "danger" ? ButtonVariant.Danger : ButtonVariant.Secondary));
                }
                bulkHost.Content = bar;
            }
            else bulkHost.Content = null;

            var defs = new List<string>();
            if (multiple) defs.Add("Auto");
            defs.AddRange(columns.Select(c => Width(c["width"])));
            if (actions.Count > 0) defs.Add("Auto");
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", defs)) };
            var first = multiple ? 1 : 0;
            var span = defs.Count;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            // header
            var head = new Border { BorderThickness = new Thickness(0, 0, 0, 1) }.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border);
            if (B(p, "stickyHeader")) head.Token(Border.BackgroundProperty, ArcDesignTokenKind.Surface);
            Grid.SetColumnSpan(head, span);
            grid.Children.Add(head);
            if (multiple)
            {
                var all = new Checkbox { IsChecked = keys.Count > 0 && keys.All(state.Selected.Contains), VerticalAlignment = VerticalAlignment.Center };
                Avalonia.Automation.AutomationProperties.SetName(all, Loc.T("pluginUi.selected", ("count", keys.Count.ToString(CultureInfo.InvariantCulture))));
                all.IsCheckedChanged += (_, _) =>
                {
                    state.Selected = TableLogic.ToggleAll(state.Selected, keys);
                    SendSelect(null);
                    Dispatcher.UIThread.Post(Draw);
                };
                var allCell = new Border { Padding = new Thickness(12, 0, 0, 0), Child = all };
                grid.Children.Add(allCell);
            }
            for (var c = 0; c < columns.Count; c++)
            {
                var colDef = columns[c];
                var key = colDef["key"]?.ToString() ?? "";
                var arrow = state.Sort?.Key == key ? (state.Sort.Dir == "asc" ? " ↑" : " ↓") : "";
                var title = KitStyle.Text((colDef["title"]?.ToString() ?? key) + arrow, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, FontWeight.Medium);
                var cell = new Border { Padding = pad, Child = title, Background = Brushes.Transparent };
                Align(cell, title, colDef);
                if (colDef["sortable"] is JsonValue sv && sv.GetValueKind() == JsonValueKind.True)
                {
                    cell.Cursor = new Cursor(StandardCursorType.Hand);
                    cell.Focusable = true;
                    void SortBy()
                    {
                        state.Sort = TableLogic.NextSort(state.Sort, key);
                        Fire(view, node, "onSort", new JsonObject { ["sort"] = state.Sort.ToJson() });
                        Draw();
                    }
                    cell.PointerReleased += (_, _) => SortBy();
                    cell.KeyDown += (_, e) =>
                    {
                        if (e.Key is not (Key.Enter or Key.Space)) return;
                        e.Handled = true;
                        SortBy();
                    };
                }
                Grid.SetColumn(cell, first + c);
                grid.Children.Add(cell);
            }

            // rows
            for (var r = 0; r < sorted.Count; r++)
            {
                var row = sorted[r];
                var key = keys[r];
                var selected = state.Selected.Contains(key);
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var back = new Border { BorderThickness = new Thickness(0, 0, 0, r == sorted.Count - 1 ? 0 : 1), Background = Brushes.Transparent }
                    .Token(Border.BorderBrushProperty, ArcDesignTokenKind.BorderSubtle);
                if (selected) back.Token(Border.BackgroundProperty, ArcDesignTokenKind.AccentSubtle);
                Grid.SetRow(back, r + 1);
                Grid.SetColumnSpan(back, span);
                grid.Children.Add(back);

                void Hover(bool on)
                {
                    if (!pressable || selected) return;
                    if (on) back.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
                    else back.Background = Brushes.Transparent;
                }
                void Press()
                {
                    if (selection == "single")
                    {
                        state.Selected = TableLogic.Toggle(state.Selected, key, multiple: false);
                        SendSelect(row);
                    }
                    Fire(view, node, "onRowPress", new JsonObject { ["row"] = row.DeepClone(), ["key"] = key });
                    if (selection == "single") Dispatcher.UIThread.Post(Draw);
                }

                if (multiple)
                {
                    var box = new Checkbox { IsChecked = selected, VerticalAlignment = VerticalAlignment.Center };
                    Avalonia.Automation.AutomationProperties.SetName(box, key);
                    box.IsCheckedChanged += (_, _) =>
                    {
                        state.Selected = TableLogic.Toggle(state.Selected, key, multiple: true);
                        SendSelect(null);
                        Dispatcher.UIThread.Post(Draw);
                    };
                    var boxCell = new Border { Padding = new Thickness(12, 0, 0, 0), Child = box };
                    Grid.SetRow(boxCell, r + 1);
                    grid.Children.Add(boxCell);
                }
                for (var c = 0; c < columns.Count; c++)
                {
                    var colDef = columns[c];
                    var content = Cell(view, colDef, row);
                    var cell = new Border { Padding = pad, Child = content, Background = Brushes.Transparent };
                    Align(cell, content, colDef);
                    if (pressable)
                    {
                        cell.Cursor = new Cursor(StandardCursorType.Hand);
                        cell.PointerReleased += (_, _) => Press();
                        cell.PointerEntered += (_, _) => Hover(true);
                        cell.PointerExited += (_, _) => Hover(false);
                    }
                    Grid.SetRow(cell, r + 1);
                    Grid.SetColumn(cell, first + c);
                    grid.Children.Add(cell);
                }
                if (actions.Count > 0)
                {
                    var menu = Ui.Menu("…", LucideIconKind.Ellipsis, actions.Select(a => new Ui.MenuItem(a["label"]?.ToString() ?? "",
                        a["icon"]?.ToString() is { Length: > 0 } ic ? PluginShell.IconKind(ic) : null,
                        () => view.Session.Send(a["action"]?.ToString() ?? "", new JsonObject { ["row"] = row.DeepClone(), ["key"] = key }),
                        a["tone"]?.ToString() == "danger")));
                    Grid.SetRow(menu, r + 1);
                    Grid.SetColumn(menu, span - 1);
                    grid.Children.Add(menu);
                }
            }
            tableHost.Content = Surface(new ScrollViewer
            {
                Content = grid,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            }, 0);
        }

        Draw();
        return column;
    }

    // A column width: a number or "120px" is pixels, "30%" a share, anything else an equal share.
    private static string Width(JsonNode? w)
    {
        if (w is JsonValue v && v.GetValueKind() == JsonValueKind.Number) return Num(v).ToString(CultureInfo.InvariantCulture);
        var text = w?.ToString()?.Trim();
        if (text is null) return "*";
        if (text.EndsWith("px", StringComparison.Ordinal) && double.TryParse(text[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var px)) return px.ToString(CultureInfo.InvariantCulture);
        if (text.EndsWith('%') && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pc) && pc > 0) return pc.ToString(CultureInfo.InvariantCulture) + "*";
        return "*";
    }

    private static void Align(Border cell, Control content, JsonObject col)
    {
        var align = col["align"]?.ToString();
        var h = align == "end" ? HorizontalAlignment.Right : align == "center" ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        content.HorizontalAlignment = h;
        if (content is TextBlock tb && align == "end") tb.FontFeatures = FontFeatureCollection.Parse("tnum");
    }

    private static Control Cell(PluginViewHost view, JsonObject col, JsonObject row)
    {
        var key = col["key"]?.ToString() ?? "";
        var v = row[key];
        var text = col["format"]?.ToString() is { } f ? PluginUiProtocol.Format(f, v, view.Locale) : v switch { null => "", JsonValue jv => jv.ToString(), _ => v.ToJsonString() };
        var cell = col["cell"] as JsonObject;
        // labelBy / toneBy are keyed by the raw value (the web's String(v)), not the formatted text
        var raw = v switch { null => "", JsonValue jv => jv.ToString(), _ => v.ToJsonString() };
        switch (cell?["type"]?.ToString())
        {
            case "badge":
                return KitBadge.Create(cell["labelBy"]?[raw]?.ToString() ?? text, ToneOf(cell["toneBy"]?[raw]?.ToString()));
            case "status":
                return Inline(6, new StatusDot(ToneOf(cell["toneBy"]?[raw]?.ToString())), Small(cell["labelBy"]?[raw]?.ToString() ?? text));
            case "icon":
                return Icon(text);
            case "link":
                return Ui.Button(text, () => view.Session.Send(cell["action"]?.ToString() ?? "", new JsonObject { ["row"] = row.DeepClone() }), ButtonVariant.Ghost);
            case "progress":
                return new ProgressBar { Minimum = 0, Maximum = 1, Value = Math.Clamp(Num(v) is var d && double.IsNaN(d) ? 0 : d, 0, 1), Width = 96, Height = 6 };
            case "copy":
                return Inline(4, Small(text, mono: true), Ui.IconButton(LucideIconKind.Copy, "copy", () => PluginViewHost.Copy(view, text)));
            case "avatar":
                var initial = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Child = new TextBlock { Text = text.Length > 0 ? text[..1].ToUpperInvariant() : "", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } }
                    .Token(Border.BackgroundProperty, ArcDesignTokenKind.AccentSubtle);
                ((TextBlock)initial.Child!).Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Accent);
                return Inline(6, initial, Small(text));
            case "tags":
                var tags = new WrapPanel { ItemSpacing = 4 };
                foreach (var t in v as JsonArray ?? []) tags.Children.Add(KitBadge.Create(t?.ToString() ?? ""));
                return tags;
        }
        var block = Small(text, col["mono"] is not null);
        if (col["truncate"] is not null)
        {
            block.TextWrapping = TextWrapping.NoWrap;
            block.TextTrimming = TextTrimming.CharacterEllipsis;
            block.MaxWidth = 260;
            ToolTip.SetTip(block, text);
        }
        return block;
    }

    private static Control PagerOf(PluginViewHost view, JsonObject node, JsonObject p, int page, int pages)
    {
        var size = double.IsNaN(N(p, "pageSize")) ? 20 : N(p, "pageSize");
        void Go(int to) => Fire(view, node, "onPage", new JsonObject { ["page"] = to, ["pageSize"] = size });
        var row = Inline(6,
            Ui.IconButton(LucideIconKind.ChevronLeft, "previous", () => Go(page - 1), page > 1),
            Muted($"{page} / {pages}"),
            Ui.IconButton(LucideIconKind.ChevronRight, "next", () => Go(page + 1), page < pages));
        row.HorizontalAlignment = HorizontalAlignment.Right;
        return row;
    }

    private static Control ListOf(PluginViewHost view, JsonObject node, JsonObject p)
    {
        var items = Rows(view, p, "items");
        if (items.Count == 0) return p["empty"] is JsonObject empty ? view.Detached(empty) : Muted(Loc.T("pluginUi.empty"));
        var group = new SettingsGroup();
        foreach (var item in items)
        {
            var key = item["key"]?.ToString() ?? item["id"]?.ToString() ?? "";
            var row = new SettingsRow { Label = item["title"]?.ToString(), Detail = item["detail"]?.ToString() };
            if (item["icon"]?.ToString() is { } icon) row.Icon = Icon(icon);
            if (item["trailing"] is JsonObject trailing) row.Trailing.Add(view.Detached(trailing));
            if (item["actions"] is JsonArray actions)
                row.Trailing.Add(Ui.Menu("…", LucideIconKind.Ellipsis, actions.OfType<JsonObject>().Select(a => new Ui.MenuItem(a["label"]?.ToString() ?? "", null,
                    () => view.Session.Send(a["action"]?.ToString() ?? "", new JsonObject { ["key"] = key, ["item"] = item.DeepClone() })))));
            if (Has(p, "onItemPress")) row.Invoked += (_, _) => Fire(view, node, "onItemPress", new JsonObject { ["key"] = key, ["item"] = item.DeepClone() });
            group.Rows.Add(row);
        }
        if (Has(p, "onVisible"))
        {
            var more = Ui.Button(Loc.T("pluginUi.more"), () => Fire(view, node, "onVisible"), ButtonVariant.Ghost);
            group.Rows.Add(Ui.GroupLine(more));
        }
        return group;
    }

    /// <summary>The Tree component (04 §7): expand / collapse locally, lazy children through onExpand, selection, checkboxes, search, node menus.</summary>
    private static Control TreeOf(PluginViewHost view, JsonObject node, JsonObject p)
    {
        var col = p["source"] is JsonObject src && src["collection"]?.GetValue<string>() is { } cn && view.Session.Collections.TryGetValue(cn, out var c) ? c : null;
        var nodes = col is not null ? col.Items.Cast<JsonNode?>().ToList() : (p["nodes"] as JsonArray ?? []).ToList();
        var state = TreeStates.GetOrCreateValue(node);
        if (state.Expanded is null)
            state.Expanded = (p["expanded"] as JsonArray ?? []).Select(x => x?.ToString() ?? "").ToHashSet();
        var selection = S(p, "selection") ?? "none";
        var selected = (p["selected"] as JsonArray ?? (S(p, "name") is { } nm && view.Session.Local.TryGetValue(nm, out var l) ? l as JsonArray : null) ?? [])
            .Select(x => x?.ToString() ?? "").ToHashSet();
        var column = new StackPanel { Spacing = 0 };
        var query = state.Query;
        var dense = B(p, "dense");
        var actions = (p["nodeActions"] as JsonArray ?? []).OfType<JsonObject>().ToList();

        IEnumerable<JsonObject> Kids(JsonObject n) => col is not null && col.Children.TryGetValue(n["key"]?.ToString() ?? "", out var lazy)
            ? lazy.OfType<JsonObject>() : (n["children"] as JsonArray ?? []).OfType<JsonObject>();
        bool Matches(JsonObject n) => query.Length == 0 || (n["label"]?.ToString() ?? "").Contains(query, StringComparison.OrdinalIgnoreCase) || Kids(n).Any(Matches);
        IEnumerable<string> Descendants(JsonObject n) => Kids(n).SelectMany(k => new[] { k["key"]?.ToString() ?? "" }.Concat(Descendants(k)));

        void Redraw()
        {
            column.Children.Clear();
            if (B(p, "search"))
            {
                var search = new SearchField { Placeholder = Loc.T("pluginUi.search"), Size = ControlSize.Small, Text = query };
                search.TextChanged += (_, _) =>
                {
                    state.Query = query = search.Text ?? "";
                    Debounce(node, TimeSpan.FromMilliseconds(150), Redraw);
                };
                column.Children.Add(new Border { Margin = new Thickness(0, 0, 0, 8), Child = search });
            }
            var any = false;
            void Add(JsonObject n, int depth)
            {
                if (!Matches(n)) return;
                any = true;
                var key = n["key"]?.ToString() ?? "";
                var lazy = n["lazy"] is JsonValue lv && lv.GetValueKind() == JsonValueKind.True && !(col?.Children.ContainsKey(key) ?? false);
                var expandable = Kids(n).Any() || lazy;
                var open = state.Expanded!.Contains(key) || query.Length > 0;
                var row = new DockPanel { Height = dense ? 28 : 32, Margin = new Thickness(depth * 16, 0, 0, 0), Background = Brushes.Transparent };
                var chevron = Ui.IconButton(open ? LucideIconKind.ChevronDown : LucideIconKind.ChevronRight, open ? "collapse" : "expand", () =>
                {
                    if (!state.Expanded.Remove(key)) state.Expanded.Add(key);
                    Fire(view, node, "onExpand", new JsonObject { ["key"] = key, ["expanded"] = state.Expanded.Contains(key) });
                    Redraw();
                });
                chevron.Opacity = expandable ? 1 : 0;
                chevron.IsHitTestVisible = expandable;
                DockPanel.SetDock(chevron, Dock.Left);
                row.Children.Add(chevron);
                if (selection == "checkbox")
                {
                    var box = new Checkbox { IsChecked = selected.Contains(key) ? true : Descendants(n).Any(selected.Contains) ? null : false, VerticalAlignment = VerticalAlignment.Center };
                    box.IsCheckedChanged += (_, _) =>
                    {
                        var all = new[] { key }.Concat(Descendants(n));
                        if (selected.Contains(key)) foreach (var k in all) selected.Remove(k);
                        else foreach (var k in all) selected.Add(k);
                        var keys = new JsonArray([.. selected.Select(k => (JsonNode?)JsonValue.Create(k))]);
                        if (S(p, "name") is { } name) view.Session.SetLocal(name, keys);
                        Fire(view, node, "onSelect", new JsonObject { ["keys"] = keys.DeepClone() });
                        Redraw();
                    };
                    DockPanel.SetDock(box, Dock.Left);
                    row.Children.Add(box);
                }
                if (n["icon"]?.ToString() is { } icon)
                {
                    var i = Icon(icon, 14);
                    i.Margin = new Thickness(0, 0, 6, 0);
                    DockPanel.SetDock(i, Dock.Left);
                    row.Children.Add(i);
                }
                if (actions.Count > 0)
                {
                    var menu = Ui.Menu("…", LucideIconKind.Ellipsis, actions.Select(a => new Ui.MenuItem(a["label"]?.ToString() ?? "", null,
                        () => view.Session.Send(a["action"]?.ToString() ?? "", new JsonObject { ["key"] = key }), a["tone"]?.ToString() == "danger")));
                    DockPanel.SetDock(menu, Dock.Right);
                    row.Children.Add(menu);
                }
                if (n["badge"]?.ToString() is { } badge)
                {
                    var b = KitBadge.Create(badge);
                    DockPanel.SetDock(b, Dock.Right);
                    row.Children.Add(b);
                }
                if (n["detail"]?.ToString() is { } detail)
                {
                    var d = Muted(detail);
                    d.Margin = new Thickness(8, 0);
                    DockPanel.SetDock(d, Dock.Right);
                    row.Children.Add(d);
                }
                var label = Small(n["label"]?.ToString() ?? key);
                label.TextWrapping = TextWrapping.NoWrap;
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                row.Children.Add(label);
                var surface = new Border { CornerRadius = new CornerRadius(6), Child = row, Cursor = new Cursor(StandardCursorType.Hand) };
                if (selected.Contains(key) && selection is "single" or "multiple") surface.Token(Border.BackgroundProperty, ArcDesignTokenKind.AccentSubtle);
                surface.PointerReleased += (_, _) =>
                {
                    if (selection == "single") selected = [key];
                    else if (selection == "multiple" && !selected.Remove(key)) selected.Add(key);
                    if (selection is "single" or "multiple")
                    {
                        var keys = new JsonArray([.. selected.Select(k => (JsonNode?)JsonValue.Create(k))]);
                        if (S(p, "name") is { } name) view.Session.SetLocal(name, keys);
                        Fire(view, node, "onSelect", new JsonObject { ["keys"] = keys.DeepClone(), ["value"] = key });
                    }
                    Fire(view, node, "onNodePress", new JsonObject { ["key"] = key });
                    Redraw();
                };
                column.Children.Add(surface);
                if (open && lazy) column.Children.Add(new Border { Margin = new Thickness((depth + 1) * 16 + 24, 2, 0, 2), Child = Muted("…") });
                if (open) foreach (var kid in Kids(n)) Add(kid, depth + 1);
            }
            foreach (var n in nodes.OfType<JsonObject>()) Add(n, 0);
            if (!any) column.Children.Add(p["empty"] is JsonObject empty ? view.Detached(empty) : Muted(Loc.T("pluginUi.empty")));
        }
        Redraw();
        return Tagged(column, node);
    }

    private sealed class TreeState
    {
        public HashSet<string>? Expanded { get; set; }
        public string Query { get; set; } = "";
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JsonObject, TreeState> TreeStates = new();

    private static Control LogOf(PluginViewHost view, JsonObject p)
    {
        var lines = Rows(view, p, "lines");
        var levels = (p["levels"] as JsonArray)?.Select(x => x?.ToString()).ToHashSet();
        var text = string.Join('\n', lines.Where(l => levels is null || l["level"] is null || levels.Contains(l["level"]?.ToString())).TakeLast(2000)
            .Select(l => (l["at"]?.ToString() is { Length: >= 19 } at ? at[11..19] + " " : "") + (l["text"]?.ToString() ?? l.ToJsonString())));
        var box = new SelectableTextBlock { Text = text, FontSize = 12, TextWrapping = B(p, "wrap") ? TextWrapping.Wrap : TextWrapping.NoWrap };
        box.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        var scroll = new ScrollViewer { Content = box, MaxHeight = Size(p["height"], 320), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (p["follow"] is not JsonValue f || f.GetValueKind() != JsonValueKind.False) Dispatcher.UIThread.Post(scroll.ScrollToEnd, DispatcherPriority.Background);
        var border = new Border { Padding = new Thickness(8), CornerRadius = new CornerRadius(8), Child = scroll };
        return border.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
    }

    private static Control MetricOf(PluginViewHost view, JsonObject p)
    {
        var column = new StackPanel { Spacing = 4 };
        column.Children.Add(Inline(6, Icon(S(p, "icon"), 13), KitStyle.Text(S(p, "label") ?? "", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary)));
        var value = KitStyle.Text(PluginUiProtocol.Format(S(p, "format"), p["value"], view.Locale), ArcDesignTokenKind.TypeXL, S(p, "tone") is { } t ? ToneToken(t) : ArcDesignTokenKind.Foreground, FontWeight.SemiBold);
        var row = new DockPanel();
        if (p["trend"] is JsonArray trend)
        {
            var spark = DrawnControl.Sparkline([.. trend.Select(Num)], S(p, "tone"), true);
            DockPanel.SetDock(spark, Dock.Right);
            row.Children.Add(spark);
        }
        row.Children.Add(value);
        column.Children.Add(row);
        if (!double.IsNaN(N(p, "delta")))
            column.Children.Add(Small((N(p, "delta") >= 0 ? "▲ " : "▼ ") + PluginUiProtocol.Format("percent", JsonValue.Create(Math.Abs(N(p, "delta"))), view.Locale),
                color: N(p, "delta") >= 0 ? ArcDesignTokenKind.Success : ArcDesignTokenKind.Danger));
        return Surface(column);
    }

    private static Control ChartOf(PluginViewHost view, JsonObject node, JsonObject p)
    {
        var rows = Rows(view, p, "data");
        if (!double.IsNaN(N(p, "window"))) rows = rows.TakeLast((int)N(p, "window")).ToList();
        if (B(p, "loading")) return Ui.Spinner();
        if (rows.Count == 0) return p["empty"] is JsonObject empty ? view.Detached(empty) : Muted(Loc.T("pluginUi.empty"));
        var spec = (JsonObject)p.DeepClone();
        spec["height"] = Size(p["height"], 240);
        var chart = new ChartControl(spec, rows, view.Locale,
            Has(p, "onSelect") ? (datum, series) => Fire(view, node, "onSelect", new JsonObject { ["datum"] = datum, ["series"] = series }) : null,
            Has(p, "onRange") ? (from, to) => Fire(view, node, "onRange", new JsonObject { ["from"] = from, ["to"] = to }) : null);
        var series = (p["series"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (S(p, "legend") == "none" || series.Count < 2 || S(p, "kind") == "donut") return chart;
        var legend = new WrapPanel { ItemSpacing = 12 };
        for (var i = 0; i < series.Count; i++)
        {
            var s = series[i];
            var key = s["key"]?.ToString() ?? "";
            var swatch = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = UiPaint.Series(s, i, chart) };
            var entry = new Avalonia.Controls.Button { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Content = Inline(6, swatch, Small(s["label"]?.ToString() ?? key)) };
            entry.Click += (_, _) =>
            {
                chart.Toggle(key);
                entry.Opacity = entry.Opacity < 1 ? 1 : 0.4;
            };
            legend.Children.Add(entry);
        }
        var column = new StackPanel { Spacing = 8 };
        if (S(p, "legend") == "top") column.Children.Add(legend);
        column.Children.Add(chart);
        if (S(p, "legend") != "top") column.Children.Add(legend);
        return column;
    }

    private static Control HeatmapOf(JsonObject p)
    {
        var days = (p["days"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var max = Math.Max(1, days.Select(d => Num(d["value"])).Where(v => !double.IsNaN(v)).DefaultIfEmpty(1).Max());
        var grid = new UniformGrid { Rows = 7 };
        foreach (var d in days)
        {
            var v = Num(d["value"]);
            var cell = new Border { Width = 10, Height = 10, Margin = new Thickness(1), CornerRadius = new CornerRadius(2), Opacity = double.IsNaN(v) || v == 0 ? 1 : 0.25 + 0.75 * v / max };
            cell.Token(Border.BackgroundProperty, double.IsNaN(v) || v == 0 ? ArcDesignTokenKind.SurfaceMuted : ToneToken(S(p, "tone") ?? "accent"));
            ToolTip.SetTip(cell, $"{d["date"]}: {d["value"]}");
            grid.Children.Add(cell);
        }
        return grid;
    }

    private static Control PopoverOf(PluginViewHost view, NodeHost host, JsonObject p)
    {
        var body = Column(view, host, 8);
        var flyout = new Flyout { Content = new Border { Padding = new Thickness(12), Child = body } };
        var trigger = p["trigger"] is JsonObject t ? view.Detached(t) : Ui.IconButton(LucideIconKind.Ellipsis, "more", () => { });
        FlyoutBase.SetAttachedFlyout(trigger, flyout);
        trigger.PointerReleased += (_, _) => FlyoutBase.ShowAttachedFlyout(trigger);
        return trigger;
    }

    // ---------------------------------------------------------------- domain pickers (read the admin API the console already uses)

    private static Control DomainPickerOf(PluginViewHost view, JsonObject node, JsonObject p, string type)
    {
        var host = new ContentControl { Content = Ui.Spinner() };
        _ = FillAsync();
        return Tagged(host, node);

        async Task FillAsync()
        {
            List<(string Value, string Label, string? Description)> options = [];
            try
            {
                var api = view.Api.Client;
                switch (type)
                {
                    case "ProtocolPicker":
                        options = [("anthropic", "Anthropic", null), ("chat", "OpenAI Chat", null), ("responses", "OpenAI Responses", null), ("gemini", "Gemini", null)];
                        break;
                    case "ModelPicker":
                        options = (await api.GetAsync<JsonArray>("/api/models") ?? []).OfType<JsonObject>().Select(m => (m["id"]?.ToString() ?? "", m["displayName"]?.ToString() ?? m["id"]?.ToString() ?? "", (string?)m["vendor"]?.ToString())).ToList();
                        break;
                    case "ProviderPicker":
                        options = (await api.GetAsync<JsonArray>("/api/providers") ?? []).OfType<JsonObject>().Select(m => (m["id"]?.ToString() ?? "", m["name"]?.ToString() ?? "", (string?)null)).ToList();
                        break;
                    case "AccountPicker" when S(p, "providerId") is { } pid:
                        options = (await api.GetAsync<JsonArray>($"/api/providers/{Uri.EscapeDataString(pid)}/accounts") ?? []).OfType<JsonObject>().Select(m => (m["id"]?.ToString() ?? "", m["displayName"]?.ToString() ?? "", (string?)m["accountEmail"]?.ToString())).ToList();
                        break;
                    case "ClientPicker":
                        options = (await api.GetAsync<JsonArray>("/api/clients") ?? []).OfType<JsonObject>().Select(m => (m["kind"]?.ToString() ?? "", m["name"]?.ToString() ?? "", (string?)null)).ToList();
                        break;
                    case "TokenPicker":
                        options = (await api.GetAsync<JsonArray>("/api/tokens") ?? []).OfType<JsonObject>().Select(m => (m["id"]?.ToString() ?? "", m["name"]?.ToString() ?? "", (string?)m["keyMasked"]?.ToString())).ToList();
                        break;
                }
            }
            catch (Exception e) when (e is HttpRequestException or Core.Api.ApiException or TaskCanceledException or JsonException)
            {
            }
            host.Content = B(p, "multiple") ? ChipsOf(view, node, p, options, true) : SelectOf(view, node, p, options);
        }
    }
}
