using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Clients;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Pages.Clients;
using Astra.Desktop.Platform;
using Astra.Desktop.Shell;
using Astra.Desktop.Theme;
using Astra.Desktop.Views;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Astra.Desktop.UiTests;

/// <summary>
/// Scenes of the Clients page (plan avalonia-page-inventory §5.4): the list with a detail in each client state (not detected, detected,
/// enabled with an update, drifted, needs repair, Claude Desktop's role map, options + protocol overrides, Claude Code's direct panel), the
/// enable preview (diff), the disable confirmation, the backups and install sheets, and the empty / error / loading list. The default fixture
/// set holds four plain clients; the richer list is built here from it (<see cref="ClientsRichJson"/>), and the per-client endpoints the
/// fixture set does not route (models, preview, backups, install job, Claude direct) are answered by <see cref="ClientsHandler"/>.
/// </summary>
public static partial class Scenes
{
    static partial void AddClients(Dictionary<string, Func<Scene>> d)
    {
        d["clients__data"] = () => ClientsScene(Fixtures());
        d["clients__empty"] = () => ClientsScene(Fixtures().Override("/api/clients", "[]"));
        d["clients__error"] = () => ClientsScene(Fixtures().Override("/api/clients", "{\"error\":\"boom\"}", 500));
        d["clients__loading"] = () => ClientsScene(Fixtures(), hang: "/api/clients", settle: false);

        d["clients__not-detected"] = () => ClientsScene(Rich(), "gemini-cli");
        d["clients__detected"] = () => ClientsScene(Rich(), "qwen-code");
        d["clients__enabled"] = () => ClientsScene(Rich(), "codex");
        d["clients__drifted"] = () => ClientsScene(Rich(), "opencode");
        d["clients__needs-repair"] = () => ClientsScene(Rich(), "crush");
        d["clients__desktop-roles"] = () => ClientsScene(Rich(), "claude-desktop");
        d["clients__options"] = () => ClientsScene(Rich(), "zed", vm => ClientsReveal(vm, "clients.protocols.title"));
        d["clients__claude-direct"] = () => ClientsScene(Rich(), "claude-code", vm => ClientsReveal(vm, "clients.claudeDirect.statsTitle"));

        d["clients__enable-preview"] = () => ClientsScene(Rich(), "qwen-code", vm => vm.BeginEnable(vm.Selected!));
        d["clients__disable"] = () => ClientsScene(Rich(), "codex", vm => _ = vm.DisableAsync(vm.Selected!), waitAfter: false);
        d["clients__backups"] = () => ClientsScene(Rich(), "codex", vm => vm.BeginBackups(vm.Selected!));
        d["clients__install-confirm"] = () => ClientsScene(Rich(), "gemini-cli", vm => vm.BeginInstall(vm.Selected!, InstallSheetMode.Install));
        d["clients__install-running"] = () => ClientsScene(Rich(busy: true), "gemini-cli", vm => vm.BeginInstall(vm.Selected!, InstallSheetMode.Log));
        // Codex is enabled, so the confirm step says its original config is restored before the npm uninstall runs.
        d["clients__uninstall-confirm"] = () => ClientsScene(Rich(), "codex", vm => vm.BeginInstall(vm.Selected!, InstallSheetMode.Uninstall));
        d["clients__uninstall-running"] = () => ClientsScene(Rich(uninstalling: true), "codex", vm => vm.BeginInstall(vm.Selected!, InstallSheetMode.Log));
    }

    // Scrolls the page so the group titled <titleKey> (below the fold of the 900 px window) is in view.
    private static void ClientsReveal(ClientsPageViewModel vm, string titleKey)
    {
        var title = Astra.Desktop.Core.I18n.Loc.T(titleKey);
        var group = vm.Panel!.GetVisualDescendants().OfType<Astra.Desktop.Components.Kit.SettingsGroup>().LastOrDefault(g => g.Title == title);
        group?.BringIntoView();
    }

    private static FixtureSet Rich(bool busy = false, bool uninstalling = false) => Fixtures().Override("/api/clients", ClientsRichJson(busy, uninstalling));

    /// <summary>
    /// The Clients page over <paramref name="fixtures"/> with the per-client endpoints answered, real toasts / confirm dialogs, optionally a
    /// path that never answers, and a <paramref name="drive"/> step (open a sheet) run once the page settled.
    /// </summary>
    private static Scene ClientsScene(FixtureSet fixtures, string? kind = null, Action<ClientsPageViewModel>? drive = null, string? hang = null, bool settle = true, bool waitAfter = true)
    {
        HttpMessageHandler handler = new ClientsHandler(fixtures.Handler(), fixtures);
        if (hang is not null) handler = new PathHangingHandler(handler, hang);
        var api = FixtureApi(handler);
        ShellView? view = null;
        var context = PageContext.Create(api, AvaloniaUiDispatcher.Instance, feedback: new ArcFeedback(() => view), clock: new FixedClock(fixtures.Now));
        var connection = new ConnectionViewModel(null, api, context.Queries);
        connection.AssumeConnected("http://127.0.0.1:17321");
        var shell = new ShellViewModel(DesktopPrefs.InMemory(), connection, context, AppModules.All);
        shell.Navigator.Navigate(AppRoute.Clients, kind);
        view = new ShellView { DataContext = shell };
        if (!settle) return new Scene(view);
        return new Scene(view, async () =>
        {
            await context.Queries.WhenIdleAsync();
            var vm = (ClientsPageViewModel)shell.Navigator.Current;
            await vm.WhenSettledAsync();
            if (drive is null) return;
            drive(vm);
            if (!waitAfter)
            {
                await Task.Delay(150);
                return;
            }
            await vm.WhenSettledAsync();
            await Task.Delay(150);
        });
    }

    /// <summary>Answers the Clients endpoints the default fixture set has no route for; everything else goes to the fixtures.</summary>
    private sealed class ClientsHandler(HttpMessageHandler inner, FixtureSet fixtures) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var post = request.Method == HttpMethod.Post;
            string? body = null;
            var status = 200;
            if (post && path == "/api/clients/check-updates")
                body = fixtures.Overrides.TryGetValue("/api/clients", out var o) ? o.Body : null;
            else if (path == "/api/clients/claude-code/direct") body = ClientsDirectJson();
            else if (path.StartsWith("/api/clients/claude-code/direct/accounts/", StringComparison.Ordinal)) body = ClientsDirectRequestsJson();
            else if (path.EndsWith("/models", StringComparison.Ordinal) && path.StartsWith("/api/clients/", StringComparison.Ordinal))
                body = "[\"gpt-5\",\"gpt-4.1\",\"gpt-4.1-mini\",\"o4-mini\",\"claude-sonnet-4-5\"]";
            else if (post && path.EndsWith("/preview-enable", StringComparison.Ordinal)) body = ClientsPreviewJson();
            else if (path.EndsWith("/backups", StringComparison.Ordinal)) body = ClientsBackupsJson();
            else if (!post && path.EndsWith("/install", StringComparison.Ordinal))
            {
                var busy = fixtures.Overrides.TryGetValue("/api/clients", out var c) && c.Body.Contains("\"busy\":true", StringComparison.Ordinal);
                (status, body) = busy ? (200, path.Contains("/codex/", StringComparison.Ordinal) ? ClientsUninstallJobJson() : ClientsJobJson()) : (404, "{\"error\":\"No install job\"}");
            }
            if (body is null) return await base.SendAsync(request, cancellationToken);
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>
    /// The default four clients plus richer ones: Codex with an npm install and an update, Gemini CLI installable (or busy), Qwen Code detected
    /// and bound but not enabled (Codex can be uninstalled with npm, or is being uninstalled), Crush with unreadable config, Claude Desktop without a role map, Zed with options and several protocols.
    /// </summary>
    private static string ClientsRichJson(bool busy, bool uninstalling = false)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ui", "default", "clients.json");
        var list = (JsonArray)JsonNode.Parse(File.ReadAllText(path))!;
        JsonObject Find(string kind) => list.OfType<JsonObject>().First(c => (string?)c["kind"] == kind);

        JsonObject Install(string method, bool installed, string? version, string? latest, bool update, bool isBusy = false) => new()
        {
            ["method"] = method, ["package"] = method == "npm" ? "@openai/codex" : null, ["homepageUrl"] = "https://example.com", ["installed"] = installed,
            ["executable"] = installed ? "/usr/local/bin/tool" : null, ["version"] = version, ["latestVersion"] = latest,
            ["latestCheckedAt"] = "2026-10-26T11:00:00Z", ["latestError"] = null, ["updateAvailable"] = update,
            ["installCommand"] = installed ? null : "npm install -g @google/gemini-cli", ["updateCommand"] = update ? "npm install -g @openai/codex@latest" : null,
            ["updateVia"] = "npm", ["blocker"] = null, ["busy"] = isBusy,
        };

        var codex = Install("npm", true, "1.4.2", "1.5.0", true, uninstalling);
        codex["uninstallCommand"] = "npm uninstall -g @openai/codex";
        Find("codex")["install"] = codex;
        var gemini = Find("gemini-cli");
        gemini["install"] = Install("npm", false, null, "0.9.1", false, busy);
        ((JsonObject)gemini["install"]!)["package"] = "@google/gemini-cli";

        JsonObject Client(string kind, string name, string protocol, bool installed, string? provider, bool enabled = false, string status = "disabled") => new()
        {
            ["kind"] = kind, ["name"] = name, ["protocol"] = protocol, ["availability"] = "available", ["availabilityReason"] = null, ["mode"] = "switch",
            ["detection"] = new JsonObject { ["installed"] = installed, ["configExists"] = installed, ["version"] = installed ? "0.8.0" : null, ["configPaths"] = new JsonArray("~/." + kind + "/settings.json") },
            ["status"] = status, ["enabled"] = enabled, ["providerId"] = provider, ["accountId"] = null, ["selectedModel"] = null, ["extras"] = new JsonObject(),
            ["bindings"] = provider is null ? null : new JsonArray(new JsonObject { ["providerId"] = provider, ["accountId"] = null }),
            ["appliedAt"] = enabled ? "2026-10-20T08:30:00Z" : null, ["warnings"] = new JsonArray(), ["requiresRestart"] = false, ["configOutdated"] = false,
            ["tokenId"] = "tok_default", ["install"] = null, ["options"] = null, ["repairs"] = null, ["protocols"] = null,
        };

        list.Add(Client("qwen-code", "Qwen Code", "openai-chat", true, "prov_openai"));
        var crush = Client("crush", "Crush", "openai-chat", true, "prov_openai", enabled: true, status: "enabled");
        crush["repairs"] = new JsonArray(new JsonObject { ["file"] = "~/.crush/crush.json", ["problem"] = "trailing-comma", ["line"] = 12 });
        crush["warnings"] = new JsonArray("Crush 需要重启后才会读取新配置");
        list.Add(crush);
        list.Add(Client("claude-desktop", "Claude Desktop", "anthropic", true, "prov_anthropic"));
        var zed = Client("zed", "Zed", "openai-chat", true, "prov_openai", enabled: true, status: "enabled");
        zed["options"] = new JsonArray(
            new JsonObject { ["key"] = "contextWindow", ["type"] = "int", ["default"] = "", ["scope"] = "config" },
            new JsonObject { ["key"] = "cacheTtl", ["type"] = "enum", ["default"] = "default", ["scope"] = "gateway", ["choices"] = new JsonArray("default", "5m", "1h") },
            new JsonObject { ["key"] = "disablePromptCaching", ["type"] = "bool", ["default"] = "false", ["scope"] = "gateway" });
        zed["protocols"] = new JsonArray("openai-chat", "anthropic");
        zed["extras"] = new JsonObject
        {
            ["options"] = new JsonObject { ["contextWindow"] = 200000, ["cacheTtl"] = "1h" },
            ["protocolOverrides"] = new JsonObject { ["claude-sonnet-4-5"] = "anthropic" },
            ["models"] = new JsonObject
            {
                ["gpt-5"] = new JsonObject { ["id"] = "gpt-5", ["name"] = "GPT-5", ["protocols"] = new JsonArray("openai-chat", "openai-responses") },
                ["claude-sonnet-4-5"] = new JsonObject { ["id"] = "claude-sonnet-4-5", ["name"] = "Claude Sonnet 4.5", ["protocols"] = new JsonArray("anthropic") },
                ["gemini-2.5-pro"] = new JsonObject { ["id"] = "gemini-2.5-pro", ["name"] = "Gemini 2.5 Pro", ["protocols"] = new JsonArray("gemini") },
            },
        };
        list.Add(zed);
        return list.ToJsonString();
    }

    private static string ClientsDirectJson() => new JsonObject
    {
        ["mode"] = "direct",
        ["accountId"] = "acc_work",
        ["accounts"] = new JsonArray(
            new JsonObject { ["id"] = "acc_work", ["name"] = "Work", ["email"] = "me@example.com", ["plan"] = "Max", ["status"] = "active", ["enabled"] = true, ["telemetryEnabled"] = true },
            new JsonObject { ["id"] = "acc_old", ["name"] = "Old", ["email"] = null, ["plan"] = "Pro", ["status"] = "expired", ["enabled"] = true, ["telemetryEnabled"] = false }),
    }.ToJsonString();

    private static string ClientsDirectRequestsJson()
    {
        JsonObject Row(string id, string model, string at, string status, long input, long output, long cache, long? ms, long? cost) => new()
        {
            ["id"] = id, ["profileId"] = "p1", ["sessionId"] = "s1", ["accountUuid"] = null, ["model"] = model, ["occurredAtUtc"] = at, ["status"] = status,
            ["inputTokens"] = input, ["outputTokens"] = output, ["cacheReadTokens"] = cache, ["cacheCreationTokens"] = 0, ["durationMs"] = ms, ["estimatedCostNanoUsd"] = cost,
        };
        return new JsonArray(
            Row("r1", "claude-sonnet-4-5", "2026-10-26T11:40:00Z", "success", 1200, 340, 18000, 4200, 12_400_000),
            Row("r2", "claude-opus-4-1", "2026-10-26T11:20:00Z", "error", 800, 0, 0, 900, null),
            Row("r3", "claude-haiku-4-5", "2026-10-26T10:55:00Z", "success", 400, 120, 2000, 1300, 800_000)).ToJsonString();
    }

    private static string ClientsPreviewJson() => new JsonObject
    {
        ["changes"] = new JsonArray(new JsonObject { ["file"] = "~/.qwen-code/settings.json", ["format"] = "json", ["keyPath"] = "baseUrl", ["before"] = null, ["after"] = "http://127.0.0.1:17321/v1" }),
        ["diffs"] = new JsonArray(new JsonObject
        {
            ["file"] = "~/.qwen-code/settings.json",
            ["unifiedDiff"] = "--- a/~/.qwen-code/settings.json\n+++ b/~/.qwen-code/settings.json\n@@ -1,4 +1,6 @@\n {\n   \"theme\": \"dark\",\n-  \"model\": \"qwen3-coder\"\n+  \"model\": \"gpt-5\",\n+  \"baseUrl\": \"http://127.0.0.1:17321/v1\",\n+  \"apiKey\": \"sk-astra-…qwen-code\"\n }\n",
        }),
        ["warnings"] = new JsonArray("Qwen Code 正在运行，重启后生效"),
    }.ToJsonString();

    private static string ClientsBackupsJson() => new JsonArray(
        new JsonObject { ["id"] = "b3", ["createdAt"] = "2026-10-25T09:12:00Z", ["files"] = new JsonArray("~/.codex/config.json"), ["firstWrite"] = false },
        new JsonObject { ["id"] = "b2", ["createdAt"] = "2026-10-21T18:40:00Z", ["files"] = new JsonArray("~/.codex/config.json", "~/.codex/auth.json"), ["firstWrite"] = false },
        new JsonObject { ["id"] = "b1", ["createdAt"] = "2026-10-01T08:00:00Z", ["files"] = new JsonArray("~/.codex/config.json"), ["firstWrite"] = true }).ToJsonString();

    private static string ClientsUninstallJobJson() => new JsonObject
    {
        ["kind"] = "codex", ["action"] = "uninstall", ["state"] = "running", ["command"] = "npm uninstall -g @openai/codex",
        ["startedAt"] = "2026-10-26T11:59:30Z", ["finishedAt"] = null, ["exitCode"] = null,
        ["log"] = new JsonArray("已恢复 Codex 的原始配置，Astra 不再接管它。", "", "$ npm uninstall -g @openai/codex", "removed 1 package in 820ms"),
        ["hint"] = null, ["elevated"] = false,
    }.ToJsonString();

    private static string ClientsJobJson() => new JsonObject
    {
        ["kind"] = "gemini-cli", ["action"] = "install", ["state"] = "running", ["command"] = "npm install -g @google/gemini-cli",
        ["startedAt"] = "2026-10-26T11:59:30Z", ["finishedAt"] = null, ["exitCode"] = null,
        ["log"] = new JsonArray("$ npm install -g @google/gemini-cli", "npm http fetch GET 200 https://registry.npmjs.org/@google%2fgemini-cli 210ms", "added 412 packages in 18s"),
        ["hint"] = null, ["elevated"] = false,
    }.ToJsonString();
}
