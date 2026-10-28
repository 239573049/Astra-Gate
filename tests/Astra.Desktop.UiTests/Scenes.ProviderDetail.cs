using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.ProviderDetail;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Pages.ProviderDetail;
using Astra.Desktop.Platform;
using Astra.Desktop.Shell;
using Astra.Desktop.Theme;
using Astra.Desktop.Views;

namespace Astra.Desktop.UiTests;

/// <summary>
/// Scenes of the provider detail page (plan avalonia-page-inventory §5.4): the three tabs (models with mapping, accounts for API-key /
/// subscription / plugin providers with usage and the balance query, settings), the sheets the page opens, the connection test dialog
/// (running / done / failed, single and batch) and the 404. The web side has no capture of this page; these are for review against the web code.
/// </summary>
public static partial class Scenes
{
    private const string PdOpenAi = "prov_openai";
    private const string PdAnthropic = "prov_anthropic";

    static partial void AddProviderDetail(Dictionary<string, Func<Scene>> d)
    {
        d["provider-detail__models"] = () => ProviderDetailDriven(PdOpenAi, Fixtures());
        d["provider-detail__models-mapping"] = () => ProviderDetailDriven(PdOpenAi, Fixtures().Override("/api/providers/" + PdOpenAi, PdProviderJson(PdOpenAi, p =>
            p["settings"] = new JsonObject { ["model_map"] = new JsonObject { ["gpt-5-codex"] = "gpt-4.1", ["o3"] = "o3-pro" } })));
        d["provider-detail__models-empty"] = () => ProviderDetailDriven(PdOpenAi, Fixtures().Override($"/api/providers/{PdOpenAi}/models", "[]"));
        d["provider-detail__model-sheet"] = () => ProviderDetailDriven(PdOpenAi, Fixtures(), vm =>
            vm.View!.ModelsTab!.Select(vm.Api.Models.Data!.First()));
        d["provider-detail__model-sheet-custom"] = () => ProviderDetailDriven(PdOpenAi, Fixtures(), vm =>
        {
            var tab = vm.View!.ModelsTab!;
            tab.Select(vm.Api.Models.Data!.First());
            tab.ModelSheet.Edit(x => x.WithCustomPrice(true).With("displayName", JsonValue.Create("GPT 4.1 (team)")), rebuild: true);
        });
        d["provider-detail__add-models"] = () => ProviderDetailDriven(PdOpenAi, Fixtures(), vm =>
        {
            vm.View!.ModelsTab!.AddSheet.Show();
            vm.View.ModelsTab.AddSheet.Type("gpt-5\ngpt-5-mini, o3");
        });
        d["provider-detail__remote-models"] = () => ProviderDetailDriven(PdOpenAi, Fixtures().Override($"/api/providers/{PdOpenAi}/remote-models", PdRemoteJson()), vm =>
        {
            vm.View!.ModelsTab!.RemoteSheet.Show();
            vm.View.ModelsTab.RemoteSheet.Tick("gpt-5");
        });
        d["provider-detail__remote-models-401"] = () => ProviderDetailDriven("prov_zhipu", PdZhipu(Fixtures())
            .Override("/api/providers/prov_zhipu/remote-models", "{\"error\":\"上游拒绝了 API Key（HTTP 401）\",\"details\":{\"httpStatus\":401}}", 502),
            vm => vm.View!.ModelsTab!.RemoteSheet.Show());
        d["provider-detail__map-sheet"] = () => ProviderDetailDriven(PdOpenAi, Fixtures(), vm =>
        {
            var tab = vm.View!.ModelsTab!;
            tab.MapSheet.ShowFor(vm.Api.Models.Data!);
            tab.MapSheet.Fill("gpt-5-codex", "gpt-4.1");
        });

        d["provider-detail__accounts-apikey"] = () => ProviderDetailDriven(PdOpenAi, PdUsage(Fixtures(), PdOpenAi), vm => vm.Tab = ProviderTab.Accounts);
        d["provider-detail__quota"] = () => ProviderDetailDriven(PdAnthropic, PdUsage(Fixtures(), PdAnthropic), vm => vm.Tab = ProviderTab.Accounts);
        d["provider-detail__quota-custom"] = () => ProviderDetailDriven(PdOpenAi, PdUsage(Fixtures(), PdOpenAi), vm =>
        {
            vm.Tab = ProviderTab.Accounts;
            vm.View!.ApiKeyTab!.Quota.Edit(f => (f with { Enabled = true }).WithTemplate(QuotaForm.Custom, [], null), rebuild: true);
        });
        d["provider-detail__quota-test"] = () => ProviderDetailDriven(PdAnthropic, PdUsage(Fixtures(), PdAnthropic), vm =>
        {
            vm.Tab = ProviderTab.Accounts;
            vm.View!.ApiKeyTab!.Quota.TestSheet.ShowResult(new Astra.Api.Contracts.ProviderQuotaTestDto(false, "unauthorized", "HTTP 401: invalid x-api-key", 401,
                "https://api.anthropic.com/v1/organizations/usage", JsonNode.Parse("{\"error\":{\"type\":\"authentication_error\"}}"), null, "anthropic"));
        });
        d["provider-detail__accounts-subscription"] = () => ProviderDetailDriven(PdAnthropic, Fixtures().Override("/api/providers/" + PdAnthropic,
            PdProviderJson(PdAnthropic, p => p["authScheme"] = "oauth-subscription")), vm => vm.Tab = ProviderTab.Accounts);
        d["provider-detail__accounts-plugin"] = () => ProviderDetailDriven(PdAnthropic, Fixtures().Override("/api/providers/" + PdAnthropic,
            PdProviderJson(PdAnthropic, p => p["authScheme"] = "plugin")), vm => vm.Tab = ProviderTab.Accounts);

        // In the shell's scroller: the bulk bar of a long account grid is held at the viewport's bottom (web sticky bottom-3).
        d["provider-detail__accounts-selection"] = () => ProviderDetailDriven("prov_claude", PdAccounts(Fixtures()), async vm =>
        {
            vm.Tab = ProviderTab.Accounts;
            var view = (Astra.Desktop.Components.Accounts.SubscriptionAccountsView)vm.View!.AccountsView!;
            view.Model.OpenUrl = _ => Task.CompletedTask;
            // the tab's queries start when it is attached: select once the list is there
            await vm.WhenSettledAsync();
            view.Model.ToggleSelect("acc_max");
            view.Model.ToggleSelect("acc_pro");
        });
        d["provider-detail__settings"] = () => ProviderDetailDriven(PdOpenAi, Fixtures(), vm => vm.Tab = ProviderTab.Settings);
        d["provider-detail__settings-dirty"] = () => ProviderDetailDriven(PdOpenAi, Fixtures(), vm =>
        {
            vm.Tab = ProviderTab.Settings;
            vm.View!.SettingsTab!.Set(f => (f with { Name = "OpenAI (team)", Preferred = ["openai-responses", "openai-chat", "gemini"] }).AddHeader("OpenAI-Organization", "org-123"), all: true);
        });
        d["provider-detail__add-endpoint"] = () => ProviderDetailDriven(PdOpenAi, Fixtures(), vm =>
        {
            vm.Tab = ProviderTab.Settings;
            vm.View!.SettingsTab!.EndpointSheet.Show();
            vm.View.SettingsTab.EndpointSheet.Type("ftp://bad");
        });

        d["provider-detail__template-switch"] = () => ProviderDetailDriven("prov_zhipu", PdZhipu(Fixtures()), vm =>
        {
            vm.Request(ProviderSheetKind.TemplateSwitch);
            vm.View!.SwitchSheet.Pick("zhipu-coding");
        });
        d["provider-detail__template-update"] = () => ProviderDetailDriven(PdOpenAi, Fixtures().Override($"/api/providers/{PdOpenAi}/template-update",
            "{\"currentVersion\":1,\"latestVersion\":3,\"changes\":[\"新增模型 gpt-5、gpt-5-mini\",\"Responses 接口地址改为 https://api.openai.com/v1\"]}"),
            vm => vm.Request(ProviderSheetKind.TemplateUpdate));

        d["provider-detail__test-idle"] = () => ProviderDetailDriven(PdOpenAi, Fixtures(), vm => vm.Request(ProviderSheetKind.Test));
        d["provider-detail__test-running"] = () => ProviderDetailDriven(PdOpenAi, Fixtures(), async vm =>
        {
            vm.Request(ProviderSheetKind.Test);
            _ = vm.View!.TestDialog!.Session.StartSingleAsync();
            await Task.Delay(150);
        }, PdTestStream.Running);
        d["provider-detail__test-done"] = () => ProviderDetailDriven(PdOpenAi, Fixtures(), async vm =>
        {
            vm.Request(ProviderSheetKind.Test);
            await vm.View!.TestDialog!.Session.StartSingleAsync();
            await Task.Delay(100);
        }, PdTestStream.Done);
        d["provider-detail__test-failed"] = () => ProviderDetailDriven("prov_zhipu", PdZhipu(Fixtures()), async vm =>
        {
            vm.Request(ProviderSheetKind.Test);
            vm.View!.TestDialog!.Session.Model = "glm-4.6";
            await vm.View.TestDialog.Session.StartSingleAsync();
            await Task.Delay(100);
        }, PdTestStream.Failed);
        d["provider-detail__test-batch"] = () => ProviderDetailDriven(PdOpenAi, Fixtures(), async vm =>
        {
            vm.Request(ProviderSheetKind.Test);
            var s = vm.View!.TestDialog!.Session;
            s.SetMode(true);
            s.SelectAll(true);
            await s.StartBatchAsync(s.Selected.ToList());
            await Task.Delay(100);
        }, PdTestStream.Done);

        d["provider-detail__loading"] = () => ProviderDetailLoading();
        d["provider-detail__404"] = () => ProviderDetailDriven("prov_missing", Fixtures().Override("/api/providers/prov_missing", "{\"error\":\"Provider 'prov_missing' not found\"}", 404));
    }

    /// <summary>How the fixture answers <c>POST …/test</c>.</summary>
    private enum PdTestStream
    {
        /// <summary>Headers and part of the answer, then the stream stays open.</summary>
        Running,

        /// <summary>A complete successful run.</summary>
        Done,

        /// <summary>The upstream refuses the key (401).</summary>
        Failed,
    }

    private static Scene ProviderDetailDriven(string id, FixtureSet fixtures, Func<ProviderDetailPage, Task>? drive = null, PdTestStream test = PdTestStream.Done)
    {
        // The accounts tab shows dates in UTC like the web capture (timezoneId: UTC).
        Astra.Desktop.Core.Accounts.AccountRules.DisplayZone = TimeZoneInfo.Utc;
        var api = FixtureApi(new PdTestHandler(fixtures.Handler(), test));
        var context = PageContext.Create(api, AvaloniaUiDispatcher.Instance, clock: new FixedClock(fixtures.Now));
        var connection = new ConnectionViewModel(null, api, context.Queries);
        connection.AssumeConnected("http://127.0.0.1:17321");
        var shell = new ShellViewModel(DesktopPrefs.InMemory(), connection, context, AppModules.All);
        shell.Navigator.Navigate(AppRoute.Providers);
        shell.Navigator.Push(AppRoute.ProviderDetail, id);
        return new Scene(new ShellView { DataContext = shell }, async () =>
        {
            await context.Queries.WhenIdleAsync();
            var vm = (ProviderDetailPage)shell.Navigator.Current;
            await vm.WhenSettledAsync();
            if (drive is not null)
            {
                await drive(vm);
                await vm.WhenSettledAsync();
            }
        });
    }

    // The provider never answers: the skeleton under the list's title (the scene does not wait for it).
    private static Scene ProviderDetailLoading()
    {
        var fixtures = Fixtures();
        var api = FixtureApi(new PathHangingHandler(fixtures.Handler(), "/api/providers/" + PdOpenAi));
        var context = PageContext.Create(api, AvaloniaUiDispatcher.Instance, clock: new FixedClock(fixtures.Now));
        var connection = new ConnectionViewModel(null, api, context.Queries);
        connection.AssumeConnected("http://127.0.0.1:17321");
        var shell = new ShellViewModel(DesktopPrefs.InMemory(), connection, context, AppModules.All);
        shell.Navigator.Navigate(AppRoute.Providers);
        shell.Navigator.Push(AppRoute.ProviderDetail, PdOpenAi);
        return new Scene(new ShellView { DataContext = shell });
    }

    private static Scene ProviderDetailDriven(string id, FixtureSet fixtures, Action<ProviderDetailPage> drive, PdTestStream test = PdTestStream.Done)
        => ProviderDetailDriven(id, fixtures, vm => { drive(vm); return Task.CompletedTask; }, test);

    private static string PdProviderJson(string id, Action<JsonObject> change)
    {
        var o = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ui", "default", "providers", id + ".json")))!;
        change(o);
        return o.ToJsonString();
    }

    private static FixtureSet PdUsage(FixtureSet f, string id) => f
        .Override($"/api/providers/{id}/usage", "{\"day\":\"2026-10-26\",\"timeZone\":\"UTC\",\"accounts\":[{\"accountId\":null,\"total\":{\"requests\":1824,\"inputTokens\":5120000,\"outputTokens\":930000,\"costNanoUsd\":48210000000},\"today\":{\"requests\":64,\"inputTokens\":182000,\"outputTokens\":30500,\"costNanoUsd\":1520000000}}]}")
        .Override("/api/provider-quota/templates", "[{\"id\":\"anthropic\",\"name\":\"Anthropic\",\"kind\":\"balance\",\"params\":[],\"request\":{\"method\":\"GET\",\"url\":\"{{origin}}/v1/organizations/usage\",\"auth\":\"provider\",\"headers\":{}},\"extract\":{\"remaining\":\"balance\"}},{\"id\":\"newapi\",\"name\":\"New API\",\"kind\":\"balance\",\"params\":[{\"name\":\"userId\",\"secret\":false,\"required\":true},{\"name\":\"accessToken\",\"secret\":true,\"required\":true}],\"request\":{\"method\":\"GET\",\"url\":\"{{origin}}/api/user/self\",\"auth\":\"none\",\"headers\":{}},\"extract\":{}},{\"id\":\"glm-coding\",\"name\":\"GLM Coding Plan\",\"kind\":\"plan\",\"params\":[],\"request\":{\"method\":\"GET\",\"url\":\"{{origin}}/api/monitor/usage/quota/limit\",\"auth\":\"provider\",\"headers\":{}},\"extract\":{}}]");

    /// <summary>The accounts scenes' subscription provider and its routes, as fixture overrides (GET routes only).</summary>
    private static FixtureSet PdAccounts(FixtureSet f)
    {
        f.Override("/api/providers/prov_claude", AccountsData.Claude);
        foreach (var (key, (body, status)) in AccountsData.Routes(AccountsData.Claude))
            if (key.StartsWith("GET ", StringComparison.Ordinal)) f.Override(key[4..], body, status);
        return f;
    }

    /// <summary>A Zhipu provider whose template names a key alternative (and the catalog that says so).</summary>
    private static FixtureSet PdZhipu(FixtureSet f) => f
        .Override("/api/providers/prov_zhipu", PdProviderJson(PdOpenAi, p =>
        {
            p["id"] = "prov_zhipu";
            p["templateId"] = "zhipu";
            p["name"] = "Zhipu GLM";
            p["icon"] = "glm";
            p["priceKey"] = "zhipu";
            p["endpoints"] = new JsonArray(new JsonObject { ["protocol"] = "openai-chat", ["baseUrl"] = "https://open.bigmodel.cn/api/paas/v4", ["fullUrl"] = false });
            p["preferredUpstreamProtocols"] = new JsonArray("openai-chat");
            p["boundClients"] = new JsonArray();
        }))
        .Override("/api/providers/prov_zhipu/models", "[]")
        .Override("/api/provider-templates", TemplatesJson());

    private static string PdRemoteJson() => new JsonArray(
        new JsonObject { ["id"] = "gpt-4.1", ["displayName"] = "GPT-4.1", ["linkedSystemModelId"] = "gpt-4.1", ["alreadyAdded"] = true },
        new JsonObject { ["id"] = "gpt-5", ["displayName"] = "GPT-5", ["linkedSystemModelId"] = "gpt-5", ["alreadyAdded"] = false },
        new JsonObject { ["id"] = "gpt-5-mini", ["displayName"] = null, ["linkedSystemModelId"] = null, ["alreadyAdded"] = false },
        new JsonObject { ["id"] = "o3", ["displayName"] = null, ["linkedSystemModelId"] = "o3", ["alreadyAdded"] = false }).ToJsonString();

    /// <summary>The fixtures plus the event stream of <c>POST /api/providers/{id}/test</c>.</summary>
    private sealed class PdTestHandler(HttpMessageHandler inner, PdTestStream mode) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post || !request.RequestUri!.AbsolutePath.EndsWith("/test", StringComparison.Ordinal))
                return await base.SendAsync(request, cancellationToken);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var model = body["modelId"]!.GetValue<string>();
            var frames = new StringBuilder();
            void Frame(string ev, JsonObject data) => frames.Append("event: ").Append(ev).Append("\ndata: ").Append(data.ToJsonString()).Append("\n\n");
            Frame("start", new JsonObject { ["protocol"] = body["protocol"]?.GetValue<string>(), ["model"] = model, ["stream"] = true });
            var headers = new JsonObject { ["content-type"] = "text/event-stream", ["x-request-id"] = "req_8f2c1a", ["openai-processing-ms"] = "412" };
            if (mode == PdTestStream.Failed)
            {
                Frame("headers", new JsonObject { ["httpStatus"] = 401, ["httpMs"] = 238, ["contentType"] = "application/json", ["headers"] = new JsonObject { ["content-type"] = "application/json" } });
                Frame("done", new JsonObject
                {
                    ["ok"] = false, ["error"] = "上游拒绝了 API Key（HTTP 401）：invalid api key", ["httpStatus"] = 401, ["httpMs"] = 238, ["ttftMs"] = null, ["totalMs"] = 241,
                    ["text"] = "", ["reasoning"] = "", ["raw"] = "{\"error\":{\"code\":\"1000\",\"message\":\"invalid api key\"}}", ["rawTruncated"] = false,
                    ["contentType"] = "application/json", ["headers"] = new JsonObject { ["content-type"] = "application/json" },
                });
            }
            else
            {
                Frame("headers", new JsonObject { ["httpStatus"] = 200, ["httpMs"] = 412, ["contentType"] = "text/event-stream", ["headers"] = headers.DeepClone() });
                Frame("delta", new JsonObject { ["kind"] = "reasoning", ["text"] = "The user asks for a short confirmation." });
                Frame("delta", new JsonObject { ["kind"] = "text", ["text"] = "OK" });
                if (mode == PdTestStream.Done)
                    Frame("done", new JsonObject
                    {
                        ["ok"] = true, ["error"] = null, ["httpStatus"] = 200, ["httpMs"] = 412, ["ttftMs"] = 655, ["totalMs"] = 812,
                        ["text"] = "OK — the connection works. (" + model + ")", ["reasoning"] = "The user asks for a short confirmation.",
                        ["raw"] = "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}\n\ndata: [DONE]", ["rawTruncated"] = false, ["contentType"] = "text/event-stream", ["headers"] = headers.DeepClone(),
                    });
            }
            var pipe = new Pipe();
            await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(frames.ToString()), cancellationToken);
            // a running test keeps its stream open; the others end it
            if (mode != PdTestStream.Running) await pipe.Writer.CompleteAsync();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(pipe.Reader.AsStream()) { Headers = { { "Content-Type", "text/event-stream" } } } };
        }
    }
}
