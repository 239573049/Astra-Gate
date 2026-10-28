using System.Text.Json.Nodes;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Pricing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Components.Pricing;

/// <summary>Event data of <see cref="PricingEditor.ValueChanged"/>.</summary>
public sealed class PricingChangedEventArgs(JsonNode? value) : EventArgs
{
    /// <summary>The new pricing JSON (storage format, snake_case); null when the price was removed. A copy: the receiver may keep it.</summary>
    public JsonNode? Value { get; } = value;
}

/// <summary>
/// The structured form and the raw JSON editor of a pricing schedule (web <c>PricingEditor</c>, web/src/components/PricingEditor.tsx):
/// ten base token prices and a per-request price, context tiers, time windows, service tiers and notes, with every rule added through
/// <see cref="PricingRuleDialog"/>. Reusable: the model detail page embeds it for the system price and for a provider price, and the
/// provider detail page embeds it for model overrides.
/// <para><b>API.</b> The value is the pricing JSON the admin API carries (<c>ModelDto.Pricing</c>, <c>ModelPriceDto.Pricing</c>,
/// <c>ModelPriceInput.Pricing</c>): a <see cref="JsonNode"/> in storage format, <c>null</c> for "no price".</para>
/// <list type="bullet">
/// <item><see cref="Value"/> (two-way styled property): bind or set the pricing in; read it for the edited pricing. Setting it to the
/// value the editor itself just produced does not rebuild the form, so a parent may write the echo back without losing the caret.</item>
/// <item><see cref="ValueChanged"/>: raised after every edit with a copy of the new JSON (<see cref="PricingChangedEventArgs.Value"/>), null when removed.</item>
/// <item><see cref="AllowEmpty"/>: shows "remove price" and lets a null value be edited back into existence through the "add pricing" dialog
/// (the model's system price); without it a null value is shown as an empty schedule.</item>
/// <item><see cref="IsReadOnly"/>: the value is shown (form or JSON) but nothing can be edited, added or removed.</item>
/// <item><see cref="Document"/>: the current value as a <see cref="PricingDocument"/> (null when none), for callers that want the typed accessors.</item>
/// </list>
/// Prices are <see cref="decimal"/>s written as canonical JSON numbers (<see cref="PriceNumber"/>); the editor never rounds through a float.
/// </summary>
public sealed class PricingEditor : UserControl
{
    /// <summary>The pricing JSON being edited (two-way); null is "no price".</summary>
    public static readonly StyledProperty<JsonNode?> ValueProperty =
        AvaloniaProperty.Register<PricingEditor, JsonNode?>(nameof(Value), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Whether the price may be removed and, when absent, added through a dialog (web <c>allowEmpty</c>).</summary>
    public static readonly StyledProperty<bool> AllowEmptyProperty = AvaloniaProperty.Register<PricingEditor, bool>(nameof(AllowEmpty));

    /// <summary>Show the value without letting it be edited.</summary>
    public static readonly StyledProperty<bool> IsReadOnlyProperty = AvaloniaProperty.Register<PricingEditor, bool>(nameof(IsReadOnly));

    private PricingDocument? _doc;
    private bool _json;
    private string _jsonText = "";
    private string? _jsonError;
    private bool _emitting;
    private readonly StackPanel _root = new() { Spacing = 16 };
    private readonly PricingRuleDialog _ruleDialog = new();
    private readonly WideSheet _newSheet = new() { SheetWidth = 660 };
    private PricingEditor? _newEditor;
    private PricingDocument? _newDoc;

    /// <inheritdoc cref="ValueProperty"/>
    public JsonNode? Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <inheritdoc cref="AllowEmptyProperty"/>
    public bool AllowEmpty { get => GetValue(AllowEmptyProperty); set => SetValue(AllowEmptyProperty, value); }

    /// <inheritdoc cref="IsReadOnlyProperty"/>
    public bool IsReadOnly { get => GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }

    /// <summary>The current pricing as a document; null while there is none.</summary>
    public PricingDocument? Document => _doc;

    /// <summary>Raised after every edit (typing in a box, adding or removing a rule, editing the JSON, removing the price).</summary>
    public event EventHandler<PricingChangedEventArgs>? ValueChanged;

    /// <summary>An editor with no price (set <see cref="Value"/>).</summary>
    public PricingEditor()
    {
        Content = _root;
        _ruleDialog.RuleAdded += (_, draft) =>
        {
            if (_doc is null) return;
            if (draft.Apply(_doc) is { } next) Commit(next, structural: true);
        };
    }

    /// <summary>An editor showing <paramref name="value"/>.</summary>
    public PricingEditor(JsonNode? value, bool allowEmpty = false) : this()
    {
        AllowEmpty = allowEmpty;
        Value = value;
    }

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

    private void OnLanguage() => Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty)
        {
            if (_emitting) return;
            var incoming = PricingDocument.FromNode(Value);
            // the parent wrote back what we produced: nothing to rebuild (the focused box keeps its caret)
            if (_doc is not null && incoming is not null && PricingDocument.Same(_doc.ToNode(), incoming.ToNode())) return;
            _doc = incoming;
            _jsonError = null;
            if (_json) _jsonText = _doc?.Pretty() ?? "";
            Rebuild();
        }
        else if (change.Property == AllowEmptyProperty || change.Property == IsReadOnlyProperty) Rebuild();
    }

    /// <summary>
    /// Opens the "add rule" dialog for <paramref name="kind"/> as if its Add button was pressed (for hosts that offer the action
    /// elsewhere, and for the visual harness). Does nothing while read-only or while the editor shows the "no price" panel.
    /// </summary>
    public void BeginAddRule(PricingRuleKind kind)
    {
        if (IsReadOnly || _doc is null && AllowEmpty && !_json) return;
        _ruleDialog.Show(kind, Current.ServiceTierNames);
    }

    // ---------- committing edits ----------

    /// <summary>Takes <paramref name="next"/> as the value and reports it; <paramref name="structural"/> rebuilds the rows (a rule was added or removed).</summary>
    private void Commit(PricingDocument? next, bool structural)
    {
        // A text box reports its initial text when its template is applied, and a re-typed identical value is no edit: only a real
        // change is committed (compared after compaction, which every edit does).
        if (next is not null && _doc is not null && !structural && PricingDocument.Same(next.ToNode(), _doc.Compacted().ToNode())) return;
        _doc = next;
        Emit(next?.ToNode());
        if (structural) Rebuild();
    }

    private void Emit(JsonNode? node)
    {
        _emitting = true;
        try
        {
            SetCurrentValue(ValueProperty, node);
        }
        finally
        {
            _emitting = false;
        }
        ValueChanged?.Invoke(this, new PricingChangedEventArgs(node?.DeepClone()));
    }

    private PricingDocument Current => _doc ?? PricingDocument.Empty();

    // ---------- building ----------

    private void Rebuild()
    {
        _root.Children.Clear();
        if (_doc is null && !_json && AllowEmpty)
        {
            BuildNone();
            return;
        }
        // Without AllowEmpty a null value is edited as the empty schedule (web: `value ?? emptySchedule()`), starting from the
        // first edit; with AllowEmpty and JSON mode the text box is just empty.
        _root.Children.Add(BuildHeaderRow());
        if (_json) _root.Children.Add(BuildJson());
        else BuildForm();
        _root.Children.Add(_ruleDialog);
    }

    private void BuildNone()
    {
        var text = PricingUi.Text(Loc.T("pricing.none"), ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.TextSecondary);
        text.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        grid.Children.Add(text);
        var add = PricingUi.Button(Loc.T("pricing.add"), PricingUi.Look.Primary, LucideIconKind.Plus, small: true, click: OpenNewSheet);
        add.IsVisible = !IsReadOnly;
        Grid.SetColumn(add, 1);
        grid.Children.Add(add);
        _root.Children.Add(PricingUi.Panel(grid, new Thickness(16, 12)));
        _root.Children.Add(_newSheet);
    }

    private void OpenNewSheet()
    {
        _newDoc = PricingDocument.Empty();
        _newEditor = new PricingEditor(_newDoc.ToNode());
        _newEditor.ValueChanged += (_, e) =>
        {
            if (e.Value is not null) _newDoc = PricingDocument.FromNode(e.Value);
        };
        _newSheet.Title = Loc.T("pricing.add");
        _newSheet.Content = _newEditor;
        _newSheet.Footer =
        [
            PricingUi.Button(Loc.T("common.cancel"), click: () => _newSheet.IsOpen = false),
            PricingUi.Button(Loc.T("common.add"), PricingUi.Look.Primary, click: () =>
            {
                var chosen = _newDoc;
                _newSheet.IsOpen = false;
                if (chosen is not null) Commit(chosen, structural: true);
            }),
        ];
        _newSheet.IsOpen = true;
    }

    private Control BuildHeaderRow()
    {
        var segmented = new SegmentedControl
        {
            ItemsSource = new[] { new SegmentedOption("form", Loc.T("pricing.mode.form")), new SegmentedOption("json", "JSON") },
            SelectedValue = _json ? "json" : "form",
            VerticalAlignment = VerticalAlignment.Center,
        };
        Avalonia.Automation.AutomationProperties.SetName(segmented, Loc.T("pricing.editMode"));
        segmented.SelectionChanged += (_, _) =>
        {
            var json = segmented.SelectedValue as string == "json";
            if (json == _json) return;
            _json = json;
            if (json) _jsonText = _doc?.Pretty() ?? "";
            _jsonError = null;
            Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
        };

        var row = new DockPanel { LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(segmented);
        left.Children.Add(PricingUi.Muted12(Loc.T("pricing.unit")));
        ((TextBlock)left.Children[1]).VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(left);
        if (AllowEmpty && !IsReadOnly)
        {
            var remove = PricingUi.Button(Loc.T("pricing.remove"), PricingUi.Look.Destructive, small: true, click: () => Commit(null, structural: true));
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
        }
        return row;
    }

    private Control BuildJson()
    {
        var area = new Textarea
        {
            Label = "JSON",
            IsLabelVisible = true,
            Text = _jsonText,
            FrameHeight = 340, // rows=16
            IsReadOnly = IsReadOnly,
            ErrorMessage = _jsonError,
            IsResizable = false,
            AcceptsReturn = true,
        };
        PricingUi.Mono(area);
        area.TextChanged += (_, _) =>
        {
            // the box reports its initial text once its template is applied: only typing is an edit
            if ((area.Text ?? "") == _jsonText) return;
            _jsonText = area.Text ?? "";
            var (value, error) = PricingDocument.Parse(_jsonText);
            _jsonError = error;
            area.ErrorMessage = error;
            if (error is null && value is not null)
            {
                _doc = value;
                Emit(value.ToNode());
            }
        };
        return area;
    }

    private void BuildForm()
    {
        _root.Children.Add(BuildBase());
        _root.Children.Add(BuildContextTiers());
        _root.Children.Add(BuildTimeWindows());
        _root.Children.Add(BuildServiceTiers());
        var notes = new Textarea
        {
            Label = Loc.T("pricing.notes"),
            IsLabelVisible = true,
            Text = Current.Notes,
            FrameHeight = 72, // rows=2
            IsReadOnly = IsReadOnly,
            IsResizable = false,
        };
        notes.TextChanged += (_, _) => Commit(Current.WithNotes(notes.Text), structural: false);
        _root.Children.Add(notes);
    }

    private static Control SectionLabel(string text, Control? action)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        grid.Children.Add(PricingUi.Text(text, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, Avalonia.Media.FontWeight.Medium));
        if (action is not null)
        {
            Grid.SetColumn(action, 1);
            grid.Children.Add(action);
        }
        return grid;
    }

    private Control? AddButton(string key, PricingRuleKind kind)
        => IsReadOnly ? null : PricingUi.Button(Loc.T(key), PricingUi.Look.Plain, LucideIconKind.Plus, small: true,
            click: () => _ruleDialog.Show(kind, Current.ServiceTierNames));

    private Control RemoveButton(Action remove)
    {
        var b = PricingUi.Button(null, PricingUi.Look.Plain, LucideIconKind.Trash2, small: true, click: remove, name: Loc.T("common.delete"));
        b.IsVisible = !IsReadOnly;
        return b;
    }

    // ---------- base prices ----------

    private Control BuildBase()
    {
        var section = new StackPanel();
        section.Children.Add(SectionLabel(Loc.T("pricing.base"), null));
        var grid = new AutoFillGrid { MinColumnWidth = 200, ColumnSpacing = 20, RowSpacing = 8 };
        foreach (var type in PricingDocument.TokenTypes)
        {
            var box = PricingUi.Number(Current.BasePrice(type), v => Commit(Current.WithBasePrice(type, v), structural: false), width: 112, name: type);
            box.IsReadOnly = IsReadOnly;
            grid.Children.Add(TypedCell(type, box));
        }
        var perRequest = PricingUi.Number(Current.PerRequest, v => Commit(Current.WithPerRequest(v), structural: false), width: 112, name: Loc.T("pricing.perRequest"));
        perRequest.IsReadOnly = IsReadOnly;
        var cell = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        cell.Children.Add(PricingUi.Text(Loc.T("pricing.perRequest"), ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.TextSecondary));
        ((TextBlock)cell.Children[0]).FontSize = 12;
        Grid.SetColumn(perRequest, 1);
        cell.Children.Add(perRequest);
        grid.Children.Add(cell);
        section.Children.Add(grid);
        return section;
    }

    /// <summary>A mono token-type name on the left, the box on the right (web <c>flex justify-between gap-2</c>).</summary>
    private static Control TypedCell(string type, Control box)
    {
        var cell = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        cell.Children.Add(PricingUi.Text(type, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, mono: true));
        Grid.SetColumn(box, 1);
        cell.Children.Add(box);
        return cell;
    }

    // ---------- context tiers ----------

    private Control BuildContextTiers()
    {
        var section = new StackPanel();
        section.Children.Add(SectionLabel(Loc.T("pricing.contextTiers"), AddButton("pricing.addTier", PricingRuleKind.Context)));
        var count = Current.Count(PricingDocument.ContextTiersKey);
        if (count == 0) section.Children.Add(Hint(Loc.T("pricing.contextTiers.hint")));
        var list = new StackPanel { Spacing = 8 };
        for (var i = 0; i < count; i++) list.Children.Add(ContextTierCard(i));
        section.Children.Add(list);
        return section;
    }

    private Control ContextTierCard(int i)
    {
        const string section = PricingDocument.ContextTiersKey;
        var rule = Current.Rule(section, i);
        var head = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var name = PricingUi.TextBox(rule.Text("name"), v => Commit(Current.WithRule(section, i, r => r["name"] = v), false), width: 112, name: Loc.T("pricing.name"), placeholder: Loc.T("pricing.name"));
        name.IsReadOnly = IsReadOnly;
        head.Children.Add(name);
        head.Children.Add(PricingUi.Secondary12(Loc.T("pricing.threshold")));
        ((TextBlock)head.Children[^1]).VerticalAlignment = VerticalAlignment.Center;
        var threshold = PricingUi.Number(rule.Whole("threshold_input_tokens"), v => Commit(Current.WithRule(section, i, r => r["threshold_input_tokens"] = JsonValue.Create((long)(v ?? 0))), false), width: 112, name: Loc.T("pricing.threshold"), integer: true);
        threshold.IsReadOnly = IsReadOnly;
        head.Children.Add(threshold);
        var mode = PricingUi.Choice(
            [new SelectOption("whole", Loc.T("pricing.mode.whole")), new SelectOption("progressive", Loc.T("pricing.mode.progressive"))],
            rule.Text("mode"), v => Commit(Current.WithRule(section, i, r => r["mode"] = v), false), width: 144, name: Loc.T("pricing.tierMode"));
        mode.IsEnabled = !IsReadOnly;
        head.Children.Add(mode);
        var policy = PricingUi.Choice(
            [new SelectOption("highest", Loc.T("pricing.output.highest")), new SelectOption("base", Loc.T("pricing.output.base"))],
            rule.Text("output_policy"), v => Commit(Current.WithRule(section, i, r => r["output_policy"] = v), false), width: 160, name: Loc.T("pricing.outputPolicy"));
        policy.IsEnabled = !IsReadOnly;
        head.Children.Add(policy);

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        top.Children.Add(head);
        var remove = RemoveButton(() => Commit(Current.WithoutRule(section, i), structural: true));
        Grid.SetColumn(remove, 1);
        top.Children.Add(remove);

        var prices = new WrapPanel { ItemSpacing = 16, LineSpacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var type in new[] { "input", "cache_read", "output" })
        {
            var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            cell.Children.Add(PricingUi.Text(type, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, mono: true));
            var box = PricingUi.Number(rule.Price(type), v => Commit(Current.WithRule(section, i, r => SetRulePrice(r, type, v)), false), width: 96, name: type);
            box.IsReadOnly = IsReadOnly;
            cell.Children.Add(box);
            prices.Children.Add(cell);
        }
        return PricingUi.Panel(new StackPanel { Children = { top, prices } }, new Thickness(12));
    }

    /// <summary>Sets one token price inside a rule's <c>prices</c> object (a cleared box stays an explicit null, like the web).</summary>
    private static void SetRulePrice(JsonObject rule, string type, decimal? price)
    {
        if (rule["prices"] is not JsonObject prices) rule["prices"] = prices = new JsonObject();
        prices[type] = PriceNumber.ToNode(price);
    }

    // ---------- time windows ----------

    private static readonly string[] DaysZh = ["一", "二", "三", "四", "五", "六", "日"];
    private static readonly string[] DaysEn = ["M", "T", "W", "T", "F", "S", "S"];

    private Control BuildTimeWindows()
    {
        var section = new StackPanel();
        section.Children.Add(SectionLabel(Loc.T("pricing.timeWindows"), AddButton("pricing.addWindow", PricingRuleKind.Time)));
        var count = Current.Count(PricingDocument.TimeWindowsKey);
        if (count == 0) section.Children.Add(Hint(Loc.T("pricing.timeWindows.hint")));
        var list = new StackPanel { Spacing = 8 };
        for (var i = 0; i < count; i++) list.Children.Add(TimeWindowCard(i));
        section.Children.Add(list);
        return section;
    }

    private Control TimeWindowCard(int i)
    {
        const string section = PricingDocument.TimeWindowsKey;
        var rule = Current.Rule(section, i);
        var head = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        var name = PricingUi.TextBox(rule.Text("name"), v => Commit(Current.WithRule(section, i, r => r["name"] = v), false), width: 96, name: Loc.T("pricing.name"), placeholder: Loc.T("pricing.name"));
        var zone = PricingUi.TextBox(rule.Text("timezone"), v => Commit(Current.WithRule(section, i, r => r["timezone"] = v), false), width: 160, mono: true, name: Loc.T("pricing.timezone"), placeholder: "Asia/Shanghai");
        var start = PricingUi.TextBox(rule.Text("start"), v => Commit(Current.WithRule(section, i, r => r["start"] = v), false), width: 110, mono: true, name: Loc.T("pricing.start"));
        var end = PricingUi.TextBox(rule.Text("end"), v => Commit(Current.WithRule(section, i, r => r["end"] = v), false), width: 110, mono: true, name: Loc.T("pricing.end"));
        var mult = PricingUi.Number(rule.Number("multiplier"), v => Commit(Current.WithRule(section, i, r => r["multiplier"] = PriceNumber.ToNode(v)), false), width: 80, name: Loc.T("pricing.multiplier"));
        foreach (var box in new Avalonia.Controls.TextBox[] { name, zone, start, end, mult }) box.IsReadOnly = IsReadOnly;
        // web: <input type="time"> can only hold HH:mm; a typed field shows when it does not
        foreach (var clock in new[] { start, end })
        {
            void Check() => clock.IsInvalid = !PricingRuleDraft.IsClockTime(clock.Text ?? "");
            Check();
            clock.TextChanged += (_, _) => Check();
        }
        head.Children.Add(name);
        head.Children.Add(zone);
        head.Children.Add(start);
        head.Children.Add(Dim("–"));
        head.Children.Add(end);
        head.Children.Add(PricingUi.Secondary12("×"));
        ((TextBlock)head.Children[^1]).VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(mult);

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        top.Children.Add(head);
        var remove = RemoveButton(() => Commit(Current.WithoutRule(section, i), structural: true));
        Grid.SetColumn(remove, 1);
        top.Children.Add(remove);

        var days = DayChipGroup.Create(Loc.T("pricing.days"), Loc.Language == "zh" ? DaysZh : DaysEn, rule.Days(), chosen =>
            Commit(Current.WithRule(section, i, r => r["days"] = chosen.Count == 7 ? null : new JsonArray(chosen.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray())), false),
            enabled: !IsReadOnly);
        var exclude = PricingUi.TextBox(string.Join(", ", rule.ExcludeDates()), v =>
        {
            var list = PricingRuleDraft.SplitDates(v);
            Commit(Current.WithRule(section, i, r => r["exclude_dates"] = list.Count > 0 ? new JsonArray(list.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray()) : null), false);
        }, width: 220, mono: true, name: Loc.T("pricing.excludeDates"), placeholder: Loc.T("pricing.excludeDates"));
        exclude.IsReadOnly = IsReadOnly;
        var second = new WrapPanel { ItemSpacing = 8, LineSpacing = 4, Margin = new Thickness(0, 8, 0, 0) };
        second.Children.Add(days);
        second.Children.Add(exclude);
        return PricingUi.Panel(new StackPanel { Children = { top, second } }, new Thickness(12));
    }

    // ---------- service tiers ----------

    private Control BuildServiceTiers()
    {
        var section = new StackPanel();
        section.Children.Add(SectionLabel(Loc.T("pricing.serviceTiers"), AddButton("pricing.addServiceTier", PricingRuleKind.Service)));
        var tiers = Current.ServiceTiers;
        if (tiers.Count == 0) section.Children.Add(Hint(Loc.T("pricing.serviceTiers.hint")));
        var list = new StackPanel { Spacing = 6 };
        for (var i = 0; i < tiers.Count; i++)
        {
            var index = i;
            var tier = tiers[i];
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            // a renamed tier is a different dictionary key: rebuild on commit of the name so rows stay in step with the document
            var name = PricingUi.TextBox(tier.Name, v =>
            {
                var before = Current.Count(PricingDocument.ServiceTiersKey);
                var renamed = Current.WithServiceTierRenamed(index, v);
                // renaming onto another tier's name merges the two: the rows must follow
                Commit(renamed, structural: renamed.Count(PricingDocument.ServiceTiersKey) != before);
            }, width: 144, mono: true, name: Loc.T("pricing.name"));
            name.IsReadOnly = IsReadOnly;
            row.Children.Add(name);
            row.Children.Add(PricingUi.Secondary12("×"));
            ((TextBlock)row.Children[^1]).VerticalAlignment = VerticalAlignment.Center;
            var mult = PricingUi.Number(tier.Multiplier, v => Commit(Current.WithServiceTierMultiplier(index, v), false), width: 96, name: Loc.T("pricing.multiplier"));
            mult.IsReadOnly = IsReadOnly;
            row.Children.Add(mult);
            if (tier.HasOwnPrices) row.Children.Add(PricingUi.Muted12(Loc.T("pricing.hasOwnPrices")));
            row.Children.Add(RemoveButton(() => Commit(Current.WithoutServiceTier(index), structural: true)));
            list.Children.Add(row);
        }
        section.Children.Add(list);
        return section;
    }

    private static Control Hint(string text)
    {
        var block = PricingUi.Muted12(text);
        block.Margin = new Thickness(0, 0, 0, 8);
        return block;
    }

    private static TextBlock Dim(string text)
    {
        var block = PricingUi.Text(text, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.TextMuted);
        block.VerticalAlignment = VerticalAlignment.Center;
        return block;
    }
}
