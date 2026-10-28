using System.Text.Json.Nodes;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Platform;
using Astra.Desktop.Theme;
using Astra.Desktop.Pages.Providers;
using Astra.Desktop.Shell;
using Astra.Desktop.Views;

namespace Astra.Desktop.UiTests;

/// <summary>
/// Scenes of the Providers page (plan avalonia-page-inventory §5.4): the list in its states, the add wizard's steps and the import sheet.
/// The web side captures <c>providers__data</c> and <c>providers__empty</c>; the others have no web counterpart yet and are for review.
/// </summary>
public static partial class Scenes
{
    static partial void AddProviders(Dictionary<string, Func<Scene>> d)
    {
        d["providers__data"] = () => Page(AppRoute.Providers, Fixtures());
        d["providers__empty"] = () => Page(AppRoute.Providers, Fixtures().Override("/api/providers", "[]"));
        d["providers__error"] = () => Page(AppRoute.Providers, Fixtures().Override("/api/providers", "{\"error\":\"boom\"}", 500));
        d["providers__states"] = () => Page(AppRoute.Providers, Fixtures().Override("/api/providers", StatesJson()));
        d["providers__loading"] = () => ProvidersLoading();

        // The app menu's "New provider" opens Providers with the parameter "new".
        d["providers__add-wizard-1"] = () => Driven(Fixtures().WithTemplates(), param: "new");
        d["providers__add-wizard-1-search"] = () => Driven(Fixtures().WithTemplates(), vm =>
        {
            vm.RequestAdd();
            vm.AddSheet!.Wizard.Search = "claude";
            vm.AddSheet.Reopen();
        });
        d["providers__add-wizard-2"] = () => Driven(Fixtures().WithTemplates(), vm =>
        {
            vm.RequestAdd();
            var w = vm.AddSheet!.Wizard;
            w.Choice = "openai";
            vm.AddSheet.Continue();
            w.ApiKey = "sk-proj-1234567890";
            vm.AddSheet.Reopen();
        });
        d["providers__add-wizard-2-more"] = () => Driven(Fixtures().WithTemplates(), vm =>
        {
            vm.RequestAdd();
            var w = vm.AddSheet!.Wizard;
            w.Choice = "openai";
            vm.AddSheet.Continue();
            w.ApiKey = "sk-proj-1234567890";
            vm.AddSheet.ExpandMore();
        });
        d["providers__add-wizard-custom"] = () => Driven(Fixtures().WithTemplates(), vm =>
        {
            vm.RequestAdd();
            var w = vm.AddSheet!.Wizard;
            w.Choice = "custom";
            vm.AddSheet.Continue();
        });
        d["providers__add-wizard-subscription"] = () => Driven(Fixtures().WithTemplates(), vm =>
        {
            vm.RequestAdd();
            var w = vm.AddSheet!.Wizard;
            w.Choice = "claude-subscription";
            vm.AddSheet.Continue();
        });
        d["providers__add-wizard-alternative"] = () => Driven(Fixtures().WithTemplates(), vm =>
        {
            vm.RequestAdd();
            var w = vm.AddSheet!.Wizard;
            w.Choice = "zhipu";
            vm.AddSheet.Continue();
        });
        d["providers__import-sheet"] = () => Driven(Fixtures().Override("/api/providers/import/sources", ImportJson()), vm => vm.RequestImport());
    }

    private static Scene Driven(FixtureSet fixtures, Action<ProvidersViewModel>? drive = null, string? param = null)
    {
        var shell = BuildShell(fixtures);
        shell.Navigator.Navigate(AppRoute.Providers, param);
        return new Scene(new ShellView { DataContext = shell }, async () =>
        {
            await shell.Context.Queries.WhenIdleAsync();
            var vm = (ProvidersViewModel)shell.Navigator.Current;
            if (drive is not null)
            {
                drive(vm);
                await shell.Context.Queries.WhenIdleAsync();
            }
        });
    }

    // The list never answers: what the page shows for the first second of a slow server (the scene does not wait for it).
    private static Scene ProvidersLoading()
    {
        var fixtures = Fixtures();
        var api = FixtureApi(new PathHangingHandler(fixtures.Handler(), "/api/providers"));
        var context = PageContext.Create(api, AvaloniaUiDispatcher.Instance, clock: new FixedClock(fixtures.Now));
        var connection = new ConnectionViewModel(null, api, context.Queries);
        connection.AssumeConnected("http://127.0.0.1:17321");
        var prefs = DesktopPrefs.InMemory();
        var shell = new ShellViewModel(prefs, connection, context, AppModules.All);
        shell.Navigator.Navigate(AppRoute.Providers);
        return new Scene(new ShellView { DataContext = shell });
    }

    /// <summary>Delegates everything to the fixtures but never answers one path (a request that stays loading); shared by the scene files.</summary>
    private sealed class PathHangingHandler(HttpMessageHandler inner, string path) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == path) await Task.Delay(Timeout.Infinite, cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private static FixtureSet WithTemplates(this FixtureSet fixtures) => fixtures.Override("/api/provider-templates", TemplatesJson());

    // A broader catalog than the default fixture: the wizard's categories, a subscription (single instance), key alternatives.
    private static string TemplatesJson()
    {
        static JsonObject Tpl(string id, string name, string category, string icon, string[] protocols, string url, string scheme = "bearer", bool needsKey = true, string[]? alternatives = null, bool variants = false)
        {
            var o = new JsonObject
            {
                ["id"] = id, ["version"] = 1, ["name"] = name, ["category"] = category, ["icon"] = icon, ["website"] = url, ["apiKeyUrl"] = needsKey ? url + "/keys" : null,
                ["endpoints"] = new JsonArray(protocols.Select(p => (JsonNode)new JsonObject { ["protocol"] = p, ["baseUrl"] = url + "/v1" }).ToArray()),
                ["preferredUpstreamProtocols"] = new JsonArray(protocols.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
                ["auth"] = new JsonObject { ["scheme"] = scheme }, ["authScheme"] = scheme, ["requiresApiKey"] = needsKey,
                ["models"] = new JsonArray("m1", "m2", "m3"), ["priceKey"] = id,
            };
            if (alternatives is not null) o["keyAlternatives"] = new JsonArray(alternatives.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray());
            if (variants) o["variants"] = new JsonArray(new JsonObject { ["id"] = "intl", ["name"] = "International", ["priceKey"] = id });
            return o;
        }
        var all = new JsonArray(
            Tpl("openai", "OpenAI", "official", "openai", ["openai-chat", "openai-responses"], "https://api.openai.com"),
            Tpl("anthropic", "Anthropic", "official", "anthropic", ["anthropic"], "https://api.anthropic.com"),
            Tpl("gemini", "Google Gemini", "official", "gemini", ["gemini"], "https://generativelanguage.googleapis.com"),
            Tpl("claude-subscription", "Claude 订阅（Pro/Max）", "subscription", "anthropic", ["anthropic"], "https://claude.ai", "oauth-subscription", false),
            Tpl("deepseek", "DeepSeek", "cn", "deepseek", ["openai-chat", "anthropic"], "https://api.deepseek.com"),
            Tpl("zhipu", "Zhipu GLM", "cn", "glm", ["openai-chat"], "https://open.bigmodel.cn", alternatives: ["zhipu-coding"], variants: true),
            Tpl("zhipu-coding", "Zhipu GLM Coding Plan", "coding", "glm", ["openai-chat", "anthropic"], "https://open.bigmodel.cn"),
            Tpl("openrouter", "OpenRouter", "aggregator", "openrouter", ["openai-chat"], "https://openrouter.ai"),
            Tpl("ollama", "Ollama", "local", "ollama", ["openai-chat"], "http://localhost:11434", "none", false));
        return all.ToJsonString();
    }

    // The default providers plus a disabled one, one without a key, one with a template update and many bound clients, one whose usage query failed.
    private static string StatesJson()
    {
        var list = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ui", "default", "providers.json")))!.AsArray();
        JsonObject Copy(int from, string id, string name, Action<JsonObject> edit)
        {
            var o = JsonNode.Parse(list[from]!.ToJsonString())!.AsObject();
            o["id"] = id;
            o["name"] = name;
            edit(o);
            return o;
        }
        var extra = new[]
        {
            Copy(0, "prov_off", "DeepSeek", o => { o["enabled"] = false; o["icon"] = "deepseek"; o["templateId"] = "deepseek"; }),
            Copy(1, "prov_nokey", "OpenRouter", o => { o["hasApiKey"] = false; o["apiKeyMasked"] = null; o["icon"] = "openrouter"; o["authScheme"] = "bearer"; o["quotaConfig"]!["enabled"] = false; o["quota"] = null; }),
            Copy(0, "prov_update", "Zhipu GLM", o =>
            {
                o["templateUpdateAvailable"] = true;
                o["icon"] = "glm";
                o["priceMultiplier"] = 0.5;
                o["boundClients"] = new JsonArray("codex", "claude-code", "opencode", "gemini-cli", "zed", "crush", "droid", "pi", "my-agent");
            }),
            Copy(1, "prov_fail", "Moonshot Kimi", o =>
            {
                o["icon"] = "kimi";
                o["quota"] = JsonNode.Parse("""{"fetchedAtUtc":"2026-10-26T10:00:00Z","isValid":true,"plans":[{"unit":"CNY","remaining":110,"total":200,"used":90,"usedPercent":45}],"error":"x","errorCode":"timeout","errorAtUtc":"2026-10-26T11:50:00Z"}""");
            }),
        };
        foreach (var e in extra) list.Add((JsonNode)e);
        return list.ToJsonString();
    }

    private static string ImportJson() => """
        [
          {"id":"cc-switch","name":"CC Switch","path":"~/.cc-switch/cc-switch.db","found":true,"error":null,"items":[
            {"ref":"1","source":"cc-switch","name":"Anthropic Official","fromApp":"Claude Code","endpoints":[{"protocol":"anthropic","baseUrl":"https://api.anthropic.com"}],"authScheme":"x-api-key","hasKey":true,"keyMasked":"sk-ant-…9xyz","keyFingerprint":"ab12","models":["claude-sonnet-4","claude-opus-4"],"headers":[],"templateId":"anthropic","status":"new","existing":null,"off":false,"skipReason":null,"ignoredFields":[]},
            {"ref":"2","source":"cc-switch","name":"OpenAI","fromApp":"Codex","endpoints":[{"protocol":"openai-responses","baseUrl":"https://api.openai.com/v1"}],"authScheme":"bearer","hasKey":true,"keyMasked":"sk-…a1b2","keyFingerprint":"cd34","models":[],"headers":[],"templateId":"openai","status":"same","existing":{"id":"prov_openai","name":"OpenAI"},"off":false,"skipReason":null,"ignoredFields":[]},
            {"ref":"3","source":"cc-switch","name":"My Relay","fromApp":"Claude Code","endpoints":[{"protocol":"openai-chat","baseUrl":"https://relay.example.com/v1"},{"protocol":"anthropic","baseUrl":"https://relay.example.com"}],"authScheme":"bearer","hasKey":false,"keyMasked":null,"keyFingerprint":null,"models":["gpt-5"],"headers":["X-Org"],"templateId":null,"status":"sameHost","existing":{"id":"prov_x","name":"Relay"},"off":false,"skipReason":null,"ignoredFields":["proxy","timeout"]},
            {"ref":"4","source":"cc-switch","name":"Broken","fromApp":null,"endpoints":[],"authScheme":"bearer","hasKey":false,"keyMasked":null,"keyFingerprint":null,"models":[],"headers":[],"templateId":null,"status":"skip","existing":null,"off":false,"skipReason":"no-base-url","ignoredFields":[]}
          ]},
          {"id":"codex","name":"Codex","path":"~/.codex/config.toml","found":false,"error":null,"items":[]},
          {"id":"alma","name":"Alma","path":null,"found":true,"error":"cannot read settings.json","items":[]}
        ]
        """;
}
