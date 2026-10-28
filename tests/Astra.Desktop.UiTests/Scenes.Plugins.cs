using System.Net;
using System.Text;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Plugins;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.SmartRouting;
using Astra.Desktop.Pages.Plugins;
using Astra.Desktop.Pages.SmartRouting;
using Astra.Desktop.Platform;
using Astra.Desktop.Shell;
using Astra.Desktop.Theme;
using Astra.Desktop.Views;

namespace Astra.Desktop.UiTests;

/// <summary>
/// Scenes of the Plugins page (installed with and without plugins, the market and a search, the runtime in its three Bun states, a job,
/// the options / install / import sheets) and of the Smart routing page (unconfigured, configured with a status, a test result, a refused
/// save). The default fixture set has no plugin or routing data, so each scene brings its own answers (<see cref="PluginData"/>,
/// <see cref="RoutingData"/>) and, for the writes a state needs (a refused save, a test), a small interceptor in front of the fixture handler.
/// </summary>
public static partial class Scenes
{
    static partial void AddPlugins(Dictionary<string, Func<Scene>> d)
    {
        d["plugins__installed"] = () => PluginsScene(PluginData.Fixtures());
        d["plugins__installed-empty"] = () => PluginsScene(PluginData.Fixtures().Override("/api/plugins", PluginData.List("[]", PluginData.BunReady)));
        d["plugins__market"] = () => PluginsScene(PluginData.Fixtures(), "market");
        d["plugins__market-search"] = () => PluginsScene(PluginData.Fixtures(), "market", async (page, queries) =>
        {
            page.Model.SearchText = "word";
            await Task.Delay(700); // the search waits for the text to rest (400 ms)
            await queries.WhenIdleAsync();
        });
        d["plugins__runtime-bun-missing"] = () => PluginsScene(PluginData.Fixtures().Override("/api/plugins", PluginData.List(PluginData.Installed, PluginData.BunMissing)), "runtime");
        d["plugins__runtime-bun-installing"] = () => PluginsScene(PluginData.Fixtures().Override("/api/plugins", PluginData.List(PluginData.Installed, PluginData.BunInstalling)), "runtime");
        d["plugins__runtime-bun-ready"] = () => PluginsScene(PluginData.Fixtures(), "runtime");
        d["plugins__job-running"] = () => PluginsScene(PluginData.Fixtures().Override("/api/plugins/jobs/j1", PluginData.JobRunning), null, async (page, queries) =>
        {
            page.Model.JobId = "j1";
            await queries.WhenIdleAsync();
        });
        d["plugins__detail-sheet"] = () => PluginsScene(PluginData.Fixtures(), null, async (page, queries) =>
        {
            page.Model.Editing = page.Model.Installed.First(p => p.Middleware is not null);
            await queries.WhenIdleAsync();
        });
        d["plugins__add-sheet"] = () => PluginsScene(PluginData.Fixtures(), null, (page, _) =>
        {
            page.Model.Adding = true;
            return Task.CompletedTask;
        });
        d["plugins__import-sheet"] = () => PluginsScene(PluginData.Fixtures(), null, async (page, queries) =>
        {
            page.Model.Importing = true;
            await queries.WhenIdleAsync();
        });

        d["smart-routing__unconfigured"] = () => RoutingScene(RoutingData.Fixtures(RoutingData.Unconfigured));
        d["smart-routing__configured"] = () => RoutingScene(RoutingData.Fixtures(RoutingData.Configured));
        d["smart-routing__custom-judge"] = () => RoutingScene(RoutingData.Fixtures(RoutingData.CustomJudge));
        d["smart-routing__test-result"] = () => RoutingScene(RoutingData.Fixtures(RoutingData.Configured), async (form, queries) =>
        {
            form.Tester.Text = "Refactor this module so the parser streams its input and add tests.";
            await form.Tester.RunAsync();
            await queries.WhenIdleAsync();
        }, RoutingData.Intercept);
        d["smart-routing__validation-error"] = () => RoutingScene(RoutingData.Fixtures(RoutingData.Unconfigured), async (form, queries) =>
        {
            form.Edit(DraftArea.None, draft => draft.Model = "");
            await form.SaveAsync(true);
            await queries.WhenIdleAsync();
        }, RoutingData.Intercept);
    }

    private static Scene PluginsScene(FixtureSet fixtures, string? tab = null, Func<PluginsPage, QueryClient, Task>? after = null)
    {
        // The web capture runs in UTC (index.timeZone); the machine's zone would shift every time text.
        Astra.Desktop.Core.Plugins.PluginFormat.DisplayZone = TimeZoneInfo.Utc;
        var shell = BuildShellWith(fixtures, null);
        shell.Navigator.Navigate(AppRoute.Plugins, tab);
        return new Scene(new ShellView { DataContext = shell }, async () =>
        {
            await shell.Context.Queries.WhenIdleAsync();
            await shell.Navigator.Current.WhenSettledAsync();
            if (after is not null && shell.Navigator.Current is PluginsPage page)
            {
                await after(page, shell.Context.Queries);
                await shell.Context.Queries.WhenIdleAsync();
            }
        });
    }

    private static Scene RoutingScene(FixtureSet fixtures, Func<SmartRoutingFormModel, QueryClient, Task>? after = null, Func<HttpRequestMessage, HttpResponseMessage?>? intercept = null)
    {
        // The web capture runs in UTC (index.timeZone); the machine's zone would shift every time text.
        Astra.Desktop.Core.Plugins.PluginFormat.DisplayZone = TimeZoneInfo.Utc;
        var shell = BuildShellWith(fixtures, intercept);
        shell.Navigator.Navigate(AppRoute.SmartRouting);
        return new Scene(new ShellView { DataContext = shell }, async () =>
        {
            await shell.Context.Queries.WhenIdleAsync();
            await shell.Navigator.Current.WhenSettledAsync();
            if (after is not null && shell.Navigator.Current is SmartRoutingPage page && page.Model.Form is { } form)
            {
                await after(form, shell.Context.Queries);
                await shell.Context.Queries.WhenIdleAsync();
            }
        });
    }

    // Scenes.BuildShell with a handler in front of the fixture one, for the writes a state needs (the fixture set only answers GETs).
    private static ShellViewModel BuildShellWith(FixtureSet fixtures, Func<HttpRequestMessage, HttpResponseMessage?>? intercept)
    {
        HttpMessageHandler handler = fixtures.Handler();
        if (intercept is not null) handler = new Interceptor(handler, intercept);
        var api = FixtureApi(handler);
        var context = PageContext.Create(api, AvaloniaUiDispatcher.Instance, clock: new FixedClock(fixtures.Now));
        var connection = new ConnectionViewModel(null, api, context.Queries);
        connection.AssumeConnected("http://127.0.0.1:17321");
        return new ShellViewModel(DesktopPrefs.InMemory(), connection, context, AppModules.All);
    }

    private sealed class Interceptor(HttpMessageHandler inner, Func<HttpRequestMessage, HttpResponseMessage?> intercept) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => intercept(request) is { } answer ? Task.FromResult(answer) : base.SendAsync(request, cancellationToken);
    }

    /// <summary>A JSON answer for the interceptors.</summary>
    internal static HttpResponseMessage JsonAnswer(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>Admin API answers of the Plugins page's scenes (shapes of <c>PluginsDto</c>, <c>PluginHostStatusDto</c>, <c>MarketListingDto</c> …).</summary>
internal static class PluginData
{
    public const string BunReady = """{"path":"/Users/demo/.astra/plugins/runtime/bun-1.2.23/bun","version":"1.2.23","downloading":false,"target":"darwin-aarch64"}""";
    public const string BunMissing = """{"version":"1.2.23","downloading":false,"target":"darwin-aarch64"}""";
    public const string BunInstalling = """{"version":"1.2.23","downloading":true,"target":"darwin-aarch64"}""";

    /// <summary>Installed plugins: gateway middleware with counters, a provider plugin another also serves, an agent, a local one that is off.</summary>
    public const string Installed = """
        [
          {"spec":"@astragate-community/middleware-word-guard","name":"@astragate-community/middleware-word-guard","shortName":"word-guard","version":"0.1.4","enabled":true,"kinds":["middleware"],"source":"market","options":{"words":["secret","password"],"action":"mask"},"optionsExample":{"words":["example"]},"isPath":false,
           "middleware":{"hooks":["onRequest","onResponse"],"calls":1280,"avgMicros":42.37,"failures":2,"lastError":"regex timed out on a 2 MB prompt"}},
          {"spec":"@astragate-community/middleware-model-map","name":"@astragate-community/middleware-model-map","shortName":"model-map","version":"0.1.4","enabled":true,"kinds":["middleware"],"source":"market","optionsExample":{"map":{"gpt-4":"gpt-5"}},"isPath":false,
           "middleware":{"hooks":["onRequest"],"calls":310,"avgMicros":8.5,"failures":0}},
          {"spec":"@astragate-community/cursor-auth","name":"@astragate-community/cursor-auth","shortName":"cursor-auth","version":"0.2.14","enabled":true,"kinds":["provider"],"source":"market","icon":"https://unpkg.com/@lobehub/icons-static-svg@1.95.1/icons/cursor.svg","isPath":false},
          {"spec":"@astragate-community/aider","name":"@astragate-community/aider","shortName":"aider","version":"0.1.1","enabled":true,"kinds":["agent"],"source":"market","isPath":false,"agent":{"id":"aider","kind":"plugin-aider","name":"Aider"}},
          {"spec":"/Users/demo/plugins/my-provider","name":"my-provider","shortName":"my-provider","enabled":false,"kinds":["provider","pi"],"source":"path","isPath":true,"error":"/Users/demo/plugins/my-provider/package.json is missing: install it again"}
        ]
        """;

    public static string List(string plugins, string bun) => $$"""{"plugins":{{plugins}},"preferences":{"cursor":"@astragate-community/cursor-auth"},"runtime":{{bun}}}""";

    public const string Host = """
        {"running":true,"plugins":[
          {"spec":"@astragate-community/cursor-auth","provides":["cursor","cursor-pro"],"servedBy":{"cursor-pro":"@someone/other-cursor"}},
          {"spec":"/Users/demo/plugins/my-provider","error":"Cannot find module 'zod'","provides":[],"servedBy":{}}],
         "recent":[
          {"at":"2026-10-26T11:48:00Z","variant":"success","title":"Cursor","message":"Signed in as demo@example.com"},
          {"at":"2026-10-26T10:02:00Z","variant":"warning","title":"","message":"Token expires in 3 days"},
          {"at":"2026-10-25T21:30:00Z","variant":"error","title":"my-provider","message":"Cannot find module 'zod'"}]}
        """;

    public const string Market = """
        [
          {"package":"@astragate-community/middleware-word-guard","name":"Word Guard","kind":"middleware","providers":[],"summary":{"en":"Masks or blocks configured words in requests before they leave your machine.","zh":"在请求离开本机之前，遮盖或拦截配置的敏感词。"},"latest":"0.1.4","origin":"upstream","tags":[],"tarballs":[]},
          {"package":"@astragate-community/middleware-model-map","name":"Model Map","kind":"middleware","providers":[],"summary":{"en":"Rewrites the model a client asks for.","zh":"改写客户端请求的模型名。"},"latest":"0.1.4","origin":"upstream","tags":[],"tarballs":[]},
          {"package":"@astragate-community/middleware-think-tags","name":"Think Tags","kind":"middleware","providers":[],"summary":{"en":"Moves <think> blocks into reasoning content.","zh":"把 <think> 段落移到 reasoning 字段。"},"latest":"0.1.3","origin":"astra","tags":[],"minAstraVersion":"0.11.0","tarballs":[]},
          {"package":"@astragate-community/cursor-auth","name":"Cursor","kind":"provider","providers":["cursor"],"summary":{"en":"Use your Cursor subscription as a provider.","zh":"把 Cursor 订阅作为提供商使用。"},"latest":"0.2.14","origin":"upstream","tags":[],"tarballs":[]},
          {"package":"@astragate-community/kiro-auth","name":"Kiro","kind":"provider","providers":["kiro"],"summary":{"en":"Use your Kiro subscription as a provider.","zh":"把 Kiro 订阅作为提供商使用。"},"latest":"0.1.12","origin":"upstream","tags":[],"tarballs":[]},
          {"package":"@astragate-community/qoder-auth","name":"Qoder","kind":"provider","providers":["qoder"],"summary":{"en":"Use your Qoder subscription as a provider.","zh":"把 Qoder 订阅作为提供商使用。"},"latest":"0.2.17","origin":"upstream","tags":[],"tarballs":[]},
          {"package":"@astragate-community/aider","name":"Aider","kind":"agent","providers":[],"summary":{"en":"Aider on the Clients page: pick one of the gateway's models for it, and switch it off to put its config back.","zh":"把 Aider 加到客户端页。"},"latest":"0.1.1","origin":"upstream","tags":[],"tarballs":[]},
          {"package":"@astragate-community/zed-auth","name":"Zed","kind":"provider","providers":["zed"],"summary":{"en":"Use your Zed subscription as a provider.","zh":"把 Zed 订阅作为提供商使用。"},"latest":"0.1.9","origin":"upstream","tags":[],"tarballs":[]},
          {"package":"@astragate-community/trae-auth","name":"Trae","kind":"provider","providers":["trae"],"summary":{"en":"Use your Trae subscription as a provider.","zh":"把 Trae 订阅作为提供商使用。"},"latest":"0.2.11","origin":"upstream","tags":[],"tarballs":[]}
        ]
        """;

    public const string Search = """
        [
          {"spec":"@astragate-community/middleware-word-guard","name":"Word Guard","description":"Masks or blocks configured words in requests before they leave your machine.","version":"0.1.4","source":"astra","kind":"middleware","stars":0},
          {"spec":"opencode-word-filter","name":"opencode-word-filter","description":"Filter words from OpenCode prompts.","version":"1.0.2","source":"npm","kind":"provider","stars":0},
          {"spec":"github:someone/magpie-wordlist","name":"magpie-wordlist","description":"A word list plugin for magpie.","source":"github","kind":"middleware","stars":42}
        ]
        """;

    public const string Magpie = """{"found":true,"path":"/Users/demo/.magpie/plugins.json","plugins":[{"spec":"opencode-gemini-auth","off":false,"hasOptions":false,"installed":false},{"spec":"@astragate-community/cursor-auth","off":false,"hasOptions":true,"installed":true},{"spec":"magpie-plugin-notify","off":true,"hasOptions":false,"installed":false}],"signIns":2}""";

    public const string JobRunning = """{"id":"j1","action":"add","target":"@astragate-community/kiro-auth","state":"running","startedAt":"2026-10-26T11:59:30Z","log":["Installing @astragate-community/kiro-auth from the market","downloading kiro-auth-0.1.12.tgz (4.1 kB)","verifying sha256 …","bun add /Users/demo/.astra/plugins/packages/kiro-auth-0.1.12.tgz"],"result":null}""";

    public static FixtureSet Fixtures() => Scenes.Fixtures()
        .Override("/api/plugins", List(Installed, BunReady))
        .Override("/api/plugins/host", Host)
        .Override("/api/plugins/market", Market)
        .Override("/api/plugins/search", Search)
        .Override("/api/plugins/import-magpie", Magpie);
}

/// <summary>Admin API answers of the Smart routing scenes (shapes of <c>SmartRoutingDto</c> and its test / refusal bodies).</summary>
internal static class RoutingData
{
    private const string Protocols = """[{"id":"typesafe","defaultBaseUrl":"https://api.typesafe.ai","defaultModel":"jev-latest"},{"id":"openai","defaultBaseUrl":"https://api.openai.com","defaultModel":"gpt-5-mini"}]""";

    private const string Instructions = "Read the user's latest request and choose the grade whose description fits it best.";

    private const string Models = """
        [{"id":"gpt-5-mini","name":"gpt-5-mini","providerId":"prov_openai","providerName":"OpenAI"},
         {"id":"gpt-5","name":"gpt-5","providerId":"prov_openai","providerName":"OpenAI"},
         {"id":"claude-sonnet-4","name":"claude-sonnet-4","providerId":"prov_anthropic","providerName":"Anthropic"},
         {"id":"claude-opus-4","name":"claude-opus-4","providerId":"prov_anthropic","providerName":"Anthropic"}]
        """;

    private static string Fill(string json) => json.Replace("@@INSTRUCTIONS@@", Instructions, StringComparison.Ordinal).Replace("@@PROTOCOLS@@", Protocols, StringComparison.Ordinal);

    /// <summary>Fresh install: off, no key, grades without models.</summary>
    public static readonly string Unconfigured = Fill("""
        {"enabled":false,"virtualModel":"astra-auto",
         "judge":{"protocol":"typesafe","effectiveBaseUrl":"https://api.typesafe.ai","hasApiKey":false,"model":"jev-latest","timeoutMs":2000},
         "grades":[{"id":"simple","name":"","description":"Quick questions, small edits, lookups","model":""},
                   {"id":"medium","name":"","description":"Everyday coding work and explanations","model":""},
                   {"id":"hard","name":"","description":"Architecture, hard debugging, long reasoning","model":""}],
         "rules":{"mode":"session","sessionTtlMinutes":30,"fallbackGrade":"medium","minConfidence":0.6,"contextTurns":2,"maxChars":4000},
         "status":{"breakerOpen":false,"pins":0,"recent":{"total":0,"outcomes":{}},"unreachable":[]},
         "defaultInstructions":"@@INSTRUCTIONS@@","protocols":@@PROTOCOLS@@}
        """);

    /// <summary>On, with a key and models, a status of recent decisions (most fell back) and a caller that cannot reach a grade.</summary>
    public static readonly string Configured = Fill("""
        {"enabled":true,"virtualModel":"astra-auto",
         "judge":{"protocol":"typesafe","baseUrl":"https://openrouter.ai/api","effectiveBaseUrl":"https://openrouter.ai/api","hasApiKey":true,"apiKeyMasked":"sk-or-…7f3a","model":"~typesafe/jev-latest","timeoutMs":2000},
         "grades":[{"id":"simple","name":"Quick","description":"Quick questions, small edits, lookups","model":"gpt-5-mini","providerId":"prov_openai"},
                   {"id":"medium","name":"Everyday","description":"Everyday coding work and explanations","model":"claude-sonnet-4","providerId":"prov_anthropic"},
                   {"id":"hard","name":"Deep","description":"Architecture, hard debugging, long reasoning","model":"claude-opus-4","providerId":"prov_anthropic"}],
         "rules":{"mode":"session","sessionTtlMinutes":30,"fallbackGrade":"medium","minConfidence":0.6,"contextTurns":2,"maxChars":4000},
         "status":{"breakerOpen":false,"pins":3,
           "recent":{"total":12,"outcomes":{"judge":4,"timeout":6,"low_confidence":2},"medianJudgeMs":1450,"lastFallback":"timeout","lastFallbackAt":"2026-10-26T11:41:00Z"},
           "unreachable":[{"caller":"claude-code","grade":"hard","model":"claude-opus-4","usedGrade":"medium","count":5,"lastAt":"2026-10-26T11:30:00Z","reason":"token_limited","providerName":"Anthropic"}]},
         "defaultInstructions":"@@INSTRUCTIONS@@","protocols":@@PROTOCOLS@@}
        """);

    /// <summary>A judge at an address of its own, with a key saved for another one: the key is about to be dropped.</summary>
    public static readonly string CustomJudge = Fill("""
        {"enabled":false,"virtualModel":"astra-auto",
         "judge":{"protocol":"openai","baseUrl":"https://judge.internal.example/v1","effectiveBaseUrl":"https://judge.internal.example/v1","hasApiKey":true,"apiKeyMasked":"sk-…91c2","model":"gpt-5-mini","timeoutMs":3000},
         "grades":[{"id":"simple","name":"Quick","description":"Quick questions","model":"gpt-5-mini","providerId":"prov_openai"},
                   {"id":"hard","name":"Deep","description":"Hard problems","model":"gpt-5","providerId":"prov_openai"}],
         "rules":{"mode":"turn","sessionTtlMinutes":30,"fallbackGrade":"hard","minConfidence":0.7,"contextTurns":2,"maxChars":4000},
         "status":{"breakerOpen":true,"pins":0,"recent":{"total":0,"outcomes":{}},"unreachable":[]},
         "defaultInstructions":"@@INSTRUCTIONS@@","protocols":@@PROTOCOLS@@}
        """);

    public static FixtureSet Fixtures(string settings) => Scenes.Fixtures()
        .Override("/api/smart-routing", settings)
        .Override("/api/smart-routing/models", Models);

    /// <summary>The writes of the scenes: a test answers, a save is refused with the problems the web shows.</summary>
    public static HttpResponseMessage? Intercept(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post && path == "/api/smart-routing/test")
            return Scenes.JsonAnswer(HttpStatusCode.OK,
                """{"ok":true,"grade":"hard","gradeName":"Deep","model":"claude-opus-4","confidence":0.82,"confident":true,"judgeModel":"~typesafe/jev-latest","latencyMs":1180,"inputTokens":214,"outputTokens":3,"limitMs":2000,"newConnection":false}""");
        if (request.Method == HttpMethod.Put && path == "/api/smart-routing")
            return Scenes.JsonAnswer(HttpStatusCode.BadRequest,
                """{"error":"invalid smart routing settings","details":{"problems":[{"code":"judge_key_missing","message":"The judge key is missing."},{"code":"judge_model_missing","message":"The judge model is missing.","field":"judge.model"},{"code":"grade_model_missing","message":"Grade has no model.","field":"grades[0].model"},{"code":"grade_model_missing","message":"Grade has no model.","field":"grades[1].model"}]}}""");
        return null;
    }
}
