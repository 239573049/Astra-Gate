using System.Net;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Plugins;
using Astra.Core.Privacy;
using Astra.Core.Requests;
using Astra.Gateway.Pipeline;
using Astra.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>magpie-compatible gateway middleware on real gateway requests: passthrough and translated, streamed and whole.</summary>
public class GatewayMiddlewareTests
{
    private const string AnthropicStream =
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-5\",\"content\":[],\"usage\":{\"input_tokens\":12,\"output_tokens\":1}}}\n\n" +
        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"the secret is out\"}}\n\n" +
        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":3}}\n\n" +
        "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

    private const string ChatCompletion =
        """{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"gpt-5","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15}}""";

    private static async Task AddMiddlewareAsync(GatewayFixture gw, string code, string? options = null)
    {
        var dir = Path.Combine(gw.Host.Root, "plugin-src", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"), """{"name":"test-mw","magpie":{"middleware":"./t.middleware.js"}}""");
        File.WriteAllText(Path.Combine(dir, "t.middleware.js"), code);
        await gw.Host.Db.Plugins.AddAsync(new PluginEntry { Spec = dir, Name = "test-mw", OptionsJson = options });
        gw.Host.App.Services.GetRequiredService<PluginCatalog>().Invalidate();
    }

    [Fact]
    public async Task Passthrough_stream_request_and_events_go_through_the_middleware()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.ClaudeCode, ApiProtocol.Anthropic, _ => FakeUpstream.Sse(AnthropicStream));
        await AddMiddlewareAsync(gw, """
            export function onRequest(b, ctx) {
              b.metadata = { user_id: ctx.protocol + '/' + ctx.agent + '/' + ctx.model + '/' + ctx.stream + '/' + ctx.options.tag }
              return b
            }
            export const events = ['content_block_delta']
            export function onEvent(ev) {
              ev.delta.text = ev.delta.text.replace('secret', '***')
              return ev
            }
            """, """{"tag":"t1"}""");

        var response = await gw.PostAsync("/v1/messages",
            """{"model":"claude-sonnet-5","stream":true,"max_tokens":10,"messages":[{"role":"user","content":"hi"}]}""");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("anthropic/claude-code/claude-sonnet-5/true/t1", gw.Upstream.Requests.Single().Json["metadata"]!["user_id"]!.GetValue<string>());
        Assert.Contains("\"text\":\"the *** is out\"", text);
        Assert.DoesNotContain("secret", text);
        Assert.EndsWith("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n", text);
        var record = await gw.RecordOfAsync(response);
        Assert.True(record.Status == RequestStatus.Success, record.ErrorType + ": " + record.ErrorMessage);
        Assert.Equal(3, record.TotalOutputTokens);
    }

    [Fact]
    public async Task A_reject_is_answered_in_the_agents_error_shape_and_nothing_goes_upstream()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.ClaudeCode, ApiProtocol.Anthropic, _ => FakeUpstream.Sse(AnthropicStream));
        await AddMiddlewareAsync(gw, "export function onRequest(b, ctx) { ctx.reject(429, 'slow down') }");

        var response = await gw.PostAsync("/v1/messages", """{"model":"claude-sonnet-5","max_tokens":10,"messages":[{"role":"user","content":"hi"}]}""");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal((HttpStatusCode)429, response.StatusCode);
        Assert.Equal("error", body["type"]!.GetValue<string>());
        Assert.Equal("rate_limit_error", body["error"]!["type"]!.GetValue<string>());
        Assert.Equal("slow down", body["error"]!["message"]!.GetValue<string>());
        Assert.Empty(gw.Upstream.Requests);
        Assert.Equal(RequestStatus.Blocked, (await gw.RecordOfAsync(response)).Status);
    }

    [Fact]
    public async Task Translated_streams_reach_on_event_in_the_agents_own_api()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.Anthropic, _ => FakeUpstream.Sse(AnthropicStream));
        await AddMiddlewareAsync(gw, """
            export function onRequest(b, ctx) { if (ctx.protocol !== 'chat') ctx.reject(400, 'saw ' + ctx.protocol) }
            export function onEvent(ev, ctx) {
              for (const c of ev.choices || []) if (c.delta && typeof c.delta.content === 'string') c.delta.content = c.delta.content.toUpperCase()
              return ev
            }
            """);

        var response = await gw.PostAsync("/v1/chat/completions",
            """{"model":"claude-sonnet-5","stream":true,"messages":[{"role":"user","content":"hi"}]}""");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("THE SECRET IS OUT", text);
        Assert.Contains("data: [DONE]", text);
    }

    [Fact]
    public async Task Whole_replies_go_to_on_response_with_the_status()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        await AddMiddlewareAsync(gw, "export function onResponse(b, ctx) { b.seen_status = ctx.status; b.choices[0].message.content += '!'; return b }");

        var response = await gw.PostAsync("/v1/chat/completions", """{"model":"gpt-5","messages":[{"role":"user","content":"hi"}]}""");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(200, body["seen_status"]!.GetValue<int>());
        Assert.Equal("OK!", body["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        Assert.Null(response.Content.Headers.ContentLength is { } n && n != (await response.Content.ReadAsByteArrayAsync()).Length ? n : null);
    }

    [Fact]
    public async Task A_failing_middleware_leaves_the_request_as_it_was()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        await AddMiddlewareAsync(gw, "export function onRequest(b) { throw new Error('boom') }\nexport function onResponse() { while (true) {} }");

        var response = await gw.PostAsync("/v1/chat/completions", """{"model":"gpt-5","messages":[{"role":"user","content":"hi"}]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("OK", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        Assert.Equal("hi", gw.Upstream.Requests.Single().Json["messages"]![0]!["content"]!.GetValue<string>());
        var plugins = await gw.Host.GetJsonAsync("/api/plugins");
        var mw = plugins["plugins"]![0]!["middleware"]!;
        Assert.Equal(2, mw["failures"]!.GetValue<int>());
        Assert.Equal("onResponse: took too long", mw["lastError"]!.GetValue<string>());
    }

    [Fact]
    public async Task Middleware_sees_the_agents_text_and_the_privacy_guard_still_covers_what_it_sends()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        await gw.Host.App.Services.GetRequiredService<PrivacyGuardService>()
            .UpdateAsync(new PrivacySettings { Enabled = true, DryRun = false, DefaultAction = PrivacyActions.Redact });
        // the middleware sees the address the agent sent, and adds one of its own (in the same message: the
        // guard numbers its placeholders per string)
        await AddMiddlewareAsync(gw, """
            export function onRequest(b) {
              const m = b.messages[0]
              m.content += (m.content.includes('a@b.com') ? ' [mw saw it]' : ' [mw saw a placeholder]') + ' and c@d.com'
              return b
            }
            """);

        var response = await gw.PostAsync("/v1/chat/completions", """{"model":"gpt-5","messages":[{"role":"user","content":"mail a@b.com"}]}""");

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var sent = gw.Upstream.Requests.Single().Body;
        Assert.Contains("[mw saw it]", sent);
        Assert.DoesNotContain("a@b.com", sent);
        Assert.DoesNotContain("c@d.com", sent);
    }
}
