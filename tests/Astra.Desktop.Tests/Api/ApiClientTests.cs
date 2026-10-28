using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Tests.Api;

public class ApiClientTests
{
    [Fact]
    public async Task Get_decodes_dto_and_sends_no_admin_header()
    {
        using var server = new FakeServer(c => FakeServer.Json(c, 200, """{"version":"1.2.3","apiVersion":"1.0"}"""));
        using var api = new ApiClient(server.Url);
        var v = await api.GetAsync<VersionDto>("/api/version");
        Assert.Equal(new VersionDto("1.2.3", "1.0"), v);
        var seen = Assert.Single(server.Requests);
        Assert.Null(seen.Admin);
        Assert.Contains("application/json", seen.Accept);
    }

    [Fact]
    public async Task Mutating_calls_carry_admin_header_and_camelCase_json_body()
    {
        using var server = new FakeServer(c => FakeServer.Json(c, 200, """{"ok":true}"""));
        using var api = new ApiClient(server.Url);
        var body = new JsonObject { ["someField"] = 1 };
        var res = await api.PostAsync<JsonObject, JsonObject>("/api/x", body);
        await api.PutAsync("/api/x", body);
        await api.PatchAsync<JsonObject, JsonObject>("/api/x", body);
        await api.DeleteAsync("/api/x");
        await api.PostAsync("/api/y");
        Assert.True((bool)res!["ok"]!);
        Assert.Equal(5, server.Requests.Count);
        Assert.All(server.Requests, r => Assert.Equal("1", r.Admin));
        Assert.Contains(server.Requests, r => r.Method == "POST" && r.Body == """{"someField":1}""");
    }

    [Fact]
    public async Task Error_body_maps_to_ApiException_with_details()
    {
        using var server = new FakeServer(c => FakeServer.Json(c, 400, """{"error":"bad input","details":{"problems":[{"code":"x"}]}}"""));
        using var api = new ApiClient(server.Url);
        var e = await Assert.ThrowsAsync<ApiException>(() => api.PostAsync("/api/x", new JsonObject()));
        Assert.Equal(400, e.Status);
        Assert.Equal("bad input", e.Message);
        Assert.True(e.IsClientError);
        Assert.False(e.IsNetwork);
        Assert.Equal("x", (string?)e.Details!["problems"]![0]!["code"]);
    }

    [Theory]
    [InlineData("plain failure", "plain failure")]
    [InlineData("", "HTTP 500")]
    [InlineData("\"quoted\"", "quoted")]
    [InlineData("{}", "HTTP 500")]
    public async Task Error_message_fallbacks(string body, string expected)
    {
        using var server = new FakeServer(c => FakeServer.Json(c, 500, body));
        using var api = new ApiClient(server.Url);
        var e = await Assert.ThrowsAsync<ApiException>(() => api.GetAsync<VersionDto>("/api/version"));
        Assert.Equal(expected, e.Message);
        Assert.Equal(500, e.Status);
    }

    [Fact]
    public async Task No_content_yields_default()
    {
        using var server = new FakeServer(c => { c.Response.StatusCode = 204; return Task.CompletedTask; });
        using var api = new ApiClient(server.Url);
        Assert.Null(await api.GetAsync<VersionDto>("/api/version"));
        await api.DeleteAsync("/api/x");
    }

    [Fact]
    public async Task Unreachable_server_is_a_network_error()
    {
        int port;
        using (var s = new FakeServer(_ => Task.CompletedTask)) port = s.Port;
        using var api = new ApiClient($"http://127.0.0.1:{port}");
        var e = await Assert.ThrowsAsync<ApiException>(() => api.GetAsync<VersionDto>("/api/version"));
        Assert.True(e.IsNetwork);
    }

    [Fact]
    public async Task Timeout_is_status_zero_but_caller_cancel_stays_cancellation()
    {
        using var server = new FakeServer(async c => { await Task.Delay(2000); await FakeServer.Json(c, 200, "{}"); });
        using var api = new ApiClient(server.Url) { Timeout = TimeSpan.FromMilliseconds(150) };
        var e = await Assert.ThrowsAsync<ApiException>(() => api.GetAsync<VersionDto>("/api/version"));
        Assert.True(e.IsNetwork);
        Assert.Contains("timed out", e.Message);

        api.Timeout = TimeSpan.FromSeconds(10);
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.GetAsync<VersionDto>("/api/version", cts.Token));
    }

    [Fact]
    public async Task Base_url_follows_port_drift()
    {
        using var a = new FakeServer(c => FakeServer.Json(c, 200, """{"version":"a","apiVersion":"1.0"}"""));
        using var b = new FakeServer(c => FakeServer.Json(c, 200, """{"version":"b","apiVersion":"1.0"}"""));
        using var api = new ApiClient(a.Url + "/");
        Assert.Equal("a", (await api.GetAsync<VersionDto>("/api/version"))!.Version);
        api.SetPort(b.Port);
        Assert.Equal("b", (await api.GetAsync<VersionDto>("/api/version"))!.Version);
    }

    [Fact]
    public async Task GetText_returns_the_exact_text()
    {
        using var server = new FakeServer(c => FakeServer.Json(c, 200, "{ \"a\":  1 }"));
        using var api = new ApiClient(server.Url);
        Assert.Equal("{ \"a\":  1 }", await api.GetTextAsync("/api/x"));
    }

    [Fact]
    public void Qs_skips_empty_and_formats_like_web()
    {
        Assert.Equal("", ApiClient.Qs(("a", null), ("b", "")));
        Assert.Equal("?page=2&ok=true&q=a%20b", ApiClient.Qs(("page", 2), ("none", null), ("ok", true), ("q", "a b")));
        Assert.Equal("a%2Fb", ApiClient.Enc("a/b"));
    }
}
