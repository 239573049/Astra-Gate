using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Privacy;
using Astra.Core.Requests;
using Astra.Core.Tools;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>
/// Search on behalf of models that cannot search: a request offering a web-search server tool gets Astra's search, run as internal calls
/// through the ordinary pipeline, and is answered in the shape the client's protocol expects. The upstream and the search API are fakes.
/// </summary>
public sealed class GatewayWebSearchTests
{
    private const string Tavily = """{"results":[{"title":"Magpie facts","url":"https://birds.example/magpie","content":"Magpies are clever corvids."},{"title":"More","url":"https://birds.example/more","content":"Second page."}]}""";

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => FakeUpstream.Json(json, status);

    /// <summary>The fake: Tavily answers by host, every other request goes to <paramref name="model"/> in turn.</summary>
    private sealed class Script(Func<int, JsonObject, string> model, string search = Tavily, HttpStatusCode searchStatus = HttpStatusCode.OK)
    {
        public int ModelCalls;
        public List<JsonObject> ModelBodies { get; } = [];
        public List<FakeUpstream.Seen> SearchCalls { get; } = [];

        public HttpResponseMessage Respond(FakeUpstream.Seen seen)
        {
            if (seen.Url.Host == "api.tavily.com")
            {
                lock (SearchCalls) SearchCalls.Add(seen);
                return Json(search, searchStatus);
            }
            JsonObject body;
            int n;
            lock (ModelBodies)
            {
                body = seen.Json;
                ModelBodies.Add(body);
                n = ++ModelCalls;
            }
            return Json(model(n, body));
        }
    }

    private static async Task<GatewayFixture> StartAsync(ApiProtocol upstream, Script script, bool onBehalf = true, string keyText = "tvly-secret-key-1")
    {
        var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, upstream, script.Respond);
        var protector = gw.Host.App.Services.GetRequiredService<ISecretProtector>();
        await gw.Host.App.Services.GetRequiredService<ToolsSettingsService>().UpdateAsync(s =>
        {
            s.Search.OnBehalf = onBehalf;
            s.Search.Apis.Add(new SearchApiConfig
            {
                Vendor = SearchVendors.Tavily, Keys = [new SearchKeyConfig { Id = "k1", KeyEnc = protector.Protect(keyText) }],
            });
        });
        return gw;
    }

    // ---------------------------------------------------------------- Anthropic

    private static string AnthropicToolUse(string query) =>
        $$$"""{"id":"msg_1","type":"message","role":"assistant","model":"claude-x","content":[{"type":"text","text":"Let me look."},{"type":"tool_use","id":"toolu_1","name":"web_search","input":{"query":"{{{query}}}"}}],"stop_reason":"tool_use","usage":{"input_tokens":10,"output_tokens":5}}""";

    private const string AnthropicFinal =
        """{"id":"msg_2","type":"message","role":"assistant","model":"claude-x","content":[{"type":"text","text":"Magpies are clever."}],"stop_reason":"end_turn","usage":{"input_tokens":30,"output_tokens":7}}""";

    private const string AnthropicRequest =
        """{"model":"claude-x","max_tokens":100,"stream":{STREAM},"tools":[{"type":"web_search_20250305","name":"web_search","max_uses":3},{"name":"calc","description":"d","input_schema":{"type":"object"}}],"messages":[{"role":"user","content":"Tell me about magpies"}]}""";

    private static Script AnthropicScript(string query = "magpie facts") =>
        new((n, _) => n == 1 ? AnthropicToolUse(query) : AnthropicFinal);

    [Fact]
    public async Task Anthropic_search_tool_is_replaced_the_search_is_run_and_the_reply_carries_server_tool_blocks()
    {
        var script = AnthropicScript();
        await using var gw = await StartAsync(ApiProtocol.Anthropic, script);

        var response = await gw.PostAsync("/v1/messages", AnthropicRequest.Replace("{STREAM}", "false"));
        var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, script.ModelCalls);
        // Round 1: the server tool is a function tool the upstream can call; the client's own tool stays.
        var first = script.ModelBodies[0];
        var tools = first["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["web_search", "calc"], tools);
        Assert.Equal("object", first["tools"]![0]!["input_schema"]!["type"]!.GetValue<string>());
        Assert.Null(first["tools"]![0]!["type"]);
        Assert.False(first["stream"]!.GetValue<bool>());
        // Round 2: the model's turn and the search results.
        var second = script.ModelBodies[1]["messages"]!.AsArray();
        Assert.Equal("assistant", second[1]!["role"]!.GetValue<string>());
        var result = second[2]!["content"]![0]!;
        Assert.Equal("tool_result", result["type"]!.GetValue<string>());
        Assert.Equal("toolu_1", result["tool_use_id"]!.GetValue<string>());
        Assert.Contains("Magpies are clever corvids.", result["content"]!.GetValue<string>());
        // The search API was asked with the key, the query and the count.
        var search = Assert.Single(script.SearchCalls);
        Assert.Equal("Bearer tvly-secret-key-1", search.Headers["authorization"]);
        Assert.Equal("magpie facts", search.Json["query"]!.GetValue<string>());
        // The client sees what a native server tool answers with.
        var types = reply["content"]!.AsArray().Select(b => b!["type"]!.GetValue<string>()).ToList();
        Assert.Equal(["text", "server_tool_use", "web_search_tool_result", "text"], types);
        Assert.Equal("web_search", reply["content"]![1]!["name"]!.GetValue<string>());
        Assert.Equal("magpie facts", reply["content"]![1]!["input"]!["query"]!.GetValue<string>());
        var hits = reply["content"]![2]!["content"]!.AsArray();
        Assert.Equal("https://birds.example/magpie", hits[0]!["url"]!.GetValue<string>());
        Assert.Equal("Magpies are clever.", reply["content"]![3]!["text"]!.GetValue<string>());
        Assert.Equal(1, reply["usage"]!["server_tool_use"]!["web_search_requests"]!.GetValue<int>());
        Assert.NotNull(response.Headers.GetValues("x-astra-request-id").Single());
    }

    [Fact]
    public async Task Anthropic_streaming_clients_get_the_finished_reply_as_events()
    {
        var script = AnthropicScript();
        await using var gw = await StartAsync(ApiProtocol.Anthropic, script);

        var response = await gw.PostAsync("/v1/messages", AnthropicRequest.Replace("{STREAM}", "true"));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.False(script.ModelBodies[0]["stream"]!.GetValue<bool>()); // the rounds themselves are not streamed
        var events = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(e => e.Split('\n')).ToList();
        Assert.Equal("message_start", events[0][0]["event: ".Length..]);
        Assert.Equal("message_stop", events[^1][0]["event: ".Length..]);
        var data = events.Select(e => JsonNode.Parse(e[1]["data: ".Length..])!).ToList();
        Assert.Contains(data, d => d["type"]!.GetValue<string>() == "content_block_start" && d["content_block"]!["type"]!.GetValue<string>() == "server_tool_use");
        Assert.Contains(data, d => d["type"]!.GetValue<string>() == "content_block_start" && d["content_block"]!["type"]!.GetValue<string>() == "web_search_tool_result");
        Assert.Contains(data, d => d["delta"]?["text"]?.GetValue<string>() == "Magpies are clever.");
        var stop = data.First(d => d["type"]!.GetValue<string>() == "message_delta");
        Assert.Equal("end_turn", stop["delta"]!["stop_reason"]!.GetValue<string>());
        // Indexes run 0..n-1 in order.
        var starts = data.Where(d => d["type"]!.GetValue<string>() == "content_block_start").Select(d => d["index"]!.GetValue<int>()).ToList();
        Assert.Equal(Enumerable.Range(0, starts.Count), starts);
    }

    [Fact]
    public async Task Search_is_off_by_default_opt_out_per_request_and_a_request_without_a_search_tool_is_left_alone()
    {
        var script = new Script((_, _) => AnthropicFinal);
        await using var off = await StartAsync(ApiProtocol.Anthropic, script, onBehalf: false);
        var passed = await off.PostAsync("/v1/messages", AnthropicRequest.Replace("{STREAM}", "false"));
        Assert.Equal(HttpStatusCode.OK, passed.StatusCode);
        Assert.Equal("web_search_20250305", script.ModelBodies[0]["tools"]![0]!["type"]!.GetValue<string>()); // untouched, as sent

        var script2 = new Script((_, _) => AnthropicFinal);
        await using var on = await StartAsync(ApiProtocol.Anthropic, script2);
        var optedOut = await on.PostAsync("/v1/messages", AnthropicRequest.Replace("{STREAM}", "false"), null, ("x-astra-web-search", "off"));
        Assert.Equal(HttpStatusCode.OK, optedOut.StatusCode);
        Assert.Equal("web_search_20250305", script2.ModelBodies[0]["tools"]![0]!["type"]!.GetValue<string>());
        var plain = await on.PostAsync("/v1/messages", """{"model":"claude-x","max_tokens":10,"messages":[{"role":"user","content":"hi"}]}""");
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Equal(2, script2.ModelCalls);
        Assert.Empty(script.SearchCalls);
        Assert.Empty(script2.SearchCalls);
    }

    [Fact]
    public async Task A_provider_that_serves_the_search_tool_itself_is_left_alone_unless_mode_is_always()
    {
        var anthropic = new Astra.Core.Models.Provider
        {
            Endpoints = [new Astra.Core.Models.ProviderEndpoint { Protocol = ApiProtocol.Anthropic, BaseUrl = "https://api.anthropic.com" }],
        };
        var openai = new Astra.Core.Models.Provider
        {
            Endpoints = [new Astra.Core.Models.ProviderEndpoint { Protocol = ApiProtocol.OpenAIChat, BaseUrl = "https://api.openai.com/v1" }],
        };
        Assert.True(WebSearchBridge.ServesItself(anthropic, ApiProtocol.Anthropic, "claude-x"));
        Assert.False(WebSearchBridge.ServesItself(anthropic, ApiProtocol.OpenAIResponses, "gpt"));
        Assert.True(WebSearchBridge.ServesItself(openai, ApiProtocol.OpenAIChat, "gpt-5-search-preview"));
        Assert.False(WebSearchBridge.ServesItself(openai, ApiProtocol.OpenAIChat, "gpt-5"));
        anthropic.Settings["web_search"] = true;
        Assert.True(WebSearchBridge.ServesItself(anthropic, ApiProtocol.OpenAIResponses, "x"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_failed_search_is_told_to_the_model_which_answers_anyway_and_the_keys_never_reach_the_model()
    {
        var script = new Script((n, _) => n == 1 ? AnthropicToolUse("x") : AnthropicFinal, search: """{"detail":"bad key tvly-secret-key-1"}""", searchStatus: HttpStatusCode.Unauthorized);
        await using var gw = await StartAsync(ApiProtocol.Anthropic, script);

        var response = await gw.PostAsync("/v1/messages", AnthropicRequest.Replace("{STREAM}", "false"));
        var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = script.ModelBodies[1]["messages"]![2]!["content"]![0]!;
        Assert.Contains("The search failed", result["content"]!.GetValue<string>());
        Assert.DoesNotContain("tvly-secret-key-1", result["content"]!.GetValue<string>());
        Assert.True(result["is_error"]!.GetValue<bool>());
        Assert.Equal("web_search_tool_result_error", reply["content"]![2]!["content"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_query_sent_to_a_search_api_is_the_guarded_text_and_the_trace_shows_the_search()
    {
        const string secret = "AKIAIOSFODNN7EXAMPLE";
        var script = AnthropicScript($"find {secret} docs");
        await using var gw = await StartAsync(ApiProtocol.Anthropic, script);
        await gw.Host.App.Services.GetRequiredService<PrivacyGuardService>()
            .UpdateAsync(new PrivacySettings { Enabled = true, DryRun = false, DefaultAction = PrivacyActions.Redact });

        var response = await gw.PostAsync("/v1/messages", AnthropicRequest.Replace("{STREAM}", "false"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var search = Assert.Single(script.SearchCalls);
        Assert.DoesNotContain(secret, search.Body);
        Assert.StartsWith("find ", search.Json["query"]!.GetValue<string>());
        // The second round's request (carrying the search call) has a request row whose trace holds the search span, without the query.
        var record = await gw.RecordOfAsync(response);
        var spans = await gw.Host.Db.Traces.ListSpansAsync(record.Id);
        var span = Assert.Single(spans, s => s.Name == TraceSpanNames.Search);
        var attributes = JsonNode.Parse(span.AttributesJson!)!;
        Assert.Equal("Tavily", attributes["astra.search.source"]!.GetValue<string>());
        Assert.Equal(2, attributes["astra.search.results"]!.GetValue<int>());
        Assert.Null(attributes["astra.search.query"]);
    }

    [Fact]
    public async Task A_model_that_asks_for_search_and_a_client_tool_together_gets_its_client_call_back()
    {
        var mixed = """{"id":"m","type":"message","role":"assistant","model":"claude-x","content":[{"type":"tool_use","id":"toolu_1","name":"web_search","input":{"query":"q"}},{"type":"tool_use","id":"toolu_2","name":"calc","input":{"x":1}}],"stop_reason":"tool_use","usage":{"input_tokens":1,"output_tokens":1}}""";
        var script = new Script((_, _) => mixed);
        await using var gw = await StartAsync(ApiProtocol.Anthropic, script);

        var response = await gw.PostAsync("/v1/messages", AnthropicRequest.Replace("{STREAM}", "false"));
        var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(1, script.ModelCalls);
        var names = reply["content"]!.AsArray().Select(b => b!["type"]!.GetValue<string>() + ":" + (b["name"]?.GetValue<string>() ?? "")).ToList();
        Assert.Equal(["server_tool_use:web_search", "web_search_tool_result:", "tool_use:calc"], names);
        Assert.Equal("tool_use", reply["stop_reason"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- Responses

    private static string ResponsesCall(string query) =>
        $$$"""{"id":"resp_1","object":"response","status":"completed","model":"gpt-x","output":[{"id":"fc_1","type":"function_call","call_id":"call_1","name":"web_search","arguments":"{\"query\":\"{{{query}}}\"}","status":"completed"}],"usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15}}""";

    private const string ResponsesFinal =
        """{"id":"resp_2","object":"response","status":"completed","model":"gpt-x","output":[{"id":"msg_1","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Magpies are clever.","annotations":[]}]}],"usage":{"input_tokens":30,"output_tokens":7,"total_tokens":37}}""";

    private const string ResponsesRequest =
        """{"model":"gpt-x","stream":{STREAM},"input":"Tell me about magpies","tools":[{"type":"web_search_preview"}],"include":["web_search_call.action.sources"]}""";

    [Fact]
    public async Task Responses_search_is_replaced_run_and_reported_as_web_search_call_items()
    {
        var script = new Script((n, _) => n == 1 ? ResponsesCall("magpies") : ResponsesFinal);
        await using var gw = await StartAsync(ApiProtocol.OpenAIResponses, script);

        var response = await gw.PostAsync("/v1/responses", ResponsesRequest.Replace("{STREAM}", "false"));
        var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var first = script.ModelBodies[0];
        Assert.Equal("function", first["tools"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("web_search", first["tools"]![0]!["name"]!.GetValue<string>());
        Assert.Empty(first["include"]!.AsArray());
        var input = script.ModelBodies[1]["input"]!.AsArray();
        Assert.Equal("function_call", input[1]!["type"]!.GetValue<string>());
        Assert.Equal("function_call_output", input[2]!["type"]!.GetValue<string>());
        Assert.Contains("Magpies are clever corvids.", input[2]!["output"]!.GetValue<string>());
        var output = reply["output"]!.AsArray();
        Assert.Equal("web_search_call", output[0]!["type"]!.GetValue<string>());
        Assert.Equal("magpies", output[0]!["action"]!["query"]!.GetValue<string>());
        Assert.Equal("message", output[1]!["type"]!.GetValue<string>());
        Assert.Equal("url_citation", output[1]!["content"]![0]!["annotations"]![0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Responses_streaming_clients_get_the_events_of_a_streamed_response()
    {
        var script = new Script((n, _) => n == 1 ? ResponsesCall("magpies") : ResponsesFinal);
        await using var gw = await StartAsync(ApiProtocol.OpenAIResponses, script);

        var response = await gw.PostAsync("/v1/responses", ResponsesRequest.Replace("{STREAM}", "true"));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        var types = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(e => JsonNode.Parse(e.Split('\n')[1]["data: ".Length..])!["type"]!.GetValue<string>()).ToList();
        Assert.Equal("response.created", types[0]);
        Assert.Equal("response.completed", types[^1]);
        Assert.Contains("response.output_text.delta", types);
        Assert.Equal(2, types.Count(t => t == "response.output_item.added"));
        Assert.Contains("Magpies are clever.", text);
    }

    // ---------------------------------------------------------------- Chat and Gemini

    [Fact]
    public async Task Chat_web_search_options_become_a_function_tool_and_the_answer_gets_citations()
    {
        var call = """{"id":"c1","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_9","type":"function","function":{"name":"web_search","arguments":"{\"query\":\"magpies\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";
        var final = """{"id":"c2","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"Clever birds."},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";
        var script = new Script((n, _) => n == 1 ? call : final);
        await using var gw = await StartAsync(ApiProtocol.OpenAIChat, script);

        var response = await gw.PostAsync("/v1/chat/completions", """{"model":"m","web_search_options":{},"messages":[{"role":"user","content":"magpies?"}]}""");
        var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Null(script.ModelBodies[0]["web_search_options"]);
        Assert.Equal("web_search", script.ModelBodies[0]["tools"]![0]!["function"]!["name"]!.GetValue<string>());
        var messages = script.ModelBodies[1]["messages"]!.AsArray();
        Assert.Equal("tool", messages[^1]!["role"]!.GetValue<string>());
        Assert.Equal("call_9", messages[^1]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("Clever birds.", reply["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        Assert.Equal("https://birds.example/magpie", reply["choices"]![0]!["message"]!["annotations"]![0]!["url_citation"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task Gemini_google_search_becomes_a_function_declaration_and_the_answer_carries_grounding_metadata()
    {
        var call = """{"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"web_search","args":{"query":"magpies"}}}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":1,"totalTokenCount":2}}""";
        var final = """{"candidates":[{"content":{"role":"model","parts":[{"text":"Clever birds."}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":1,"totalTokenCount":2}}""";
        var script = new Script((n, _) => n == 1 ? call : final);
        await using var gw = await StartAsync(ApiProtocol.Gemini, script);

        var response = await gw.PostAsync("/v1beta/models/gemini-x:generateContent",
            """{"contents":[{"role":"user","parts":[{"text":"magpies?"}]}],"tools":[{"google_search":{}}]}""");
        var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("web_search", script.ModelBodies[0]["tools"]![0]!["functionDeclarations"]![0]!["name"]!.GetValue<string>());
        var contents = script.ModelBodies[1]["contents"]!.AsArray();
        Assert.Equal("web_search", contents[^1]!["parts"]![0]!["functionResponse"]!["name"]!.GetValue<string>());
        Assert.Equal("Clever birds.", reply["candidates"]![0]!["content"]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("magpies", reply["candidates"]![0]!["groundingMetadata"]!["webSearchQueries"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task An_upstream_error_in_a_round_reaches_the_client_as_the_pipeline_wrote_it()
    {
        var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.Anthropic, _ => Json("""{"type":"error","error":{"type":"overloaded_error","message":"busy"}}""", (HttpStatusCode)529));
        await using var _ = gw;
        var protector = gw.Host.App.Services.GetRequiredService<ISecretProtector>();
        await gw.Host.App.Services.GetRequiredService<ToolsSettingsService>().UpdateAsync(s =>
        {
            s.Search.OnBehalf = true;
            s.Search.Apis.Add(new SearchApiConfig { Vendor = SearchVendors.Tavily, Keys = [new SearchKeyConfig { Id = "k", KeyEnc = protector.Protect("tvly-k") }] });
        });

        var response = await gw.PostAsync("/v1/messages", AnthropicRequest.Replace("{STREAM}", "false"));

        Assert.True((int)response.StatusCode >= 500);
        Assert.Contains("busy", await response.Content.ReadAsStringAsync());
    }
}
