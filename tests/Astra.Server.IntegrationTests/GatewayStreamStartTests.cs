using System.IO.Pipelines;
using System.Net;
using System.Text;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Requests;

namespace Astra.Server.IntegrationTests;

/// <summary>
/// How a pass-through stream starts: held until it proves to be SSE (an upstream may answer a stream request with one JSON
/// body under an SSE content type), except when it opens with a comment line, which no JSON body does.
/// </summary>
public class GatewayStreamStartTests
{
    private const string Completion =
        """{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"gpt-5","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15}}""";

    // The client asks for the usage chunk itself, so nothing is injected and the upstream bytes are forwarded as they are.
    private const string StreamRequest =
        """{"model":"gpt-5","stream":true,"stream_options":{"include_usage":true},"messages":[{"role":"user","content":"hi"}]}""";

    [Fact]
    public async Task A_json_body_under_an_sse_content_type_is_held_and_answered_whole()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Sse(Completion));

        using var response = await gw.PostAsync("/v1/chat/completions", StreamRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Completion, await response.Content.ReadAsStringAsync());
        Assert.Equal(RequestStatus.Success, (await gw.RecordOfAsync(response)).Status);
    }

    [Fact]
    public async Task A_heartbeat_before_the_first_event_reaches_the_client_at_once()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, seen =>
        {
            // A model still thinking: a comment line now, the events only later.
            var pipe = new Pipe();
            _ = Task.Run(async () =>
            {
                await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(": ping\n\n"));
                await release.Task;
                await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(
                    "data: {\"id\":\"chatcmpl-1\",\"model\":\"gpt-5\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}\n\n"
                    + "data: {\"id\":\"chatcmpl-1\",\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":3,\"total_tokens\":15}}\n\n"
                    + "data: [DONE]\n\n"));
                await pipe.Writer.CompleteAsync();
            });
            var content = new StreamContent(pipe.Reader.AsStream());
            content.Headers.ContentType = new("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(StreamRequest, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {gw.Key}");
        try
        {
            using var response = await gw.Host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await using var stream = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[256];
            var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(": ping", Encoding.UTF8.GetString(buffer, 0, read).Trim());
            release.TrySetResult();
            using var rest = new StreamReader(stream);
            Assert.Contains("[DONE]", await rest.ReadToEndAsync());
        }
        finally
        {
            release.TrySetResult();
        }
    }
}
