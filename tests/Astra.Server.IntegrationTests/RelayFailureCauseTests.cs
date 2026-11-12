using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Astra.Gateway.Pipeline;

namespace Astra.Server.IntegrationTests;

/// <summary>Issue #39: an upstream failure names its cause, and a failed model list is told apart from a failed generation.</summary>
public sealed class RelayFailureCauseTests
{
    private static HttpRequestException Ended() =>
        new(HttpRequestError.ResponseEnded, "An error occurred while sending the request.",
            new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely."));

    [Fact]
    public void Describe_names_the_inner_cause_not_the_generic_text()
    {
        Assert.Equal("The response ended prematurely. (ResponseEnded)", UpstreamFailure.Describe(Ended()));
    }

    [Fact]
    public void Describe_leaves_out_the_upstream_address()
    {
        var refused = new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (internal.example:8443)",
            new SocketException((int)SocketError.ConnectionRefused));
        var text = UpstreamFailure.Describe(refused);
        Assert.DoesNotContain("internal.example", text);
        Assert.Contains("(ConnectionError)", text);
    }

    [Fact]
    public void Describe_falls_back_to_the_outer_message_without_an_inner_one()
    {
        Assert.Equal("boom", UpstreamFailure.Describe(new HttpRequestException("boom")));
    }

    [Fact]
    public async Task A_failed_generation_names_its_cause()
    {
        var upstream = new RelayUpstream
        {
            Respond = (request, _) => request.Uri.AbsolutePath == "/models"
                ? Task.FromResult(RelayUpstream.Default(request))
                : throw Ended(),
        };
        await using var relay = await RelayHost.StartAsync(upstream);
        using var response = await relay.SendAsync();
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("无法连接上游", body);
        Assert.Contains("ResponseEnded", body);
    }

    [Fact]
    public async Task A_failed_model_list_says_so()
    {
        var upstream = new RelayUpstream
        {
            Respond = (request, _) => request.Uri.AbsolutePath == "/models"
                ? throw Ended()
                : Task.FromResult(RelayUpstream.Default(request)),
        };
        await using var relay = await RelayHost.StartAsync(upstream);
        using var response = await relay.SendAsync();
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("无法获取上游模型列表", body);
        Assert.Contains("ResponseEnded", body);
        Assert.DoesNotContain(upstream.Requests, request => request.Uri.AbsolutePath == "/chat/completions");
    }
}
