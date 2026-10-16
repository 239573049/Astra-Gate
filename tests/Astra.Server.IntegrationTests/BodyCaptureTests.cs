using System.Net;
using System.Text;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Gateway.Pipeline;
using Astra.Server.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>Debug body capture as the request detail shows it: the start of each part, and each whole part on request.</summary>
public class BodyCaptureTests
{
    [Fact]
    public async Task Detail_Carries_The_Start_Of_A_Large_Body_And_The_Whole_Part_Is_Served_Apart()
    {
        var upstream = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "responses", "stream-custom-tool.sse"));
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIResponses, _ => FakeUpstream.Sse(upstream));
        await gw.Host.App.Services.GetRequiredService<SettingsService>().UpdateAsync(s => s.DebugBodies = true);

        // Multi-byte text, so the cut must land on a character boundary.
        var input = string.Concat(Enumerable.Repeat("汉字 abc ", 20_000));
        var sent = $$"""{"model":"gpt-5","input":"{{input}}","stream":true}""";
        var response = await gw.PostAsync("/v1/responses", sent);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await response.Content.ReadAsStringAsync();
        var record = await gw.RecordOfAsync(response);

        var bodies = (await gw.Host.GetJsonAsync($"/api/requests/{record.Id}"))["bodies"]!;
        Assert.Equal(RequestEndpoints.BodyPreviewBytes, bodies["previewLimit"]!.GetValue<int>());
        var preview = bodies["clientRequest"]!.GetValue<string>();
        var previewBytes = Encoding.UTF8.GetByteCount(preview);
        Assert.InRange(previewBytes, RequestEndpoints.BodyPreviewBytes - 3, RequestEndpoints.BodyPreviewBytes);
        Assert.StartsWith(preview, sent, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetByteCount(sent), bodies["sizes"]!["clientRequest"]!.GetValue<long>());

        var whole = await gw.Host.Client.GetAsync($"/api/requests/{record.Id}/bodies/client-request");
        Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
        Assert.Equal(sent, await whole.Content.ReadAsStringAsync());

        // The streamed upstream response is captured as the upstream's own bytes.
        var captured = await gw.Host.Client.GetStringAsync($"/api/requests/{record.Id}/bodies/upstream-response");
        Assert.Equal(upstream, captured);
        Assert.Equal(upstream, bodies["upstreamResponse"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(bodies["clientResponse"]?.GetValue<string>()));
    }

    [Fact]
    public async Task Body_Part_Endpoint_Refuses_Unknown_Parts_And_Requests()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIResponses, _ => FakeUpstream.Json("{}"));
        Assert.Equal(HttpStatusCode.NotFound, (await gw.Host.Client.GetAsync("/api/requests/nope/bodies/client-request")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await gw.Host.Client.GetAsync("/api/requests/nope/bodies/..%2Fconfig")).StatusCode);
    }
}
