using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Astra.Gateway.ClaudeBridge;

/// <summary>
/// The small stdio MCP server a <c>claude</c> run starts for the caller's tools (magpie's <c>claude-mcp-helper</c>, MIT): the genuine
/// <c>claude</c> launches it, and a tool call is handed back to the gateway, which holds it until the caller returns the tool's result in its
/// next request. It implements only the MCP methods Claude Code needs: <c>initialize</c>, <c>tools/list</c> and <c>tools/call</c>.
/// </summary>
public static class ClaudeMcpHelper
{
    /// <summary>The hidden subcommand of <c>astra-server</c> that runs the helper (<c>claude-mcp-helper &lt;callback file&gt; &lt;tools file&gt;</c>).</summary>
    public const string Command = "claude-mcp-helper";

    /// <param name="args">The callback address (or the file that holds it, which keeps its token off the command line) and the tools file.</param>
    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, CancellationToken ct)
    {
        if (args.Length != 2)
        {
            await Console.Error.WriteLineAsync("claude-mcp-helper expects a callback address (or its file) and a tools file");
            return 2;
        }
        var callback = args[0];
        if (!callback.StartsWith("http://", StringComparison.Ordinal) && !callback.StartsWith("https://", StringComparison.Ordinal))
            callback = (await File.ReadAllTextAsync(callback, ct)).Trim();
        var tools = JsonNode.Parse(await File.ReadAllTextAsync(args[1], ct)) as JsonArray ?? [];

        // The callback is the gateway on this computer, never through a proxy: claude hands down the *_PROXY it was given for its vendor.
        using var handler = new SocketsHttpHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var write = new SemaphoreSlim(1, 1);
        async Task RespondAsync(JsonObject message)
        {
            await write.WaitAsync(ct);
            try
            {
                await output.WriteLineAsync(message.ToJsonString());
                await output.FlushAsync(ct);
            }
            finally
            {
                write.Release();
            }
        }

        var pending = new List<Task>();
        string? line;
        while ((line = await input.ReadLineAsync(ct)) is not null)
        {
            JsonObject? request;
            try
            {
                request = JsonNode.Parse(line) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }
            // notifications have no id and must not be answered
            if (request is null || request["id"] is null) continue;
            // a tools/call blocks until the caller returns its result: each call is its own task
            pending.Add(Task.Run(async () => await RespondAsync(await AnswerAsync(request, tools, client, callback, ct)), ct));
            pending.RemoveAll(t => t.IsCompleted);
        }
        try
        {
            await Task.WhenAll(pending);
        }
        catch (OperationCanceledException)
        {
        }
        return 0;
    }

    /// <summary>One request's response. Public so the protocol can be tested without a process.</summary>
    public static async Task<JsonObject> AnswerAsync(JsonObject request, JsonArray tools, HttpClient client, string callback, CancellationToken ct)
    {
        var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]?.DeepClone() };
        switch (request["method"]?.GetValue<string>())
        {
            case "initialize":
                response["result"] = new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = ClaudeNames.ServerName, ["version"] = "1" },
                };
                break;
            case "tools/list":
                response["result"] = new JsonObject { ["tools"] = tools.DeepClone() };
                break;
            case "tools/call":
                try
                {
                    var p = request["params"] as JsonObject;
                    // claude names the call it is making; a client that does not gets an id of the helper's own
                    var id = p?["_meta"]?["claudecode/toolUseId"]?.GetValue<string>() ?? "call_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
                    var payload = new JsonObject { ["tool_call_id"] = id, ["name"] = p?["name"]?.DeepClone(), ["arguments"] = p?["arguments"]?.DeepClone() ?? new JsonObject() };
                    using var message = new HttpRequestMessage(HttpMethod.Post, callback)
                    {
                        Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
                    };
                    using var reply = await client.SendAsync(message, ct);
                    var body = await reply.Content.ReadAsStringAsync(ct);
                    if (!reply.IsSuccessStatusCode)
                    {
                        response["error"] = new JsonObject { ["code"] = -32000, ["message"] = body };
                        break;
                    }
                    var answer = JsonNode.Parse(body) as JsonObject ?? [];
                    response["result"] = new JsonObject { ["content"] = answer["content"]?.DeepClone() ?? new JsonArray(), ["isError"] = answer["is_error"]?.GetValue<bool>() ?? false };
                }
                catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
                {
                    response["error"] = new JsonObject { ["code"] = -32000, ["message"] = e.Message };
                }
                break;
            default:
                response["error"] = new JsonObject { ["code"] = -32601, ["message"] = "method not found" };
                break;
        }
        return response;
    }
}
