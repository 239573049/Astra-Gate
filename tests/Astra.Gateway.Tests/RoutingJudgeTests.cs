using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core.Routing;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Routing;

namespace Astra.Gateway.Tests;

/// <summary>
/// The routing judges (plan §5.3) against a stand-in HTTP service: what they send, how they read the answer, and how
/// every way of failing is told apart. The TypeSafe fixtures follow docs.typesafe.ai. The OpenAI Decisions fixtures are
/// modelled on LiteLLM's description of OpenAI's format, NOT on OpenAI's own reference (which could not be read):
/// replace them with the official examples when it can.
/// </summary>
public class RoutingJudgeTests
{
    private static readonly JudgeQuestion Question = new(
        "Which grade?",
        [new("light", "Simple things."), new("standard", "Everyday things."), new("heavy", "Hard things.")],
        new JudgeState("Fix the flaky login test", ["Add a login test"], new JudgeShape(14, 22, false)));

    private sealed record Seen(
        HttpMethod Method, string Url, string? Authorization, string? ContentType, JsonNode? Body, Version Version, HttpVersionPolicy VersionPolicy);

    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public Stub(Func<CancellationToken, Task<HttpResponseMessage>> respond) : this((_, ct) => respond(ct))
        {
        }

        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var text = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Seen(request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.MediaType, text.Length == 0 ? null : JsonNode.Parse(text), request.Version, request.VersionPolicy));
            return await respond(request, ct);
        }
    }

    private static HttpResponseMessage Answer() =>
        new(HttpStatusCode.OK) { Content = new StringContent(Fixture("systemone-response.json"), Encoding.UTF8, "application/json") };

    private static Stub Replies(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));

    private static GatewayHttpClients Through(Stub stub) => new() { HandlerFactory = _ => stub };

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "routing", name));

    private static JudgeEndpoint Endpoint(string baseUrl = "https://api.typesafe.ai", string model = "jev-latest", int timeoutMs = 2000) =>
        new(baseUrl, "sk-test", model, TimeSpan.FromMilliseconds(timeoutMs));

    // ---------------------------------------------------------------- TypeSafe System One

    [Fact]
    public async Task SystemOne_Sends_The_State_Object_And_One_Choice_Question()
    {
        var stub = Replies(Fixture("systemone-response.json"));
        await new SystemOneJudge(Through(stub)).JudgeAsync(Endpoint(), Question, default);

        var seen = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Post, seen.Method);
        Assert.Equal("https://api.typesafe.ai/v1/systemone", seen.Url);
        Assert.Equal("Bearer sk-test", seen.Authorization);
        Assert.Equal("application/json", seen.ContentType);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Fixture("systemone-request.json")), seen.Body), seen.Body!.ToJsonString());
    }

    [Fact]
    public async Task SystemOne_Reads_Choice_Confidence_Probabilities_Model_And_Usage()
    {
        var verdict = await new SystemOneJudge(Through(Replies(Fixture("systemone-response.json")))).JudgeAsync(Endpoint(), Question, default);

        Assert.Equal("heavy", verdict.Choice);
        Assert.Equal(0.82, verdict.Confidence, 3);
        Assert.Equal(0.88, verdict.Probabilities["heavy"], 3);
        Assert.Equal("jev-1.13.0", verdict.JudgeModel);
        Assert.Equal(412, verdict.InputTokens);
        Assert.Equal(18, verdict.OutputTokens);
    }

    [Fact]
    public async Task A_Missing_Confidence_Is_Worked_Out_From_The_Probabilities()
    {
        // 3 options, top probability 0.7: (0.7 - 1/3) / (1 - 1/3) = 0.55
        var json = """{"model":"jev-1.13.0","answers":{"grade":{"type":"choice","choice":"standard","probabilities":{"light":0.1,"standard":0.7,"heavy":0.2}}}}""";
        var verdict = await new SystemOneJudge(Through(Replies(json))).JudgeAsync(Endpoint(), Question, default);

        Assert.Equal("standard", verdict.Choice);
        Assert.Equal(0.55, verdict.Confidence, 3);
        Assert.Equal(0, verdict.InputTokens);
    }

    [Fact]
    public async Task Confidence_Is_Clamped_Into_Zero_To_One()
    {
        var json = """{"answers":{"grade":{"choice":"light","confidence":1.7}}}""";
        var verdict = await new SystemOneJudge(Through(Replies(json))).JudgeAsync(Endpoint(), Question, default);
        Assert.Equal(1, verdict.Confidence);
    }

    [Theory]
    [InlineData("""{"answers":{"grade":{"choice":"enormous","confidence":0.9}}}""")] // not one of the grades
    [InlineData("""{"answers":{"grade":{"choice":"light"}}}""")] // neither confidence nor probabilities
    [InlineData("""{"answers":{"other":{"choice":"light","confidence":0.9}}}""")] // no answer to the question
    [InlineData("""{"model":"jev-1.13.0"}""")] // no answers at all
    [InlineData("""["not","an","object"]""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public async Task An_Answer_That_Is_Not_Usable_Is_A_Bad_Response(string body)
    {
        var e = await Assert.ThrowsAsync<RoutingJudgeException>(() =>
            new SystemOneJudge(Through(Replies(body))).JudgeAsync(Endpoint(), Question, default));
        Assert.Equal(JudgeFailure.BadResponse, e.Kind);
    }

    [Theory]
    [InlineData("https://api.typesafe.ai", "https://api.typesafe.ai/v1/systemone")]
    [InlineData("https://api.typesafe.ai/", "https://api.typesafe.ai/v1/systemone")]
    [InlineData("https://openrouter.ai/api", "https://openrouter.ai/api/v1/systemone")]
    [InlineData("https://openrouter.ai/api/v1", "https://openrouter.ai/api/v1/systemone")] // a /v1 already there is not doubled
    [InlineData("https://ai-gateway.vercel.sh/typesafe", "https://ai-gateway.vercel.sh/typesafe/v1/systemone")]
    [InlineData("http://127.0.0.1:4000", "http://127.0.0.1:4000/v1/systemone")]
    public async Task The_Address_Is_The_Base_Plus_V1_Systemone(string baseUrl, string expected)
    {
        var stub = Replies(Fixture("systemone-response.json"));
        await new SystemOneJudge(Through(stub)).JudgeAsync(Endpoint(baseUrl), Question, default);
        Assert.Equal(expected, Assert.Single(stub.Requests).Url);
    }

    [Theory]
    [InlineData(401, JudgeFailure.Auth)]
    [InlineData(403, JudgeFailure.Auth)]
    [InlineData(429, JudgeFailure.RateLimited)]
    [InlineData(500, JudgeFailure.Overloaded)]
    [InlineData(503, JudgeFailure.Overloaded)]
    [InlineData(529, JudgeFailure.Overloaded)] // TypeSafe's "temporarily overloaded"
    [InlineData(400, JudgeFailure.BadRequest)]
    [InlineData(413, JudgeFailure.BadRequest)]
    [InlineData(422, JudgeFailure.BadRequest)]
    public async Task Each_Failing_Status_Is_Told_Apart(int status, JudgeFailure expected)
    {
        var e = await Assert.ThrowsAsync<RoutingJudgeException>(() =>
            new SystemOneJudge(Through(Replies("""{"error":{"message":"nope"}}""", (HttpStatusCode)status))).JudgeAsync(Endpoint(), Question, default));
        Assert.Equal(expected, e.Kind);
        Assert.Equal(status, e.Status);
        Assert.Contains("nope", e.Message);
    }

    [Fact]
    public async Task An_Error_Message_Is_One_Short_Line_Without_The_Key()
    {
        var body = "{\"error\":\"" + new string('x', 500) + "\\nsecond line\"}";
        var e = await Assert.ThrowsAsync<RoutingJudgeException>(() =>
            new SystemOneJudge(Through(Replies(body, HttpStatusCode.BadRequest))).JudgeAsync(Endpoint(), Question, default));
        Assert.DoesNotContain('\n', e.Message);
        Assert.True(e.Message.Length < 260, e.Message.Length.ToString());
        Assert.DoesNotContain("sk-test", e.Message);
    }

    [Fact]
    public async Task A_Slow_Service_Is_A_Timeout_And_Not_A_Cancellation()
    {
        var stub = new Stub(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        });
        var e = await Assert.ThrowsAsync<RoutingJudgeException>(() =>
            new SystemOneJudge(Through(stub)).JudgeAsync(Endpoint(timeoutMs: 50), Question, default));
        Assert.Equal(JudgeFailure.Timeout, e.Kind);
    }

    [Fact]
    public async Task A_Judgement_Asks_For_HTTP2_And_Settles_For_What_The_Service_Speaks()
    {
        var stub = Replies(Fixture("systemone-response.json"));
        await new SystemOneJudge(Through(stub)).JudgeAsync(Endpoint(), Question, default);

        var seen = Assert.Single(stub.Requests);
        Assert.Equal(HttpVersion.Version20, seen.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, seen.VersionPolicy);
    }

    [Fact]
    public async Task A_Judgement_That_Has_To_Open_Its_Connection_Gets_The_Connect_Allowance_On_Top_Of_Its_Deadline()
    {
        // The same answer, 300 ms late for a 100 ms deadline: a timeout on a connection already open...
        var slow = new Stub(async (_, ct) =>
        {
            await Task.Delay(300, ct);
            return Answer();
        });
        var e = await Assert.ThrowsAsync<RoutingJudgeException>(() => new SystemOneJudge(Through(slow)).JudgeAsync(Endpoint(timeoutMs: 100), Question, default));
        Assert.Equal(JudgeFailure.Timeout, e.Kind);

        // ...but in time when the request first had to connect (the transport says so, as the judge client's connect callback does).
        var opened = 0;
        var connecting = new Stub(async (request, ct) =>
        {
            Assert.True(request.Options.TryGetValue(GatewayHttpClients.NewConnection, out var notify));
            notify();
            await Task.Delay(300, ct);
            return Answer();
        });
        var verdict = await new SystemOneJudge(Through(connecting))
            .JudgeAsync(Endpoint(timeoutMs: 100) with { OnNewConnection = () => opened++ }, Question, default);
        Assert.Equal("heavy", verdict.Choice);
        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task The_Judge_Client_Reports_Each_Connection_It_Opens_And_Reuses_It_For_The_Next_Judgement()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = 0;
        using var stop = new CancellationTokenSource();
        var server = ServeAsync(listener, Fixture("systemone-response.json"), () => Interlocked.Increment(ref accepted), stop.Token);
        using var clients = new GatewayHttpClients(); // the real judge client, no stand-in transport
        var opened = 0;
        var endpoint = Endpoint(baseUrl: $"http://127.0.0.1:{port}", timeoutMs: 5000) with { OnNewConnection = () => Interlocked.Increment(ref opened) };

        await new SystemOneJudge(clients).JudgeAsync(endpoint, Question, default);
        await new SystemOneJudge(clients).JudgeAsync(endpoint, Question, default);

        Assert.Equal(1, accepted);
        Assert.Equal(1, opened); // the second judgement went out on the connection the first one opened
        await stop.CancelAsync();
        listener.Stop();
        await server;
    }

    /// <summary>A minimal keep-alive HTTP/1.1 server: every request on every connection gets <paramref name="json"/>.</summary>
    private static async Task ServeAsync(TcpListener listener, string json, Action onAccept, CancellationToken ct)
    {
        var connections = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                onAccept();
                connections.Add(Task.Run(async () =>
                {
                    using var _ = client;
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    var body = Encoding.UTF8.GetBytes(json);
                    while (await reader.ReadLineAsync(ct) is { } line)
                    {
                        var length = 0;
                        for (; line.Length > 0; line = await reader.ReadLineAsync(ct) ?? "")
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line["Content-Length:".Length..].Trim());
                        var request = new char[length];
                        for (var read = 0; read < length;) read += await reader.ReadAsync(request.AsMemory(read), ct);
                        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n");
                        await stream.WriteAsync(head, ct);
                        await stream.WriteAsync(body, ct);
                    }
                }, ct));
            }
        }
        catch (OperationCanceledException)
        {
            // the test is over
        }
        catch (SocketException)
        {
            // the listener was stopped
        }
        try
        {
            await Task.WhenAll(connections);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException)
        {
            // a connection closed while it was being read
        }
    }

    [Fact]
    public async Task The_Callers_Own_Cancellation_Is_Not_Turned_Into_A_Failure()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SystemOneJudge(Through(Replies(Fixture("systemone-response.json")))).JudgeAsync(Endpoint(), Question, cancelled.Token));
    }

    [Fact]
    public async Task An_Unreachable_Service_Is_A_Network_Failure()
    {
        var stub = new Stub(_ => throw new HttpRequestException("connection refused"));
        var e = await Assert.ThrowsAsync<RoutingJudgeException>(() => new SystemOneJudge(Through(stub)).JudgeAsync(Endpoint(), Question, default));
        Assert.Equal(JudgeFailure.Network, e.Kind);
    }

    [Fact]
    public async Task An_Oversized_Answer_Is_Refused_Without_Reading_It_All()
    {
        var huge = new string('x', 1024 * 1024 + 10);
        var e = await Assert.ThrowsAsync<RoutingJudgeException>(() =>
            new SystemOneJudge(Through(Replies(huge))).JudgeAsync(Endpoint(), Question, default));
        Assert.Equal(JudgeFailure.BadResponse, e.Kind);
    }

    [Fact]
    public async Task Without_A_Key_No_Authorization_Header_Is_Sent()
    {
        var stub = Replies(Fixture("systemone-response.json"));
        await new SystemOneJudge(Through(stub)).JudgeAsync(Endpoint() with { ApiKey = null }, Question, default);
        Assert.Null(Assert.Single(stub.Requests).Authorization);
    }

    [Fact]
    public async Task Text_Outside_Ascii_Is_Sent_As_It_Is()
    {
        var stub = Replies(Fixture("systemone-response.json"));
        var question = Question with { State = Question.State with { Request = "修复登录测试 🚀" } };
        await new SystemOneJudge(Through(stub)).JudgeAsync(Endpoint(), question, default);
        Assert.Equal("修复登录测试 🚀", Assert.Single(stub.Requests).Body!["state"]!["request"]!.GetValue<string>());
    }

    [Fact]
    public async Task Without_Earlier_Requests_The_State_Has_No_Such_Field()
    {
        var stub = Replies(Fixture("systemone-response.json"));
        var question = Question with { State = Question.State with { Earlier = [] } };
        await new SystemOneJudge(Through(stub)).JudgeAsync(Endpoint(), question, default);
        Assert.Null(Assert.Single(stub.Requests).Body!["state"]!["earlier_requests"]);
    }

    // ---------------------------------------------------------------- OpenAI Decisions (draft dialect)

    [Fact]
    public async Task Decisions_Sends_The_State_As_Text_And_One_Choice_Question()
    {
        var stub = Replies(Fixture("decisions-response.json"));
        await new DecisionsJudge(Through(stub)).JudgeAsync(Endpoint("https://api.openai.com", "decision-model"), Question, default);

        var seen = Assert.Single(stub.Requests);
        Assert.Equal("https://api.openai.com/v1/decisions", seen.Url);
        Assert.Equal("Bearer sk-test", seen.Authorization);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Fixture("decisions-request.json")), seen.Body), seen.Body!.ToJsonString());
    }

    [Fact]
    public async Task Decisions_Reads_The_Answer_List_And_Its_Probability_List()
    {
        var verdict = await new DecisionsJudge(Through(Replies(Fixture("decisions-response.json"))))
            .JudgeAsync(Endpoint("https://api.openai.com", "decision-model"), Question, default);

        Assert.Equal("standard", verdict.Choice);
        Assert.Equal(0.55, verdict.Confidence, 3);
        Assert.Equal(0.7, verdict.Probabilities["standard"], 3);
        Assert.Equal("decision-model-2026-10-06", verdict.JudgeModel);
        Assert.Equal(300, verdict.InputTokens);
        Assert.Equal(12, verdict.OutputTokens);
    }

    [Fact]
    public async Task Decisions_Finds_Its_Answer_By_Name_Else_The_First_Choice_And_Accepts_A_Probability_Map()
    {
        var json = """
            {"answers":[
              {"type":"predicate","name":"other","probability":0.9},
              {"type":"choice","name":"grade","choice":"light","probabilities":{"light":0.8,"standard":0.1,"heavy":0.1}}
            ]}
            """;
        var named = await new DecisionsJudge(Through(Replies(json))).JudgeAsync(Endpoint("https://api.openai.com", "m"), Question, default);
        Assert.Equal("light", named.Choice);
        Assert.Equal(0.7, named.Confidence, 3); // (0.8 - 1/3) / (2/3)

        var unnamed = """{"answers":[{"type":"choice","choice":"heavy","confidence":0.4}]}""";
        var first = await new DecisionsJudge(Through(Replies(unnamed))).JudgeAsync(Endpoint("https://api.openai.com", "m"), Question, default);
        Assert.Equal("heavy", first.Choice);
        Assert.Equal(0.4, first.Confidence, 3);
    }

    [Theory]
    [InlineData("""{"answers":[{"type":"choice","name":"grade","choice":"enormous","confidence":0.9}]}""")]
    [InlineData("""{"answers":[]}""")]
    [InlineData("""{"answers":{"grade":{"choice":"light"}}}""")] // an object, not the list OpenAI sends
    [InlineData("""{}""")]
    public async Task Decisions_Rejects_What_Is_Not_Usable(string body)
    {
        var e = await Assert.ThrowsAsync<RoutingJudgeException>(() =>
            new DecisionsJudge(Through(Replies(body))).JudgeAsync(Endpoint("https://api.openai.com", "m"), Question, default));
        Assert.Equal(JudgeFailure.BadResponse, e.Kind);
    }

    // ---------------------------------------------------------------- registry

    [Fact]
    public void The_Registry_Finds_A_Judge_By_Its_Protocol()
    {
        var clients = new GatewayHttpClients();
        var registry = new RoutingJudgeRegistry([new SystemOneJudge(clients), new DecisionsJudge(clients)]);

        Assert.IsType<SystemOneJudge>(registry.Require(JudgeProtocols.TypeSafe));
        Assert.IsType<DecisionsJudge>(registry.Require(JudgeProtocols.OpenAI));
        Assert.Null(registry.Get("made-up"));
        Assert.Throws<InvalidOperationException>(() => registry.Require("made-up"));
    }

    [Fact]
    public void Every_Protocol_Has_A_Judge()
    {
        var clients = new GatewayHttpClients();
        var registry = new RoutingJudgeRegistry([new SystemOneJudge(clients), new DecisionsJudge(clients)]);
        foreach (var protocol in JudgeProtocols.All) Assert.NotNull(registry.Get(protocol));
    }
}
