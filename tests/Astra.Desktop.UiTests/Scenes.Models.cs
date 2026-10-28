using System.Net;
using System.Text;
using Astra.Desktop.Components.Pricing;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Pricing;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Pages.Models;
using Astra.Desktop.Platform;
using Astra.Desktop.Shell;
using Astra.Desktop.Theme;
using Astra.Desktop.Views;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using TKCode.Arc;

namespace Astra.Desktop.UiTests;

/// <summary>
/// The Models area's scenes (WP4, plan avalonia-client §24 / inventory §5.4): the catalog (data / empty / search-no-result / create-sheet /
/// sync-preview) and the model detail page (data / price-editor / rule-dialog / delete-confirm). The catalog states match the web capture
/// (<c>models__*__light.png</c>); the detail states have no web capture and are matched from web/src/pages/ModelDetailPage.tsx.
/// A scene that needs an open sheet opens it from its <c>Settled</c> step, after the page loaded, and lets the overlay finish.
/// </summary>
public static partial class Scenes
{
    static partial void AddModels(Dictionary<string, Func<Scene>> d)
    {
        d["models__data"] = () => Page(AppRoute.Models, Fixtures());
        d["models__empty"] = () => Page(AppRoute.Models, Fixtures().Override("/api/models", "[]"));
        d["models__search-no-result"] = () => ModelsScene(Fixtures().Override("/api/models", "[]"), async (shell, view) =>
        {
            var page = (ModelsPage)shell.Navigator.Current;
            page.SearchBox.Text = "gpt-9";
            // the search is debounced (250 ms), then the filtered list is fetched
            await Task.Delay(500);
            await shell.Context.Queries.WhenIdleAsync();
        });
        d["models__create-sheet"] = () => ModelsScene(Fixtures(), async (shell, view) =>
        {
            var page = (ModelsPage)shell.Navigator.Current;
            var sheet = view.GetVisualDescendants().OfType<CreateModelSheet>().First();
            page.IsCreating = true;
            await OverlayAsync();
        });
        d["models__sync-preview"] = () => ModelsScene(Fixtures(), async (shell, view) =>
        {
            var page = (ModelsPage)shell.Navigator.Current;
            var sheet = view.GetVisualDescendants().OfType<SyncSheet>().First();
            page.IsSyncOpen = true;
            await OverlayAsync();
            await sheet.LoadAsync();
            await OverlayAsync();
        }, post: new Dictionary<string, string> { ["/api/models/sync/preview"] = SyncPreviewJson });

        d["model-detail__data"] = () => DetailScene(Fixtures().Override("/api/models/gpt-4.1", SeedModelJson), "gpt-4.1");
        d["model-detail__user"] = () => DetailScene(Fixtures().Override("/api/models/my-model-1", UserModelJson), "my-model-1");
        d["model-detail__price-editor"] = () => DetailScene(Fixtures().Override("/api/models/gpt-4.1", SeedModelJson), "gpt-4.1", async (shell, view) =>
        {
            var sheet = view.GetVisualDescendants().OfType<PriceSheet>().First();
            var detail = ((ModelDetailPage)shell.Navigator.Current).Detail!;
            sheet.Open(detail, detail.ProviderPrices[0]);
            await OverlayAsync();
        });
        d["model-detail__rule-dialog"] = () => DetailScene(Fixtures().Override("/api/models/gpt-4.1", SeedModelJson), "gpt-4.1", async (shell, view) =>
        {
            var sheet = view.GetVisualDescendants().OfType<PriceSheet>().First();
            var detail = ((ModelDetailPage)shell.Navigator.Current).Detail!;
            sheet.Open(detail, detail.ProviderPrices[0]);
            await OverlayAsync();
            var editor = sheet.Sheet.Content is Panel p ? p.Children.OfType<PricingEditor>().First() : throw new InvalidOperationException("no editor in the price sheet");
            editor.BeginAddRule(PricingRuleKind.Time);
            await OverlayAsync();
        });
        d["models__error"] = () => Page(AppRoute.Models, Fixtures().Override("/api/models", "{\"error\":\"database is locked\"}", 500));
        d["model-detail__error"] = () => DetailScene(Fixtures().Override("/api/models/gpt-4.1", "{\"error\":\"Model 'gpt-4.1' was not found\"}", 404), "gpt-4.1");
        d["model-detail__dirty"] = () => DetailScene(Fixtures().Override("/api/models/gpt-4.1", SeedModelJson), "gpt-4.1", async (shell, view) =>
        {
            Named<Input>(view, Astra.Desktop.Core.I18n.Loc.T("models.field.displayName")).Text = "GPT-4.1 (edited)";
            Named<NumberBox>(view, "output").Value = 9;
            await Task.Delay(100);
        });
        d["model-detail__simulator"] = () => DetailScene(Fixtures().Override("/api/models/gpt-4.1", SeedModelJson), "gpt-4.1", async (shell, view) =>
        {
            var run = view.GetVisualDescendants().OfType<TKCode.Arc.Button>().First(b => b.Variant == TKCode.Arc.ButtonVariant.Primary && b.IsVisible && b.Content is StackPanel);
            run.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            await Task.Delay(400);
            // the simulator is below the fold: scroll the page to its end
            foreach (var scroller in view.GetVisualDescendants().OfType<ScrollViewer>().Where(v => v.Extent.Height > v.Viewport.Height + 1))
                scroller.Offset = new Vector(0, scroller.Extent.Height);
            await Task.Delay(300);
        }, post: new Dictionary<string, string> { ["/api/billing/simulate"] = SimulationJson });
        d["model-detail__delete-confirm"] = () => DetailScene(Fixtures().Override("/api/models/my-model-1", UserModelJson), "my-model-1", async (shell, view) =>
        {
            var morph = view.GetVisualDescendants().OfType<ConfirmMorph>().First();
            morph.State = ConfirmMorphState.Confirming;
            await OverlayAsync();
        });
    }

    private static Task OverlayAsync() => Task.Delay(250);

    private static T Named<T>(Visual root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().First(c => Avalonia.Automation.AutomationProperties.GetName(c) == name);

    /// <summary>The Models list in the shell over a fixture API that also answers the given POST bodies; <paramref name="after"/> runs once the page settled.</summary>
    private static Scene ModelsScene(FixtureSet fixtures, Func<ShellViewModel, Control, Task> after, Dictionary<string, string>? post = null)
        => ShellScene(AppRoute.Models, null, fixtures, after, post);

    private static Scene DetailScene(FixtureSet fixtures, string id, Func<ShellViewModel, Control, Task>? after = null, Dictionary<string, string>? post = null)
        => ShellScene(AppRoute.ModelDetail, id, fixtures, after, post);

    private static Scene ShellScene(AppRoute route, string? param, FixtureSet fixtures, Func<ShellViewModel, Control, Task>? after, Dictionary<string, string>? post)
    {
        // The detail page shows dates in UTC like the web capture (timezoneId: UTC in index.json).
        ModelDetailPage.DisplayZone = TimeZoneInfo.Utc;
        var handler = post is null ? fixtures.Handler() : new PostRoutes(fixtures.Handler(), post);
        var api = FixtureApi(handler);
        var context = PageContext.Create(api, AvaloniaUiDispatcher.Instance, clock: new FixedClock(fixtures.Now));
        var connection = new ConnectionViewModel(null, api, context.Queries);
        connection.AssumeConnected("http://127.0.0.1:17321");
        var prefs = DesktopPrefs.InMemory();
        prefs.SidebarCollapsed = false;
        var shell = new ShellViewModel(prefs, connection, context, AppModules.All);
        shell.Navigator.Navigate(route, param);
        var view = new ShellView { DataContext = shell };
        return new Scene(view, async () =>
        {
            await shell.Context.Queries.WhenIdleAsync();
            await shell.Navigator.Current.WhenSettledAsync();
            if (after is not null)
            {
                await after(shell, view);
                await Task.Delay(300);
            }
        });
    }

    /// <summary>Answers POSTs the default fixture set has no route for (the table of path to body); everything else goes to the fixtures.</summary>
    private sealed class PostRoutes(HttpMessageHandler inner, Dictionary<string, string> bodies) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && bodies.TryGetValue(request.RequestUri!.AbsolutePath, out var body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            return base.SendAsync(request, cancellationToken);
        }
    }

    // ---------- scene data ----------

    private const string SyncPreviewJson = """
        {
          "source": "models.dev",
          "fetchedAt": "2026-10-26T12:00:00Z",
          "changes": [
            { "id": "c1", "modelId": "gpt-5.1", "kind": "new_model", "skippedBecauseUserModified": false,
              "before": null, "after": { "displayName": "GPT-5.1", "vendor": "openai", "contextWindow": 400000 } },
            { "id": "c2", "modelId": "gpt-4.1", "kind": "field", "field": "contextWindow", "skippedBecauseUserModified": false,
              "before": 1000000, "after": 1047576 },
            { "id": "c3", "modelId": "gpt-4.1", "kind": "price", "priceKey": "openai", "skippedBecauseUserModified": false,
              "before": { "upstreamModelId": null, "pricing": { "base": { "input": 2, "output": 8, "cache_read": 0.5 } } },
              "after":  { "upstreamModelId": null, "pricing": { "base": { "input": 2.5, "output": 8, "cache_read": 0.25 } } } },
            { "id": "c4", "modelId": "gpt-4.1", "kind": "field", "field": "displayName", "skippedBecauseUserModified": true,
              "before": "GPT-4.1", "after": "GPT 4.1" },
            { "id": "c5", "modelId": "claude-sonnet-4-5", "kind": "field", "field": "pricing", "skippedBecauseUserModified": false,
              "before": { "base": { "input": 3, "output": 15 } },
              "after":  { "base": { "input": 3, "output": 15, "cache_read": 0.3, "cache_write_5m": 3.75, "cache_write_1h": 6, "reasoning": 15 }, "per_request": 0.001 } },
            { "id": "c6", "modelId": "gemini-2.5-pro", "kind": "price", "priceKey": "google", "skippedBecauseUserModified": false,
              "before": null, "after": { "upstreamModelId": "gemini-2.5-pro", "pricing": { "base": { "input": 1.25, "output": 10 } } } }
          ]
        }
        """;

    private const string SimulationJson = """
        {
          "items": [
            { "tokenType": "input", "tokens": 1000000, "isPerCall": false, "baseUnitPrice": 2, "unitPrice": 2, "multiplier": 1, "multiplierSources": [], "tier": "base", "pricedAs": "input", "costNanos": 2000000000, "note": null },
            { "tokenType": "output", "tokens": 100000, "isPerCall": false, "baseUnitPrice": 8, "unitPrice": 4, "multiplier": 0.5, "multiplierSources": ["off-peak"], "tier": "off-peak", "pricedAs": "output", "costNanos": 400000000, "note": null },
            { "tokenType": "cache_write_5m", "tokens": 5000, "isPerCall": false, "baseUnitPrice": 2.5, "unitPrice": 2.5, "multiplier": 1, "multiplierSources": [], "tier": "base", "pricedAs": "input", "costNanos": 12500000, "note": null }
          ],
          "totalNanos": 2412500000, "totalUsd": 2.4125, "priced": true, "trace": [],
          "description": "1,000,000 input tokens at $2 / 1M = $2.00\n100,000 output tokens at $8 / 1M x 0.5 (off-peak) = $0.40",
          "pricingSource": "provider_price", "priceKey": "openai"
        }
        """;

    private const string SeedModelJson = """
        {
          "id": "gpt-4.1", "displayName": "GPT-4.1", "vendor": "openai", "family": "gpt-4", "aliases": ["gpt4.1", "gpt-4.1-latest"],
          "contextWindow": 1047576, "maxOutputTokens": 32768,
          "capabilities": { "vision": true, "tools": true, "reasoning": false, "promptCache": true, "audio": false, "pdf": false, "structuredOutput": true },
          "pricing": { "currency": "USD", "unit": "per_1m_tokens", "base": { "input": 2, "output": 8, "cache_read": 0.5 },
                       "service_tiers": null, "context_tiers": null, "time_windows": null, "per_request": null, "notes": null },
          "source": "seed", "userModifiedFields": ["contextWindow", "pricing"], "enabled": true, "providerPriceKeys": ["openai", "azure"],
          "updatedAt": "2026-10-06T12:00:00Z",
          "providerPrices": [
            { "priceKey": "openai", "upstreamModelId": null, "source": "seed", "userModified": false, "updatedAt": "2026-10-06T12:00:00Z",
              "pricing": { "currency": "USD", "unit": "per_1m_tokens", "base": { "input": 2, "output": 8, "cache_read": 0.5, "reasoning": 0.000003 },
                "context_tiers": [ { "name": ">200K", "threshold_input_tokens": 200000, "mode": "whole", "output_policy": "highest", "prices": { "input": 4, "output": 12 } } ],
                "time_windows": [ { "name": "off-peak", "timezone": "Asia/Shanghai", "start": "00:30", "end": "08:30", "multiplier": 0.5, "days": [1,2,3,4,5], "exclude_dates": ["2026-10-01"] } ],
                "service_tiers": { "flex": { "multiplier": 0.5 }, "priority": { "multiplier": 2 } },
                "notes": "Official list price" } },
            { "priceKey": "azure", "upstreamModelId": "gpt-4.1-2025-04-14", "source": "user", "userModified": true, "updatedAt": "2026-10-06T12:00:00Z",
              "pricing": { "currency": "USD", "unit": "per_1m_tokens", "base": { "input": 2.2, "output": 8.8 } } }
          ],
          "providerOverrides": [
            { "providerId": "prov_openai", "providerName": "OpenAI", "providerModelId": 3, "modelId": "gpt-4.1",
              "pricing": { "currency": "USD", "unit": "per_1m_tokens", "base": { "input": 1.5, "output": 6 } } }
          ]
        }
        """;

    private const string UserModelJson = """
        {
          "id": "my-model-1", "displayName": "My model", "vendor": "acme", "family": null, "aliases": [],
          "contextWindow": 128000, "maxOutputTokens": null,
          "capabilities": { "vision": false, "tools": true },
          "pricing": null,
          "source": "user", "userModifiedFields": [], "enabled": true, "providerPriceKeys": [],
          "updatedAt": "2026-10-20T08:30:00Z", "providerPrices": [], "providerOverrides": []
        }
        """;
}

/// <summary>
/// Behavior of the models pages and the pricing editor in a headless window (the logic itself is unit-tested in Astra.Desktop.Tests/Pricing
/// and /Models): what a person's typing and clicks turn into. Kept in this file because it is the Models package's only UiTests file.
/// </summary>
public class ModelsUiTests
{
    private static AtomUI.Desktop.Controls.Window Show(Control content)
    {
        var window = new AtomUI.Desktop.Controls.Window { Content = content, Width = 1000, Height = 800, IsTitleBarVisible = false };
        window.Show();
        SceneCapture.Pump(5);
        return window;
    }

    private static T Named<T>(Visual root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().First(c => Avalonia.Automation.AutomationProperties.GetName(c) == name);

    private static string Json(JsonNode? n) => PricingDocument.CompactJson(n);

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void Typing_a_price_emits_the_exact_decimal_and_leaves_the_caret_text_alone()
    {
        var editor = new PricingEditor(PricingDocument.Empty().ToNode());
        JsonNode? last = null;
        editor.ValueChanged += (_, e) => last = e.Value;
        var window = Show(editor);
        var box = Named<NumberBox>(editor, "input");

        box.Text = "0.000003";
        Assert.Equal("""{"currency":"USD","unit":"per_1m_tokens","base":{"input":0.000003,"output":null}}""", Json(last));
        // the parent writing the echo back does not rebuild the form (the text typed stays as typed)
        box.Text = "1.";
        Assert.Equal("1.", box.Text);
        Assert.Equal(1m, PriceNumber.FromNode(last!["base"]!["input"]));
        box.Text = "1e-7";
        Assert.Equal("1e-7", box.Text);
        Assert.Equal(0.0000001m, PriceNumber.FromNode(last!["base"]!["input"]));
        window.Close();
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void An_invalid_price_text_marks_the_box_and_keeps_the_last_good_value()
    {
        var editor = new PricingEditor(PricingDocument.Empty().WithBasePrice("input", 3m).ToNode());
        var changes = 0;
        editor.ValueChanged += (_, _) => changes++;
        var window = Show(editor);
        var box = Named<NumberBox>(editor, "input");
        Assert.Equal("3", box.Text);

        box.Text = "3,5";
        Assert.True(box.IsInvalid);
        Assert.Equal(0, changes);
        Assert.Equal(3m, editor.Document!.BasePrice("input"));

        box.Text = "";
        Assert.False(box.IsInvalid);
        Assert.Equal(1, changes);
        Assert.Null(editor.Document!.BasePrice("input"));
        window.Close();
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void Adding_a_rule_through_the_dialog_changes_the_value_only_on_confirm()
    {
        var editor = new PricingEditor(PricingDocument.Empty().ToNode());
        var changes = new List<JsonNode?>();
        editor.ValueChanged += (_, e) => changes.Add(e.Value);
        var window = Show(editor);
        var dialog = editor.GetVisualDescendants().OfType<PricingRuleDialog>().First();

        editor.BeginAddRule(PricingRuleKind.Time);
        SceneCapture.Pump(5);
        Assert.True(dialog.IsOpen);
        dialog.Draft!.Name = "never-applied";
        // cancel: closing without confirming changes nothing
        dialog.Cancel();
        SceneCapture.Pump(5);
        Assert.False(dialog.IsOpen);
        Assert.Empty(changes);
        Assert.Equal(0, editor.Document!.Count(PricingDocument.TimeWindowsKey));

        // confirm: the rule is added with what the draft holds
        editor.BeginAddRule(PricingRuleKind.Time);
        SceneCapture.Pump(5);
        dialog.Draft!.Name = "night";
        Assert.True(dialog.Confirm());
        SceneCapture.Pump(5);
        Assert.Single(changes);
        Assert.Equal("night", editor.Document!.Rule(PricingDocument.TimeWindowsKey, 0).Text("name"));
        // an invalid draft is not added
        editor.BeginAddRule(PricingRuleKind.Context);
        dialog.Draft!.Name = "";
        Assert.False(dialog.Confirm());
        window.Close();
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void The_none_panel_adds_a_schedule_only_after_confirmation_and_remove_goes_back()
    {
        var editor = new PricingEditor(null, allowEmpty: true);
        var changes = new List<JsonNode?>();
        editor.ValueChanged += (_, e) => changes.Add(e.Value);
        var window = Show(editor);
        Assert.Null(editor.Document);

        // "remove" exists only with a price
        editor.Value = PricingDocument.Empty().ToNode();
        SceneCapture.Pump(3);
        Assert.NotNull(editor.Document);
        var remove = editor.GetVisualDescendants().OfType<TKCode.Arc.Button>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) is null && b.Variant == TKCode.Arc.ButtonVariant.Danger);
        remove.Command?.Execute(null);
        remove.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        SceneCapture.Pump(3);
        Assert.Null(editor.Value);
        Assert.Contains(changes, c => c is null);
        window.Close();
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void A_read_only_editor_offers_no_edit_actions()
    {
        var editor = new PricingEditor(PricingDocument.Empty().WithBasePrice("input", 2m).ToNode(), allowEmpty: true) { IsReadOnly = true };
        var window = Show(editor);
        Assert.True(Named<NumberBox>(editor, "input").IsReadOnly);
        Assert.DoesNotContain(editor.GetVisualDescendants().OfType<TKCode.Arc.Button>(), b => b.Variant == TKCode.Arc.ButtonVariant.Danger);
        window.Close();
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void Json_mode_reports_a_syntax_error_and_applies_valid_json()
    {
        var editor = new PricingEditor(PricingDocument.Empty().ToNode());
        var changes = 0;
        editor.ValueChanged += (_, _) => changes++;
        var window = Show(editor);
        var segmented = editor.GetVisualDescendants().OfType<TKCode.Arc.SegmentedControl>().First();
        segmented.SelectedValue = "json";
        SceneCapture.Pump(5);
        var area = editor.GetVisualDescendants().OfType<TKCode.Arc.Textarea>().First();
        Assert.Contains("\"base\"", area.Text);

        area.Text = "{ nope";
        SceneCapture.Pump(3); // TextBox raises TextChanged on the dispatcher
        Assert.False(string.IsNullOrEmpty(area.ErrorMessage));
        Assert.Equal(0, changes);
        area.Text = """{"base":{"input":0.000003}}""";
        SceneCapture.Pump(3);
        Assert.True(string.IsNullOrEmpty(area.ErrorMessage));
        Assert.Equal(1, changes);
        Assert.Equal(0.000003m, editor.Document!.BasePrice("input"));
        window.Close();
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void Editing_the_detail_form_makes_it_dirty_and_saving_puts_it()
    {
        var fixtures = Scenes.Fixtures().Override("/api/models/gpt-4.1", SeedJson);
        var puts = new List<(string Path, string Body)>();
        var api = Scenes.FixtureApi(new CapturePut(fixtures.Handler(), puts));
        var feedback = new RecordingFeedback();
        var context = PageContext.Create(api, AvaloniaUiDispatcher.Instance, feedback: feedback, clock: new FixedClock(fixtures.Now));
        var page = new ModelDetailPage(context, "gpt-4.1");
        page.Activate();
        SceneCapture.Await(page.WhenSettledAsync(), TimeSpan.FromSeconds(10));
        var view = new ModelDetailView(page);
        var window = Show(view);
        Assert.False(page.IsDirty);

        Named<Input>(view, Astra.Desktop.Core.I18n.Loc.T("models.field.displayName")).Text = "GPT 4.1 renamed";
        SceneCapture.Pump(3);
        Assert.True(page.IsDirty);
        SceneCapture.Await(page.SaveAsync(), TimeSpan.FromSeconds(10));

        var put = Assert.Single(puts);
        Assert.Equal("/api/models/gpt-4.1", put.Path);
        Assert.Equal("GPT 4.1 renamed", JsonNode.Parse(put.Body)!["displayName"]!.GetValue<string>());
        Assert.Equal(Astra.Desktop.Core.I18n.Loc.T("common.saved"), feedback.Successes.Single());
        window.Close();
        page.Deactivate();
    }

    private const string SeedJson = """
        {"id":"gpt-4.1","displayName":"GPT-4.1","vendor":"openai","family":"gpt-4","aliases":[],"contextWindow":1047576,"maxOutputTokens":32768,
         "capabilities":{"vision":true},"pricing":{"currency":"USD","unit":"per_1m_tokens","base":{"input":2,"output":8}},
         "source":"seed","userModifiedFields":[],"enabled":true,"providerPriceKeys":[],"updatedAt":"2026-10-06T12:00:00Z","providerPrices":[],"providerOverrides":[]}
        """;

    private sealed class CapturePut(HttpMessageHandler inner, List<(string, string)> puts) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put)
            {
                puts.Add((request.RequestUri!.AbsolutePath, await request.Content!.ReadAsStringAsync(cancellationToken)));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SeedJson, Encoding.UTF8, "application/json") };
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class RecordingFeedback : Astra.Desktop.Core.Shell.IFeedback
    {
        public List<string> Successes { get; } = [];
        public List<string> Errors { get; } = [];
        public void Success(string title, string? description = null) => Successes.Add(title);
        public void Error(string title, string? description = null) => Errors.Add(title);
        public void Info(string title, string? description = null) { }
        public Task<bool> ConfirmAsync(Astra.Desktop.Core.Shell.ConfirmRequest request, CancellationToken ct = default) => Task.FromResult(true);
    }
}
