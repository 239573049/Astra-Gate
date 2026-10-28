using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Core.Models;
using Astra.Data;
using Astra.Plugins.Host;

namespace Astra.Server.Api;








/// <summary>
/// Signing in to a plugin's provider (magpie's StartPluginSignIn / pluginsignin.go): the plugin's methods and their
/// questions, a browser sign-in the plugin finishes on its own or with a pasted code, a key, signing out, and each
/// account's allowance as the plugin tells it. Every finished sign-in syncs the provider's accounts.
/// </summary>
public sealed partial class PluginSignInService(AstraDatabase db, PluginProviders providers, PluginProviderSync sync)
{
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, Flow> _flows = new();

    public async Task<List<PluginAuthMethodDto>> MethodsAsync(string providerId, CancellationToken ct)
    {
        var pp = await PluginProviderAsync(providerId, ct);
        return pp.Methods.Select((m, i) => new PluginAuthMethodDto(i, m.Type, m.Label, m.Placeholder)).ToList();
    }

    public async Task<JsonObject> PromptAsync(string providerId, PluginPromptInput body, CancellationToken ct)
    {
        var pp = await PluginProviderAsync(providerId, ct);
        var result = await Host("prompt", new JsonObject { ["provider"] = pp.Id, ["method"] = body.Method, ["inputs"] = Inputs(body.Inputs) }, ct);
        return result as JsonObject ?? new JsonObject { ["prompt"] = null };
    }

    public async Task<JsonObject> ValidateAsync(string providerId, PluginValidateInput body, CancellationToken ct)
    {
        var pp = await PluginProviderAsync(providerId, ct);
        var result = await Host("validate", new JsonObject
        {
            ["provider"] = pp.Id, ["method"] = body.Method, ["key"] = body.Key ?? "", ["value"] = body.Value ?? "",
        }, ct);
        return result as JsonObject ?? new JsonObject { ["error"] = null };
    }

    /// <summary>Starts a browser sign-in: the page to open; the plugin then finishes it, or waits for the pasted code.</summary>
    public async Task<PluginSignInDto> StartAsync(string providerId, PluginSignInInput body, CancellationToken ct)
    {
        var pp = await PluginProviderAsync(providerId, ct);
        var account = body.Account is { Length: > 0 } id ? await KeyOfAsync(providerId, id, ct) : "new";
        var a = await Host("authorize", new JsonObject
        {
            ["provider"] = pp.Id, ["method"] = body.Method, ["inputs"] = Inputs(body.Inputs), ["account"] = account,
        }, ct) as JsonObject ?? throw new AdminApiException(502, $"{pp.Name}'s plugin started no sign-in");
        var instructions = PluginProviders.Str(a, "instructions");
        var flow = new Flow(Guid.NewGuid().ToString("N")[..16], pp.Id, a["session"]?.DeepClone())
        {
            Url = PluginProviders.Str(a, "url"),
            Instructions = instructions,
            PasteCode = PluginProviders.Str(a, "method") == "code",
            DeviceCode = instructions is null ? null : DeviceCode().Match(instructions) is { Success: true } m ? m.Groups[1].Value : null,
        };
        foreach (var (key, old) in _flows)
            if (old.Provider == pp.Id && old.State == "waiting") _flows.TryRemove(key, out _);
        _flows[flow.Id] = flow;
        if (!flow.PasteCode)
        {
            // the plugin notices the sign-in on its own (a loopback page, a device poll): wait for it
            _ = Task.Run(async () =>
            {
                using var wait = new CancellationTokenSource(SignInTimeout);
                await FinishAsync(flow, null, wait.Token);
            });
        }
        return flow.Dto();
    }

    public PluginSignInDto Status(string flowId) =>
        _flows.TryGetValue(flowId, out var flow) ? flow.Dto() : throw new AdminApiException(404, "This sign-in is over; start it again");

    /// <summary>Finishes a sign-in with the code its page showed.</summary>
    public async Task<PluginSignInDto> SubmitCodeAsync(string flowId, string? code, CancellationToken ct)
    {
        if (!_flows.TryGetValue(flowId, out var flow) || flow.State != "waiting" || !flow.PasteCode)
            throw new AdminApiException(409, "This sign-in is over; start it again");
        if (string.IsNullOrWhiteSpace(code)) throw new AdminApiException(400, "Paste the code the sign-in page showed");
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromMinutes(2));
        await FinishAsync(flow, code.Trim(), wait.Token);
        return flow.Dto();
    }

    /// <summary>Signs in with a key, as the provider's "api" method does.</summary>
    public async Task<JsonObject> ApiKeyAsync(string providerId, PluginApiKeyInput body, CancellationToken ct)
    {
        var pp = await PluginProviderAsync(providerId, ct);
        if (string.IsNullOrWhiteSpace(body.Key)) throw new AdminApiException(400, "Enter the key");
        var saved = await Host("apiKey", new JsonObject
        {
            ["provider"] = pp.Id, ["method"] = body.Method, ["inputs"] = Inputs(body.Inputs), ["key"] = body.Key.Trim(), ["account"] = "new",
        }, ct) as JsonObject;
        if (saved?["ok"] is not JsonValue ok || !ok.GetValue<bool>())
            throw new AdminApiException(400, PluginProviders.Str(saved, "error") ?? $"{pp.Name} didn't take the key");
        await sync.SyncAsync(CancellationToken.None);
        return new JsonObject { ["account"] = await AccountIdOfAsync(providerId, PluginProviders.Str(saved, "account"), ct) };
    }

    /// <summary>Signs the account out of the plugin (plugin-auth.json forgets it) and removes it.</summary>
    public async Task SignOutAsync(string providerId, string accountId, CancellationToken ct)
    {
        var pp = await PluginProviderAsync(providerId, ct);
        var key = await KeyOfAsync(providerId, accountId, ct);
        await Host("signOut", new JsonObject { ["provider"] = pp.Id, ["account"] = key }, ct);
        await db.Accounts.DeleteAsync(accountId, ct);
        await sync.SyncAsync(CancellationToken.None);
    }

    /// <summary>The account's allowance as the plugin tells it: plan, windows used and when they reset.</summary>
    public async Task<JsonObject> UsageAsync(string providerId, string accountId, CancellationToken ct)
    {
        var pp = await PluginProviderAsync(providerId, ct);
        if (!pp.Usage) throw new AdminApiException(409, $"{pp.Name}'s plugin tells no allowance");
        var (key, proxy) = await AccountOfAsync(providerId, accountId, ct);
        var p = new JsonObject { ["provider"] = pp.Id, ["account"] = key };
        if (proxy is not null) p["proxy"] = proxy;
        return await Host("usage", p, ct) as JsonObject ?? new JsonObject();
    }

    private async Task FinishAsync(Flow flow, string? code, CancellationToken ct)
    {
        try
        {
            var p = new JsonObject { ["session"] = flow.Session?.DeepClone() };
            if (code is not null) p["code"] = code;
            var done = await Host("callback", p, ct) as JsonObject;
            if (done?["ok"] is JsonValue ok && ok.GetValue<bool>())
            {
                await sync.SyncAsync(CancellationToken.None);
                flow.Account = await AccountIdOfAsync(PluginProviderSync.RowId(flow.Provider), PluginProviders.Str(done, "account"), CancellationToken.None);
                flow.State = "done";
            }
            else
            {
                flow.Error = PluginProviders.Str(done, "error") ?? "the sign-in didn't go through";
                flow.State = "failed";
            }
        }
        catch (OperationCanceledException)
        {
            flow.Error = "the sign-in timed out; start it again";
            flow.State = "failed";
        }
        catch (AdminApiException e)
        {
            flow.Error = e.Message;
            flow.State = "failed";
        }
    }

    private async Task<JsonNode?> Host(string method, JsonObject parameters, CancellationToken ct)
    {
        try
        {
            return await providers.Host.CallAsync(method, parameters, ct);
        }
        catch (PluginHostException e)
        {
            throw new AdminApiException(502, e.Message);
        }
    }

    private async Task<PluginProviderInfo> PluginProviderAsync(string providerId, CancellationToken ct)
    {
        var row = await db.Providers.GetAsync(providerId, ct) ?? throw new AdminApiException(404, "Provider not found");
        if (row.AuthScheme != AuthSchemes.Plugin) throw new AdminApiException(409, "This provider isn't a plugin's");
        var id = Gateway.Pipeline.GatewayPipeline.PluginProviderOf(row);
        List<PluginProviderInfo> list;
        try
        {
            list = await providers.ListAsync(ct: ct);
        }
        catch (PluginHostException e)
        {
            throw new AdminApiException(502, e.Message);
        }
        return list.FirstOrDefault(p => p.Id == id)
               ?? throw new AdminApiException(409, $"No plugin serves {row.Name} now: is its plugin installed and switched on?");
    }

    private async Task<string> KeyOfAsync(string providerId, string accountId, CancellationToken ct) => (await AccountOfAsync(providerId, accountId, ct)).Key;

    private async Task<(string Key, string? Proxy)> AccountOfAsync(string providerId, string accountId, CancellationToken ct)
    {
        var account = await db.Accounts.GetAsync(accountId, ct);
        if (account is null || account.ProviderId != providerId || PluginProviderSync.PluginKeyOf(account) is not { } key)
            throw new AdminApiException(404, "Account not found");
        return (key, account.HttpProxy);
    }

    private async Task<string?> AccountIdOfAsync(string providerId, string? key, CancellationToken ct) =>
        key is null ? null : (await db.Accounts.ListAsync(providerId, ct)).FirstOrDefault(a => PluginProviderSync.PluginKeyOf(a) == key)?.Id;

    private static JsonObject Inputs(JsonObject? inputs)
    {
        var o = new JsonObject();
        foreach (var (k, v) in inputs ?? []) o[k] = v?.ToString() ?? "";
        return o;
    }

    // The code a device sign-in's instructions ask to confirm on the vendor's page ("Confirm the code ABCD-EFGH").
    [GeneratedRegex(@"(?i:\bcode):?\s+([A-Z0-9]+(?:-[A-Z0-9]+)+|[A-Z0-9]{6,})\b")]
    private static partial Regex DeviceCode();

    private sealed class Flow(string id, string provider, JsonNode? session)
    {
        public string Id { get; } = id;
        public string Provider { get; } = provider;
        public JsonNode? Session { get; } = session;
        public string? Url { get; init; }
        public string? Instructions { get; init; }
        public bool PasteCode { get; init; }
        public string? DeviceCode { get; init; }
        public volatile string State = "waiting";
        public string? Account { get; set; }
        public string? Error { get; set; }

        public PluginSignInDto Dto() => new(Id, State, Url, Instructions, PasteCode, DeviceCode, Account, Error);
    }
}
