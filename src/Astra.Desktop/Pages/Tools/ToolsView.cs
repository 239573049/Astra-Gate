using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Clients;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.ProviderDetail;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Components.Tokens;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Settings;
using Astra.Desktop.Core.Tools;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;

namespace Astra.Desktop.Pages.Tools;

/// <summary>
/// The tools page's body (web <c>ToolsSettings</c>): search on behalf of models, the search APIs with their keys (switch, order by arrows,
/// pick policy, test), web fetch limits, the image model and the tool list. Each block redraws from the one settings query; what is typed
/// (a key, an address, the image model) is kept across redraws. Reordering is by the arrows only: the web page also drags rows.
/// </summary>
public sealed class ToolsView : UserControl
{
    private readonly ToolsPage _page;
    private readonly QueryHost<ToolsSettingsDto> _settings;
    private readonly QueryHost<List<ToolInfoDto>> _tools;
    private readonly Dictionary<string, string> _keyDrafts = [];
    private readonly Dictionary<string, string> _urlDrafts = [];
    private string? _newAddress;
    private string? _addingVendor;
    private string? _imageModel;
    private string? _imageSize;
    private bool _wired;

    /// <summary>Builds the body for <paramref name="page"/>.</summary>
    public ToolsView(ToolsPage page)
    {
        _page = page;
        _settings = new QueryHost<ToolsSettingsDto>(page.Api.Settings, Render);
        _tools = new QueryHost<List<ToolInfoDto>>(page.Api.Tools, RenderTools);
        Content = new StackPanel { Spacing = 20, MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Stretch, Children = { _settings, _tools } };
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_wired) return;
        _wired = true;
        Loc.LanguageChanged += OnLanguage;
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (!_wired) return;
        _wired = false;
        Loc.LanguageChanged -= OnLanguage;
    }

    private void OnLanguage() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        _settings.Rebuild();
        _tools.Rebuild();
    });

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ApiException e)
        {
            _page.Feedback.Error(Loc.T("common.error"), e.Message);
        }
    }

    private void Save(JsonObject body) => _ = GuardAsync(async () =>
    {
        await _page.Api.UpdateAsync(body);
        _page.Feedback.Success(Loc.T("common.saved"));
    });

    // ---------- settings ----------

    private Control? Render(Query<ToolsSettingsDto> q)
    {
        if (q.Data is not { } s)
            return q.IsError ? ClientUi.Alert(AlertTone.Danger, Loc.T("common.error"), q.Error?.Message ?? "") : new LoadingState(3);
        _imageModel ??= s.Image.Model;
        _imageSize ??= s.Image.Size;
        var column = new StackPanel { Spacing = 20 };
        column.Children.Add(OnBehalf(s.Search));
        column.Children.Add(SearchApis(s.Search));
        column.Children.Add(Fetch(s.Fetch));
        column.Children.Add(Image(s.Image));
        return column;
    }

    private Control OnBehalf(WebSearchSettingsDto s)
    {
        var enable = PdUi.Toggle(s.OnBehalf, on => Save(new JsonObject { ["search"] = JsonPatch.Of(("onBehalf", on)) }), Loc.T("tools.onBehalf.enable"));
        var mode = PdUi.Choice([("auto", Loc.T("tools.onBehalf.mode.auto")), ("always", Loc.T("tools.onBehalf.mode.always"))], s.Mode,
            v => Save(new JsonObject { ["search"] = JsonPatch.Of(("mode", v)) }), 140, Loc.T("tools.onBehalf.mode"));
        var count = PdUi.Number(s.ResultCount, v => { if (v is { } n) Save(new JsonObject { ["search"] = JsonPatch.Of(("resultCount", (int)n)) }); }, 100, Loc.T("tools.onBehalf.count"), integer: true, minimum: 1);
        return RowBuilder.Group(Loc.T("tools.onBehalf.title"), Loc.T("tools.onBehalf.footer"),
            RowBuilder.Hinted(Loc.T("tools.onBehalf.enable"), Loc.T("tools.onBehalf.enableHint"), enable),
            RowBuilder.Hinted(Loc.T("tools.onBehalf.mode"), Loc.T("tools.onBehalf.modeHint"), mode),
            RowBuilder.Hinted(Loc.T("tools.onBehalf.count"), Loc.T("tools.onBehalf.countHint"), count));
    }

    // ---------- search APIs ----------

    private Control SearchApis(WebSearchSettingsDto s)
    {
        var title = PdUi.Text(Loc.T("tools.search.title"), 12, ArcDesignTokenKind.TextSecondary, Avalonia.Media.FontWeight.Medium);
        title.Margin = new Thickness(4, 0, 0, 0);
        var add = Ui.Menu(Loc.T("tools.search.add"), LucideIconKind.Plus, s.Available.Select(v =>
            new Ui.MenuItem(v.Plugin ? $"{v.Name} ({Loc.T("tools.search.fromPlugin")})" : v.Name, null, () =>
            {
                if (v.NeedsUrl)
                {
                    _addingVendor = v.Vendor;
                    _settings.Rebuild();
                }
                else _ = GuardAsync(() => _page.Api.AddApiAsync(v.Vendor, null));
            })));
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(title);
        Grid.SetColumn(add, 1);
        header.Children.Add(add);

        var column = new StackPanel { Spacing = 8, Children = { header } };
        if (_addingVendor is { } adding && s.Available.Any(v => v.Vendor == adding))
        {
            var address = PdUi.TextBox(_newAddress, v => _newAddress = v, 320, mono: true, name: Loc.T("tools.search.url"), placeholder: "https://searx.example.com");
            column.Children.Add(PdUi.Card(PdUi.Inline(8, address,
                Ui.Button(Loc.T("tools.search.add"), () => _ = GuardAsync(async () =>
                {
                    await _page.Api.AddApiAsync(adding, _newAddress);
                    _addingVendor = null;
                    _newAddress = null;
                }), ButtonVariant.Primary),
                Ui.Button(Loc.T("common.cancel"), () => { _addingVendor = null; _settings.Rebuild(); }, ButtonVariant.Ghost))));
        }
        column.Children.Add(PdUi.Hint(Loc.T("tools.search.hint")));
        if (s.Apis.Count == 0)
            column.Children.Add(PdUi.Card(PdUi.Pad(PdUi.Hint(Loc.T("tools.search.empty")))));
        else
        {
            var ids = s.Apis.Select(a => a.Vendor).ToList();
            column.Children.Add(PdUi.Card(PdUi.Divided(s.Apis.Select(a => ApiBlock(a, ids)))));
        }
        return column;
    }

    private Control ApiBlock(SearchApiDto api, IReadOnlyList<string> ids)
    {
        var name = ClientUi.HRow(8, PdUi.Text(api.Name, 13, ArcDesignTokenKind.Foreground, Avalonia.Media.FontWeight.SemiBold));
        if (api.Plugin) name.Children.Add(KitBadge.Create(Loc.T("tools.search.fromPlugin")));
        if (!api.Available) name.Children.Add(KitBadge.Create(Loc.T("tools.search.pluginOff"), Tone.Orange));
        var keys = api.KeyOptional ? Loc.T("tools.search.keyOptional") : Loc.T("tools.search.keysCount", ("count", api.Keys.Count));

        var test = Ui.Button(Loc.T("tools.search.test"), () => { }, ButtonVariant.Secondary, LucideIconKind.Zap);
        test.Click += async (_, _) =>
        {
            test.IsLoading = true;
            try
            {
                await GuardAsync(async () =>
                {
                    var r = await _page.Api.TestAsync(api.Vendor);
                    if (r.Ok) _page.Feedback.Success(Loc.T("tools.search.testOk", ("source", r.Source ?? api.Name), ("pages", r.Pages), ("ms", r.ElapsedMs)));
                    else _page.Feedback.Error(Loc.T("tools.search.testFailed", ("message", r.Error ?? "")));
                });
            }
            finally
            {
                test.IsLoading = false;
            }
        };
        var actions = ClientUi.HRow(4,
            Ui.IconButton(LucideIconKind.ArrowUp, Loc.T("tools.search.moveUp"), () => Reorder(ids, api.Vendor, -1), enabled: api.Vendor != ids[0]),
            Ui.IconButton(LucideIconKind.ArrowDown, Loc.T("tools.search.moveDown"), () => Reorder(ids, api.Vendor, 1), enabled: api.Vendor != ids[^1]),
            test,
            PdUi.Toggle(api.Enabled, on => _ = GuardAsync(() => _page.Api.UpdateApiAsync(api.Vendor, JsonPatch.Of(("enabled", on)))), $"{api.Name}: {Loc.T("tools.search.enabled")}"),
            Ui.IconButton(LucideIconKind.Trash2, Loc.T("common.delete"), () => _ = GuardAsync(() => RemoveApiAsync(api))));

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel { Spacing = 2, Children = { name, PdUi.Text(keys, 11.5, ArcDesignTokenKind.TextMuted) } };
        head.Children.Add(text);
        Grid.SetColumn(actions, 1);
        head.Children.Add(actions);

        var body = new StackPanel { Spacing = 8, Children = { head } };
        if (api.NeedsUrl || api.Vendor is "firecrawl" or "you")
        {
            var draft = _urlDrafts.GetValueOrDefault(api.Vendor, api.Url ?? "");
            body.Children.Add(PdUi.Inline(8,
                PdUi.TextBox(draft, v => _urlDrafts[api.Vendor] = v, 320, mono: true, name: Loc.T("tools.search.url"), placeholder: api.NeedsUrl ? "https://searx.example.com" : Loc.T("tools.search.urlDefault")),
                Ui.Button(Loc.T("common.save"), () => _ = GuardAsync(async () =>
                {
                    await _page.Api.UpdateApiAsync(api.Vendor, JsonPatch.Of(("url", _urlDrafts.GetValueOrDefault(api.Vendor, api.Url ?? "").Trim())));
                    _urlDrafts.Remove(api.Vendor);
                }))));
        }
        if (api.Keys.Count > 1)
            body.Children.Add(RowBuilder.Hinted(Loc.T("tools.search.pick"), Loc.T("tools.search.pickHint"),
                PdUi.Choice([("turns", Loc.T("tools.search.pick.turns")), ("order", Loc.T("tools.search.pick.order"))], api.Pick,
                    v => _ = GuardAsync(() => _page.Api.UpdateApiAsync(api.Vendor, JsonPatch.Of(("pick", v)))), 140, Loc.T("tools.search.pick"))));
        var keyIds = api.Keys.Select(k => k.Id).ToList();
        foreach (var k in api.Keys) body.Children.Add(KeyRow(api, k, keyIds));
        body.Children.Add(PdUi.Inline(8,
            PdUi.Secret(_keyDrafts.GetValueOrDefault(api.Vendor, ""), v => _keyDrafts[api.Vendor] = v, 320, Loc.T("tools.search.key"), Loc.T("tools.search.keyPlaceholder")),
            Ui.Button(Loc.T("tools.search.addKey"), () => _ = GuardAsync(async () =>
            {
                var key = _keyDrafts.GetValueOrDefault(api.Vendor, "").Trim();
                if (key.Length == 0) return;
                await _page.Api.AddKeyAsync(api.Vendor, key);
                _keyDrafts.Remove(api.Vendor);
                _settings.Rebuild();
            }), ButtonVariant.Primary, LucideIconKind.Plus)));
        return PdUi.Pad(body);
    }

    private Control KeyRow(SearchApiDto api, SearchKeyDto k, IReadOnlyList<string> keyIds)
    {
        var row = ClientUi.HRow(8, PdUi.Text(k.Masked, 12, ArcDesignTokenKind.TextSecondary, mono: true));
        if (k.Resting is { Length: > 0 } why)
            row.Children.Add(KitBadge.Create(Loc.T("tools.search.rest." + why.Replace(' ', '_')), Tone.Orange,
                k.RestUntil is { } until ? SettingsFormat.DateTime(until, Loc.Language) : null));
        var actions = ClientUi.HRow(4,
            Ui.IconButton(LucideIconKind.ArrowUp, Loc.T("tools.search.moveUp"), () => ReorderKeys(api, keyIds, k.Id, -1), enabled: k.Id != keyIds[0]),
            Ui.IconButton(LucideIconKind.ArrowDown, Loc.T("tools.search.moveDown"), () => ReorderKeys(api, keyIds, k.Id, 1), enabled: k.Id != keyIds[^1]),
            PdUi.Toggle(k.Enabled, on => _ = GuardAsync(() => _page.Api.SetKeyEnabledAsync(api.Vendor, k.Id, on)), $"{k.Masked}: {Loc.T("tools.search.enabled")}"),
            Ui.IconButton(LucideIconKind.Trash2, Loc.T("common.delete"), () => _ = GuardAsync(() => _page.Api.DeleteKeyAsync(api.Vendor, k.Id))));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 0, 0, 0) };
        grid.Children.Add(row);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        return grid;
    }

    private void Reorder(IReadOnlyList<string> ids, string vendor, int by)
    {
        if (ToolsApi.Moved(ids, vendor, by) is { } order) _ = GuardAsync(() => _page.Api.ReorderApisAsync(order));
    }

    private void ReorderKeys(SearchApiDto api, IReadOnlyList<string> ids, string id, int by)
    {
        if (ToolsApi.Moved(ids, id, by) is { } order) _ = GuardAsync(() => _page.Api.ReorderKeysAsync(api.Vendor, order));
    }

    private async Task RemoveApiAsync(SearchApiDto api)
    {
        if (!await _page.Feedback.ConfirmAsync(new Astra.Desktop.Core.Shell.ConfirmRequest(Loc.T("tools.search.removeConfirm", ("name", api.Name)), Loc.T("tools.search.removeDetail"), Loc.T("common.delete"), Destructive: true))) return;
        await _page.Api.DeleteApiAsync(api.Vendor);
    }

    // ---------- fetch and images ----------

    private Control Fetch(WebFetchSettingsDto f)
    {
        static JsonObject Body(string key, object value) => new() { ["fetch"] = JsonPatch.Of((key, value)) };
        return RowBuilder.Group(Loc.T("tools.fetch.title"), Loc.T("tools.fetch.footer"),
            RowBuilder.Hinted(Loc.T("tools.fetch.chars"), Loc.T("tools.fetch.charsHint"),
                PdUi.Number(f.MaxChars, v => { if (v is { } n) Save(Body("maxChars", (int)n)); }, 120, Loc.T("tools.fetch.chars"), integer: true, minimum: 1000)),
            RowBuilder.Hinted(Loc.T("tools.fetch.timeout"), Loc.T("tools.fetch.timeoutHint"),
                PdUi.Number(f.TimeoutSeconds, v => { if (v is { } n) Save(Body("timeoutSeconds", (int)n)); }, 100, Loc.T("tools.fetch.timeout"), integer: true, minimum: 3)),
            RowBuilder.Hinted(Loc.T("tools.fetch.bytes"), Loc.T("tools.fetch.bytesHint"),
                PdUi.Number(f.MaxBytes / 1024 / 1024, v => { if (v is { } n) Save(Body("maxBytes", (int)n * 1024 * 1024)); }, 100, Loc.T("tools.fetch.bytes"), integer: true, minimum: 1)),
            RowBuilder.Hinted(Loc.T("tools.fetch.private"), Loc.T("tools.fetch.privateHint"),
                PdUi.Toggle(f.AllowPrivate, on => Save(Body("allowPrivate", on)), Loc.T("tools.fetch.private"))));
    }

    private Control Image(ImageToolSettingsDto image)
    {
        var save = Ui.Button(Loc.T("common.save"), () => Save(new JsonObject { ["image"] = JsonPatch.Of(("model", _imageModel ?? ""), ("size", _imageSize ?? "")) }));
        return RowBuilder.Group(Loc.T("tools.image.title"), Loc.T("tools.image.footer"),
            RowBuilder.Hinted(Loc.T("tools.image.model"), Loc.T("tools.image.modelHint"),
                PdUi.TextBox(_imageModel, v => _imageModel = v, 224, mono: true, name: Loc.T("tools.image.model"), placeholder: "gpt-image-1")),
            RowBuilder.Hinted(Loc.T("tools.image.size"), Loc.T("tools.image.sizeHint"),
                PdUi.TextBox(_imageSize, v => _imageSize = v, 160, mono: true, name: Loc.T("tools.image.size"), placeholder: "1024x1024")),
            Ui.GroupLine(save, 12, 12));
    }

    // ---------- the tool list ----------

    private Control? RenderTools(Query<List<ToolInfoDto>> q)
    {
        if (!q.HasData) return q.IsError ? null : new LoadingState(2);
        var rows = new List<Control>();
        if (_page.Api.Settings.Data is { } s)
            rows.Add(RowBuilder.Hinted(Loc.T("tools.list.endpoint"), Loc.T("tools.list.endpointHint"), PdUi.Text(s.McpEndpoint, 12, ArcDesignTokenKind.TextSecondary, mono: true)));
        foreach (var tool in q.Data!)
        {
            var label = ClientUi.HRow(8, PdUi.Text(tool.Name, 13, ArcDesignTokenKind.Foreground, mono: true),
                KitBadge.Create(Loc.T(tool.Source == "plugin" ? "tools.list.plugin" : "tools.list.builtin")));
            if (!tool.Available) label.Children.Add(KitBadge.Create(Loc.T("tools.list.unavailable"), Tone.Orange));
            var detail = tool.Available ? tool.Description : tool.Name switch
            {
                "web_search" => Loc.T("tools.reason.web_search"),
                "generate_image" => Loc.T("tools.reason.generate_image"),
                _ => tool.Reason,
            };
            rows.Add(RowBuilder.Row(label, detail));
        }
        return RowBuilder.Group(Loc.T("tools.list.title"), Loc.T("tools.list.footer"), rows.ToArray());
    }
}
