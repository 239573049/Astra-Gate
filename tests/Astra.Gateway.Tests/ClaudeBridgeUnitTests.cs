using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Gateway.ClaudeBridge;

namespace Astra.Gateway.Tests;

/// <summary>The Claude bridge's pure parts (linked update 36): tool names, the request read, the prompt a run is told, failures and the helper's protocol.</summary>
public class ClaudeBridgeUnitTests
{
    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    // ---------------------------------------------------------------- names

    [Theory]
    [InlineData("read_file")]
    [InlineData("Bash")]
    [InlineData("mcp__linear__list-issues")]
    public void A_Name_Claude_Would_Keep_Stays(string name) => Assert.Equal(name, ClaudeNames.BridgeName(name));

    [Fact]
    public void A_Changed_Name_Is_Made_Of_Valid_Characters_And_A_Hash_Of_The_Whole_Name()
    {
        var dotted = ClaudeNames.BridgeName("a.b");
        var underscored = ClaudeNames.BridgeName("a_b");
        Assert.Equal("a_b", underscored);
        Assert.NotEqual(dotted, underscored); // unique: a.b and a_b stay apart
        Assert.Matches("^[A-Za-z0-9_-]+$", dotted);
        Assert.Equal(dotted, ClaudeNames.BridgeName("a.b")); // the same in every run
        Assert.Matches("_[0-9a-f]{12}$", dotted);
    }

    [Fact]
    public void A_Long_Name_Fits_With_The_Prefix_And_Names_Ending_Like_A_Made_One_Are_Hashed_Too()
    {
        var name = new string('x', 80);
        var bridge = ClaudeNames.BridgeName(name);
        Assert.True((ClaudeNames.Prefix + bridge).Length <= 64);
        Assert.NotEqual(bridge, ClaudeNames.BridgeName(new string('x', 81)));
        // a name that already ends as a made one does must not collide with the one made from another
        var lookalike = "tool_0123456789ab";
        Assert.NotEqual(lookalike, ClaudeNames.BridgeName(lookalike));
    }

    [Fact]
    public void An_Input_Schema_With_A_OneOf_Root_Is_Folded_To_An_Object()
    {
        var folded = ClaudeNames.ObjectSchema(Obj("""
            {"oneOf":[{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"number"}},"required":["a","b"]},
                      {"type":"object","properties":{"a":{"type":"string"},"c":{"type":"boolean"}},"required":["a"]}]}
            """));
        Assert.Equal("object", folded["type"]!.GetValue<string>());
        Assert.Equal(["a", "b", "c"], folded["properties"]!.AsObject().Select(p => p.Key).Order().ToArray());
        Assert.Equal(["a"], folded["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray()); // only what every variant requires
        // an object root goes as it came
        Assert.Equal("""{"type":"object","properties":{"x":{"type":"string"}}}""",
            ClaudeNames.ObjectSchema(Obj("""{"type":"object","properties":{"x":{"type":"string"}}}""")).ToJsonString());
    }

    [Theory]
    [InlineData("claude-opus-5-5", "claude-opus-5-5")] // one claude knows
    [InlineData("claude-fable-5-2", "claude-fable-5-2[1m]")] // 5 generation it does not: asked by its [1m] name
    [InlineData("claude-sonnet-4-6", "claude-sonnet-4-6")] // 4 generation keeps its name
    [InlineData("claude-haiku-4-5-20251001", "claude-haiku-4-5-20251001")]
    [InlineData("gpt-5", "gpt-5")]
    [InlineData("claude-fable-5-2[1m]", "claude-fable-5-2[1m]")]
    public void A_Run_Asks_For_The_Model_By_The_Name_That_Gets_Its_Window(string model, string expected) =>
        Assert.Equal(expected, ClaudeRequest.RunName(model));

    // ---------------------------------------------------------------- the request

    private const string Conversation = """
        {"model":"claude-sonnet-5-5","stream":true,"system":[{"type":"text","text":"x-anthropic-billing-header: cc_version=2.1.99"},{"type":"text","text":"Be brief."}],
         "tools":[{"name":"read.file","description":"Reads","input_schema":{"type":"object","properties":{"path":{"type":"string"}}}}],
         "tool_choice":{"type":"auto"},
         "messages":[
           {"role":"user","content":"What is in a.txt?"},
           {"role":"assistant","content":[{"type":"thinking","thinking":"hmm","signature":"sig"},{"type":"text","text":"Let me look."},{"type":"tool_use","id":"toolu_1","name":"read.file","input":{"path":"a.txt"}}]},
           {"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"hello world"}]}]}
        """;

    [Fact]
    public void The_Request_Is_Read_With_The_Tools_Under_Their_Bridge_Names_And_Without_The_Billing_Line()
    {
        var r = ClaudeRequest.Parse(Obj(Conversation));
        Assert.True(r.Stream);
        Assert.Equal("Be brief.", r.System);
        var tool = Assert.Single(r.Tools);
        Assert.Equal("read.file", tool.Name);
        Assert.NotEqual("read.file", tool.BridgeName);
        Assert.Equal("read.file", r.DeclaredByBridgeName[tool.BridgeName]);
        Assert.Equal(3, r.Digests.Count);
    }

    [Fact]
    public void A_Conversation_Is_Matched_By_Its_Words_Not_By_Whitespace_Thinking_Or_Signatures()
    {
        var a = ClaudeRequest.Parse(Obj(Conversation));
        var rewrapped = Conversation.Replace("Let me look.", "Let  me\\nlook.").Replace("""{"type":"thinking","thinking":"hmm","signature":"sig"},""", "");
        var b = ClaudeRequest.Parse(Obj(rewrapped));
        Assert.Equal(a.Digests, b.Digests);
        // the run's own reply digests as the caller's echo of it does
        Assert.Equal(a.Digests[1], ClaudeRequest.DigestOfReply("Let me look.", ["toolu_1"]));
        Assert.NotEqual(a.Digests[1], ClaudeRequest.DigestOfReply("Let me look.", ["toolu_2"]));
    }

    [Fact]
    public void Tool_Results_Come_Back_As_Mcp_Content_Keyed_By_The_Call()
    {
        var r = ClaudeRequest.Parse(Obj("""
            {"model":"m","messages":[{"role":"user","content":[
              {"type":"tool_result","tool_use_id":"t1","content":"plain"},
              {"type":"tool_result","tool_use_id":"t2","is_error":true,"content":[{"type":"text","text":"boom"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AAAA"}}]}]}]}
            """));
        var results = r.ToolResults(0);
        Assert.Equal("plain", results["t1"].Content[0]!["text"]!.GetValue<string>());
        Assert.False(results["t1"].IsError);
        Assert.True(results["t2"].IsError);
        Assert.Equal("image/png", results["t2"].Content[1]!["mimeType"]!.GetValue<string>());
    }

    [Fact]
    public void Effort_And_Thinking_Follow_The_Request()
    {
        Assert.Equal("high", ClaudeRequest.Parse(Obj("""{"model":"m","output_config":{"effort":"high"},"messages":[]}""")).Effort);
        Assert.Equal("low", ClaudeRequest.Parse(Obj("""{"model":"m","thinking":{"type":"enabled","budget_tokens":1024},"messages":[]}""")).Effort);
        Assert.True(ClaudeRequest.Parse(Obj("""{"model":"m","thinking":{"type":"disabled"},"messages":[]}""")).ThinkingOff);
        // a turn that may call no tool is offered none
        Assert.Empty(ClaudeRequest.Parse(Obj(Conversation.Replace("""{"type":"auto"}""", """{"type":"none"}"""))).Tools);
    }

    [Fact]
    public void A_New_Run_Is_Told_The_Whole_Conversation_With_The_History_Apart_From_The_Turn()
    {
        var r = ClaudeRequest.Parse(Obj("""
            {"model":"m","system":"Be brief.","tool_choice":{"type":"any"},
             "messages":[{"role":"user","content":"one"},{"role":"assistant","content":"two"},
                         {"role":"user","content":[{"type":"text","text":"three"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AAAA"}}]}]}
            """));
        var blocks = r.RenderWhole();
        var text = string.Concat(blocks.OfType<JsonObject>().Where(b => b["type"]!.GetValue<string>() == "text").Select(b => b["text"]!.GetValue<string>()));
        Assert.Contains("<external_system_instructions>\nBe brief.", text);
        Assert.Contains("You must call at least one available tool", text);
        Assert.Contains("<conversation_history>\nHuman: one", text);
        Assert.Contains("</conversation_history>\n\n<current_turn>\nHuman: three", text);
        Assert.Single(blocks.OfType<JsonObject>(), b => b["type"]!.GetValue<string>() == "image"); // images are sent, not described
    }

    [Fact]
    public void Past_Tool_Calls_Are_A_Record_And_Thinking_Is_Never_Told()
    {
        var text = string.Concat(ClaudeRequest.Parse(Obj(Conversation)).RenderWhole().OfType<JsonObject>().Select(b => b["text"]?.GetValue<string>()));
        Assert.Contains("[Called tool read.file (toolu_1) with arguments {\"path\":\"a.txt\"}]", text);
        Assert.Contains("[Result of tool call toolu_1]\nhello world", text);
        Assert.Contains("a record of what already happened", text);
        Assert.DoesNotContain("hmm", text);
    }

    [Fact]
    public void A_Run_That_Has_The_Conversation_Is_Told_Only_The_New_Messages()
    {
        var r = ClaudeRequest.Parse(Obj(Conversation));
        var text = r.RenderSince(2)[0]!["text"]!.GetValue<string>();
        Assert.Contains("hello world", text);
        Assert.DoesNotContain("What is in a.txt", text);
    }

    // ---------------------------------------------------------------- the process

    [Fact]
    public void The_Run_Has_None_Of_Claudes_Own_Tools_Settings_Or_Session_Files()
    {
        var r = ClaudeRequest.Parse(Obj("""{"model":"claude-fable-5-2","output_config":{"effort":"max"},"messages":[]}"""));
        var args = ClaudeBridgeService.LaunchArguments(r, """{"mcpServers":{}}""").ToList();
        Assert.Equal("-p", args[0]);
        Assert.Equal("", args[args.IndexOf("--tools") + 1]);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Equal("", args[args.IndexOf("--setting-sources") + 1]);
        Assert.Contains("--no-session-persistence", args);
        Assert.Equal("claude-fable-5-2[1m]", args[args.IndexOf("--model") + 1]);
        Assert.Equal("max", args[args.IndexOf("--effort") + 1]);
        Assert.Equal("stream-json", args[args.IndexOf("--input-format") + 1]);
    }

    [Fact]
    public void Nothing_Points_The_Run_At_Another_Endpoint_And_The_Accounts_Directory_And_Proxy_Are_Its_Own()
    {
        var psi = new ProcessStartInfo("claude");
        psi.Environment["ANTHROPIC_API_KEY"] = "sk-secret";
        psi.Environment["ANTHROPIC_BASE_URL"] = "https://elsewhere";
        psi.Environment["CLAUDE_CODE_OAUTH_TOKEN"] = "tok";
        psi.Environment["CLAUDE_CONFIG_DIR"] = "/home/me/.claude";
        ClaudeProcess.ApplyEnvironment(psi, "/data/acct", "http://proxy:8080", run: true);
        Assert.False(psi.Environment.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.False(psi.Environment.ContainsKey("ANTHROPIC_BASE_URL"));
        Assert.False(psi.Environment.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN"));
        Assert.Equal("/data/acct", psi.Environment["CLAUDE_CONFIG_DIR"]);
        Assert.Equal("http://proxy:8080", psi.Environment["HTTPS_PROXY"]);
        Assert.Equal("1", psi.Environment["DISABLE_AUTOUPDATER"]);
        Assert.NotNull(ClaudeProcess.ProxyProblem("socks5://127.0.0.1:1080"));
        Assert.Null(ClaudeProcess.ProxyProblem("http://127.0.0.1:8080"));
    }

    [Theory]
    [InlineData("You've hit your limit · resets 9pm", 0, 429, "rate_limit_error")]
    [InlineData("Claude usage limit reached", 0, 429, "rate_limit_error")]
    [InlineData("x", 429, 429, "rate_limit_error")]
    [InlineData("Not logged in · Please run /login", 0, 401, "authentication_error")]
    [InlineData("OAuth token has expired", 401, 401, "authentication_error")]
    [InlineData("Overloaded", 529, 529, "api_error")]
    [InlineData("something odd", 0, 502, "api_error")]
    public void A_Failure_Is_Given_The_Status_An_Upstream_Would_Have_Used(string text, int apiStatus, int status, string type)
    {
        var e = ClaudeBridgeService.ClassifyText(text, apiStatus, null);
        Assert.Equal(status, e.Status);
        Assert.Equal(type, e.ErrorType);
    }

    // ---------------------------------------------------------------- the reply

    [Fact]
    public void Stream_Events_Are_Put_Together_Into_The_Message_A_Non_Streaming_Request_Gets()
    {
        var b = new ClaudeMessageBuilder();
        foreach (var line in new[]
                 {
                     """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"m","content":[],"usage":{"input_tokens":5,"output_tokens":0}}}""",
                     """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
                     """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}""",
                     """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"lo"}}""",
                     """{"type":"content_block_stop","index":0}""",
                     """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"read.file","input":{}}}""",
                     """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"path\":"}}""",
                     """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"\"a.txt\"}"}}""",
                     """{"type":"content_block_stop","index":1}""",
                     """{"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":9}}""",
                     """{"type":"message_stop"}""",
                 })
            b.Add(Obj(line));
        var m = b.Build();
        Assert.Equal("Hello", m["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("a.txt", m["content"]![1]!["input"]!["path"]!.GetValue<string>());
        Assert.Equal("tool_use", m["stop_reason"]!.GetValue<string>());
        Assert.Equal(5, m["usage"]!["input_tokens"]!.GetValue<int>());
        Assert.Equal(9, m["usage"]!["output_tokens"]!.GetValue<int>());
    }

    // ---------------------------------------------------------------- the helper

    private sealed class Echo(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    [Fact]
    public async Task The_Helper_Speaks_The_Three_Mcp_Methods_And_Hands_A_Call_Back_To_The_Gateway()
    {
        var tools = new JsonArray(Obj("""{"name":"echo","inputSchema":{"type":"object"}}"""));
        var handler = new Echo(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"content":[{"type":"text","text":"pong"}],"is_error":false}""", Encoding.UTF8, "application/json"),
        });
        using var client = new HttpClient(handler);

        var init = await ClaudeMcpHelper.AnswerAsync(Obj("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}"""), tools, client, "http://x/", default);
        Assert.Equal(ClaudeNames.ServerName, init["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        var list = await ClaudeMcpHelper.AnswerAsync(Obj("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""), tools, client, "http://x/", default);
        Assert.Equal("echo", list["result"]!["tools"]![0]!["name"]!.GetValue<string>());

        var call = await ClaudeMcpHelper.AnswerAsync(
            Obj("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"s":"ping"},"_meta":{"claudecode/toolUseId":"toolu_9"}}}"""), tools, client, "http://x/", default);
        Assert.Equal("pong", call["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(3, call["id"]!.GetValue<int>());
        var sent = Obj(handler.Body!);
        Assert.Equal("toolu_9", sent["tool_call_id"]!.GetValue<string>()); // the id claude gave the call, which the caller's result will name
        Assert.Equal("ping", sent["arguments"]!["s"]!.GetValue<string>());

        var unknown = await ClaudeMcpHelper.AnswerAsync(Obj("""{"jsonrpc":"2.0","id":4,"method":"nope"}"""), tools, client, "http://x/", default);
        Assert.Equal(-32601, unknown["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_Callback_Server_Answers_Only_A_Live_Runs_Token_And_Waits_For_The_Result()
    {
        using var server = new ClaudeCallbackServer();
        var gate = new TaskCompletionSource();
        server.Handler = async (token, id, name, args, ct) =>
        {
            if (token != "good") return null;
            await gate.Task;
            return ([Obj("""{"type":"text","text":"done"}""")], false);
        };
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var payload = new StringContent("""{"tool_call_id":"t","name":"echo","arguments":{}}""", Encoding.UTF8, "application/json");

        using var wrong = await http.PostAsync(server.BaseUrl + "bad", payload);
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);

        var pending = http.PostAsync(server.BaseUrl + "good", new StringContent("""{"tool_call_id":"t","name":"echo","arguments":{}}""", Encoding.UTF8, "application/json"));
        await Task.Delay(150);
        Assert.False(pending.IsCompleted); // the call is held until the caller's result is given
        gate.SetResult();
        using var ok = await pending;
        Assert.Equal("done", Obj(await ok.Content.ReadAsStringAsync())["content"]![0]!["text"]!.GetValue<string>());
        // only POST is understood
        using var get = await http.GetAsync(server.BaseUrl + "good");
        Assert.Equal(HttpStatusCode.BadRequest, get.StatusCode);
    }
}
