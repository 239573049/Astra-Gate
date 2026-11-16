using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Requests;
using Astra.Gateway.ClaudeBridge;
using Astra.Gateway.Pipeline;
using Astra.Server.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>Needs Node: the fake <c>claude</c> these tests run is a script.</summary>
public sealed class NodeFactAttribute : FactAttribute
{
    public NodeFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "the fake claude is a shebang script";
        else if (NodePath is null) Skip = "needs node on the PATH";
    }

    public static string? NodePath { get; } = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Select(d => Path.Combine(d, "node")).FirstOrDefault(File.Exists);
}

/// <summary>
/// The Claude bridge end to end (linked update 36) against a fake <c>claude</c> program — never a real subscription: a request of any caller protocol
/// is answered by the program's stream-json, the caller's tools round-trip through the MCP helper (this program's own subcommand) and
/// come back with the caller's next request, and a limit becomes the 429 the gateway's account failover knows.
/// </summary>
public class ClaudeBridgeTests
{
    /// <summary>
    /// A stand-in for <c>claude -p --input-format stream-json</c>: it echoes a message, calls the caller's <c>echo</c> tool through its MCP server
    /// on "CALL_TOOL" (the call is made after the reply that names it ended, as the real program does), and hits its limit on "LIMIT".
    /// </summary>
    private const string FakeClaude = """
        #!/usr/bin/env node
        const fs = require('fs'), path = require('path'), cp = require('child_process'), readline = require('readline');
        const args = process.argv.slice(2);
        const dir = path.dirname(process.argv[1]);
        const out = (o) => process.stdout.write(JSON.stringify(o) + '\n');
        if (args.includes('--version')) { console.log('2.1.999 (Claude Code)'); process.exit(0); }
        if (args[0] === 'auth') {
          console.log(JSON.stringify({ loggedIn: true, authMethod: 'claude.ai', email: 'me@example.com', subscriptionType: 'max' }));
          process.exit(0);
        }
        fs.appendFileSync(path.join(dir, 'runs.log'), JSON.stringify({ args, config: process.env.CLAUDE_CONFIG_DIR || null, key: process.env.ANTHROPIC_API_KEY || null }) + '\n');
        const stream = args.includes('stream-json');
        if (!stream) {
          let all = '';
          process.stdin.on('data', (d) => all += d);
          process.stdin.on('end', () => {
            fs.appendFileSync(path.join(dir, 'asks.log'), JSON.stringify({ args, said: all }) + '\n');
            if (all.includes('LIMIT')) out({ type: 'result', is_error: true, api_error_status: 429, result: "You've hit your limit" });
            else out({ type: 'result', is_error: false, result: 'hi' });
          });
          return;
        }
        const ev = (e) => out({ type: 'stream_event', event: e });
        let n = 0;
        const start = () => { n++; ev({ type: 'message_start', message: { id: 'msg_' + n, type: 'message', role: 'assistant', model: 'fake', content: [], stop_reason: null, usage: { input_tokens: 10, output_tokens: 0, cache_read_input_tokens: 100 } } }); };
        const text = (t, i) => { ev({ type: 'content_block_start', index: i, content_block: { type: 'text', text: '' } }); ev({ type: 'content_block_delta', index: i, delta: { type: 'text_delta', text: t } }); ev({ type: 'content_block_stop', index: i }); };
        const end = (r) => { ev({ type: 'message_delta', delta: { stop_reason: r, stop_sequence: null }, usage: { output_tokens: 5 } }); ev({ type: 'message_stop' }); };
        const done = () => out({ type: 'result', subtype: 'success', is_error: false, result: 'ok' });
        const cfg = JSON.parse(args[args.indexOf('--mcp-config') + 1]);
        let mcp = null;
        function client() {
          if (mcp) return mcp;
          const s = cfg.mcpServers.astra;
          const p = cp.spawn(s.command, s.args, { stdio: ['pipe', 'pipe', 'inherit'] });
          const waiting = {};
          let id = 0;
          readline.createInterface({ input: p.stdout }).on('line', (l) => { const m = JSON.parse(l); if (m.id && waiting[m.id]) waiting[m.id](m); });
          mcp = { req: (method, params) => new Promise((res) => { const i = ++id; waiting[i] = res; p.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: i, method, params }) + '\n'); }) };
          return mcp;
        }
        readline.createInterface({ input: process.stdin }).on('line', async (line) => {
          const m = JSON.parse(line);
          fs.appendFileSync(path.join(dir, 'inputs.log'), JSON.stringify(m.message.content) + '\n');
          const t = m.message.content.filter((b) => b.type === 'text').map((b) => b.text).join('\n');
          out({ type: 'system', subtype: 'init' });
          if (t.includes('LIMIT')) {
            out({ type: 'rate_limit_event', rate_limit_info: { status: 'rejected', resetsAt: 1900000000, rateLimitType: 'five_hour' } });
            out({ type: 'assistant', message: { model: '<synthetic>', content: [{ type: 'text', text: "You've hit your limit" }] } });
            out({ type: 'result', subtype: 'success', is_error: true, api_error_status: 429, result: "You've hit your limit" });
            return;
          }
          if (t.includes('CALL_TOOL')) {
            start(); text('Looking. ', 0);
            const id = 'toolu_' + n;
            ev({ type: 'content_block_start', index: 1, content_block: { type: 'tool_use', id, name: 'mcp__astra__echo', input: {} } });
            ev({ type: 'content_block_delta', index: 1, delta: { type: 'input_json_delta', partial_json: '{"text":' } });
            ev({ type: 'content_block_delta', index: 1, delta: { type: 'input_json_delta', partial_json: '"hi"}' } });
            ev({ type: 'content_block_stop', index: 1 });
            end('tool_use');
            const c = client();
            await c.req('initialize', {});
            const listed = await c.req('tools/list', {});
            fs.appendFileSync(path.join(dir, 'tools.log'), JSON.stringify(listed.result.tools) + '\n');
            const r = await c.req('tools/call', { name: 'echo', arguments: { text: 'hi' }, _meta: { 'claudecode/toolUseId': id } });
            start(); text('Tool said: ' + r.result.content.map((x) => x.text).join('') + (r.result.isError ? ' (error)' : ''), 0); end('end_turn'); done();
            return;
          }
          start(); text('echo: ' + t.slice(-60), 0); end('end_turn'); done();
        });
        """;

    private sealed class Fixture : IAsyncDisposable
    {
        public required GatewayFixture Gateway { get; init; }
        public required string Dir { get; init; }
        public required ProviderAccount Account { get; init; }

        public string[] Lines(string file) => File.Exists(Path.Combine(Dir, file)) ? File.ReadAllLines(Path.Combine(Dir, file)) : [];

        public async ValueTask DisposeAsync()
        {
            await Gateway.DisposeAsync();
            try
            {
                Directory.Delete(Dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>A Claude subscription provider (any client may use it) with one bridge account that runs the fake program.</summary>
    private static async Task<Fixture> StartAsync(string configDir = "")
    {
        var dir = Path.Combine(Path.GetTempPath(), "astra-fake-claude-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "claude");
        await File.WriteAllTextAsync(script, FakeClaude);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // the fake upstream is never reached: a Claude bridge account sends nothing to the network
        var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.Anthropic,
            _ => FakeUpstream.Json("""{"error":"must not be called"}""", HttpStatusCode.InternalServerError), authScheme: AuthSchemes.OAuthSubscription);
        gw.Provider.TemplateId = SubscriptionSupport.ClaudeSubscriptionKey;
        gw.Provider.Settings = JsonNode.Parse("""{"subscription_oauth":{"verified":true},"subscription":{"client_policy":"any","switch_mode":"failover"}}""")!.AsObject();
        await gw.Host.Db.Providers.UpdateAsync(gw.Provider);
        var account = new ProviderAccount
        {
            Id = "bridge-a", ProviderId = gw.Provider.Id, DisplayName = "claude", Kind = AccountKinds.ClaudeBridge, Status = AccountStatus.Active,
            Extra = new JsonObject { ["claude_bridge"] = new JsonObject { ["config_dir"] = configDir } },
        };
        await gw.Host.Db.Accounts.InsertAsync(account);
        var (status, _) = await gw.Host.SendAsync(HttpMethod.Put, "/api/claude-bridge/settings", new { binaryPath = script, maxRuns = 4, idleMinutes = 5 });
        Assert.Equal(HttpStatusCode.OK, status);
        return new Fixture { Gateway = gw, Dir = dir, Account = account };
    }

    private const string EchoTool = """{"type":"function","function":{"name":"echo","description":"Echoes","parameters":{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}}}""";

    private static string Chat(string messages, bool stream = false, bool tools = false) =>
        $$"""{"model":"claude-sonnet-5-5","stream":{{(stream ? "true" : "false")}},{{(tools ? "\"tools\":[" + EchoTool + "]," : "")}}"messages":{{messages}}}""";

    [NodeFact]
    public async Task A_Chat_Client_Is_Answered_By_The_Claude_Program_And_Nothing_Reaches_The_Network()
    {
        await using var f = await StartAsync();
        var response = await f.Gateway.PostAsync("/v1/chat/completions", Chat("""[{"role":"user","content":"say hello"}]"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.StartsWith("echo: ", body["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        Assert.Contains("say hello", body["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        Assert.True(body["usage"]!["prompt_tokens"]!.GetValue<int>() >= 10); // the usage the program printed rides through to the caller
        Assert.Empty(f.Gateway.Upstream.Requests);

        var record = await f.Gateway.RecordOfAsync(response);
        Assert.Equal(f.Account.Id, record.AccountId);
        Assert.Equal(RequestStatus.Success, record.Status);

        // the run is the real program with none of its tools, settings or session files
        var run = JsonNode.Parse(Assert.Single(f.Lines("runs.log")))!;
        var args = run["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        Assert.Contains("--no-session-persistence", args);
        Assert.Equal("", args[args.IndexOf("--tools") + 1]);
        Assert.Null(run["key"]); // no API key of the machine reaches the run
    }

    [NodeFact]
    public async Task A_Streamed_Reply_Comes_Back_As_The_Callers_Own_Stream()
    {
        await using var f = await StartAsync();
        var response = await f.Gateway.PostAsync("/v1/chat/completions", Chat("""[{"role":"user","content":"stream it"}]""", stream: true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("echo: ", text);
        Assert.Contains("[DONE]", text);
        Assert.Empty(f.Gateway.Upstream.Requests);
    }

    [NodeFact]
    public async Task The_Callers_Tool_Round_Trips_Through_The_Mcp_Helper_And_The_Next_Request()
    {
        await using var f = await StartAsync();
        // 1. the program calls the caller's tool: the reply ends in a tool call, the program is parked on it
        var first = await f.Gateway.PostAsync("/v1/chat/completions", Chat("""[{"role":"user","content":"CALL_TOOL please"}]""", tools: true));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = JsonNode.Parse(await first.Content.ReadAsStringAsync())!;
        var choice = firstBody["choices"]![0]!;
        Assert.Equal("tool_calls", choice["finish_reason"]!.GetValue<string>());
        var call = choice["message"]!["tool_calls"]![0]!;
        Assert.Equal("echo", call["function"]!["name"]!.GetValue<string>()); // the name the caller declared
        Assert.Equal("hi", JsonNode.Parse(call["function"]!["arguments"]!.GetValue<string>())!["text"]!.GetValue<string>());
        var callId = call["id"]!.GetValue<string>();
        Assert.StartsWith("toolu_", callId);

        // the helper listed the caller's tool, under the name claude calls it by
        for (var i = 0; i < 100 && f.Lines("tools.log").Length == 0; i++) await Task.Delay(50); // the program asks after its reply ended
        var tools = JsonNode.Parse(Assert.Single(f.Lines("tools.log")))!.AsArray();
        Assert.Equal("echo", tools[0]!["name"]!.GetValue<string>());
        Assert.Equal("string", tools[0]!["inputSchema"]!["properties"]!["text"]!["type"]!.GetValue<string>());

        // 2. the caller runs the tool and answers in its next request: the held MCP call gets the result, the same run goes on
        var second = await f.Gateway.PostAsync("/v1/chat/completions", Chat(
            $$$"""[{"role":"user","content":"CALL_TOOL please"},{"role":"assistant","content":"Looking. ","tool_calls":[{"id":"{{{callId}}}","type":"function","function":{"name":"echo","arguments":"{\"text\":\"hi\"}"}}]},{"role":"tool","tool_call_id":"{{{callId}}}","content":"pong"}]""",
            tools: true));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = JsonNode.Parse(await second.Content.ReadAsStringAsync())!;
        Assert.Equal("Tool said: pong", secondBody["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        Assert.Single(f.Lines("runs.log")); // one program for the whole exchange
        Assert.Single(f.Lines("inputs.log")); // told the conversation once; the result went through MCP, not as a message
        Assert.Empty(f.Gateway.Upstream.Requests);
    }

    [NodeFact]
    public async Task The_Next_Turn_Goes_On_In_The_Run_That_Answered_The_Conversation_Told_Only_What_Is_New()
    {
        await using var f = await StartAsync();
        var first = await f.Gateway.PostAsync("/v1/chat/completions", Chat("""[{"role":"user","content":"first question"}]"""));
        var reply = JsonNode.Parse(await first.Content.ReadAsStringAsync())!["choices"]![0]!["message"]!["content"]!.GetValue<string>();

        var replyJson = JsonValue.Create(reply)!.ToJsonString();
        var second = await f.Gateway.PostAsync("/v1/chat/completions", Chat(
            "[{\"role\":\"user\",\"content\":\"first question\"},{\"role\":\"assistant\",\"content\":" + replyJson + "},{\"role\":\"user\",\"content\":\"second question\"}]"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("second question", await second.Content.ReadAsStringAsync());

        Assert.Single(f.Lines("runs.log")); // the same process
        var inputs = f.Lines("inputs.log");
        Assert.Equal(2, inputs.Length);
        Assert.Contains("second question", inputs[1]);
        Assert.DoesNotContain("first question", inputs[1]); // only the new message
    }

    [NodeFact]
    public async Task A_Conversation_The_Caller_Rewrote_Starts_A_New_Run_Told_The_Whole_Of_It()
    {
        await using var f = await StartAsync();
        await f.Gateway.PostAsync("/v1/chat/completions", Chat("""[{"role":"user","content":"one"}]"""));
        var second = await f.Gateway.PostAsync("/v1/chat/completions", Chat(
            """[{"role":"user","content":"something else entirely"},{"role":"assistant","content":"ok"},{"role":"user","content":"two"}]"""));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, f.Lines("runs.log").Length);
        Assert.Contains("<conversation_history>", f.Lines("inputs.log")[1]);
        Assert.Contains("something else entirely", f.Lines("inputs.log")[1]);
    }

    [NodeFact]
    public async Task A_Limit_Is_A_429_With_The_Reset_The_Gateway_Cools_The_Account_By()
    {
        await using var f = await StartAsync();
        var response = await f.Gateway.PostAsync("/v1/chat/completions", Chat("""[{"role":"user","content":"LIMIT now"}]"""));
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains("hit your limit", await response.Content.ReadAsStringAsync());
        var account = (await f.Gateway.Host.Db.Accounts.GetAsync(f.Account.Id))!;
        Assert.NotNull(account.CooldownUntilUtc); // out of rotation until the program's reset
        Assert.True(account.CooldownUntilUtc > DateTimeOffset.UtcNow.AddDays(1)); // the reset the program printed, clamped by the gateway
    }

    [NodeFact]
    public async Task A_Missing_Claude_Program_Is_A_503_That_Says_So()
    {
        await using var f = await StartAsync();
        File.Delete(Path.Combine(f.Dir, "claude")); // the program the settings name is gone
        var response = await f.Gateway.PostAsync("/v1/chat/completions", Chat("""[{"role":"user","content":"hello"}]"""));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("claude", await response.Content.ReadAsStringAsync());
        var (_, status) = await f.Gateway.Host.SendAsync(HttpMethod.Get, "/api/claude-bridge");
        Assert.False(status!["found"]!.GetValue<bool>());
    }

    [NodeFact]
    public async Task A_Streamed_Tool_Call_Ends_The_Stream_As_A_Tool_Call_And_The_Run_Waits_For_The_Result()
    {
        await using var f = await StartAsync();
        var response = await f.Gateway.PostAsync("/v1/chat/completions", Chat("""[{"role":"user","content":"CALL_TOOL streamed"}]""", stream: true, tools: true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"name\":\"echo\"", text);
        Assert.Contains("tool_calls", text);
        Assert.Contains("[DONE]", text);
        var bridge = f.Gateway.Host.App.Services.GetRequiredService<ClaudeBridgeService>();
        Assert.Equal("parked", Assert.Single(bridge.Runs()).State); // waiting for the caller's tool results
    }

    [NodeFact]
    public async Task A_Warm_Up_Of_A_Bridge_Account_Asks_The_Program_One_Short_Question_And_Logs_It_As_A_Warm_Up()
    {
        await using var f = await StartAsync();
        var warmup = f.Gateway.Host.App.Services.GetRequiredService<WarmupService>();
        var run = await warmup.WarmNowAsync(f.Account.Id, default);
        Assert.Equal("ok", run.Result);

        var ask = JsonNode.Parse(Assert.Single(f.Lines("asks.log")))!;
        Assert.Equal("hi", ask["said"]!.GetValue<string>());
        var args = ask["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        Assert.Equal("haiku", args[args.IndexOf("--model") + 1]); // the cheapest there is
        Assert.Contains("--no-session-persistence", args);

        await f.Gateway.Host.App.Services.GetRequiredService<UsageWriter>().FlushAsync();
        var page = await f.Gateway.Host.Db.Requests.QueryAsync(new Astra.Data.Repositories.RequestQuery { ClientKind = ProviderProbe.WarmupClientKind });
        var row = Assert.Single(page.Items);
        Assert.Equal(f.Account.Id, row.AccountId);
        Assert.Equal(RequestStatus.Success, row.Status);
        Assert.Equal("Astra warm-up (claude)", row.UserAgent);
        Assert.Empty(f.Gateway.Upstream.Requests);
    }

    [NodeFact]
    public async Task A_One_Off_Ask_That_Hits_The_Limit_Is_A_429_Error_Not_A_Crash()
    {
        await using var f = await StartAsync();
        var bridge = f.Gateway.Host.App.Services.GetRequiredService<ClaudeBridgeService>();
        var e = await Assert.ThrowsAsync<ClaudeBridgeException>(() => bridge.AskAsync(new ClaudeTarget("", null, null), "haiku", "LIMIT", default));
        Assert.Equal(429, e.Status);
    }

    [NodeFact]
    public async Task The_Account_Runs_The_Program_In_Its_Own_Config_Directory()
    {
        await using var f = await StartAsync("/data/work-account");
        await f.Gateway.PostAsync("/v1/chat/completions", Chat("""[{"role":"user","content":"hello"}]"""));
        Assert.Equal("/data/work-account", JsonNode.Parse(Assert.Single(f.Lines("runs.log")))!["config"]!.GetValue<string>());

        var (status, signIn) = await f.Gateway.Host.SendAsync(HttpMethod.Get, $"/api/claude-bridge/accounts/{f.Account.Id}/signin");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(signIn!["loggedIn"]!.GetValue<bool>());
        Assert.Equal("me@example.com", signIn["email"]!.GetValue<string>());
    }
}
