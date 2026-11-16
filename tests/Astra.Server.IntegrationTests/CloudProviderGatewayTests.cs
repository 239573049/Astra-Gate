using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Providers.Cloud;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>Amazon Bedrock with an IAM key (SigV4) and Google Vertex AI with a service account, through the whole gateway.</summary>
public class CloudProviderGatewayTests
{
    private const string AnthropicReply =
        """{"id":"msg_1","type":"message","role":"assistant","model":"m","content":[{"type":"text","text":"OK"}],"stop_reason":"end_turn","usage":{"input_tokens":10,"output_tokens":2}}""";

    private const string ChatReply =
        """{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15}}""";

    private const string GeminiReply = """{"candidates":[{"content":{"role":"model","parts":[{"text":"OK"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":3,"candidatesTokenCount":1}}""";

    private const string Messages = """{"model":"{MODEL}","max_tokens":16,"stream":false,"messages":[{"role":"user","content":"hi"}]}""";

    private static readonly string IamKey =
        """{"accessKeyId":"AKIDEXAMPLE","secretAccessKey":"wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY","sessionToken":"session/token=="}""";

    private static string ServiceAccountKey()
    {
        using var rsa = RSA.Create(2048);
        return new JsonObject
        {
            ["type"] = "service_account", ["project_id"] = "from-key-file",
            ["client_email"] = "astra@demo-project.iam.gserviceaccount.com", ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
        }.ToJsonString();
    }

    private static async Task BecomeAsync(GatewayFixture gw, string scheme, string secret, JsonObject? settings, params ProviderEndpoint[] endpoints)
    {
        var protector = gw.Host.App.Services.GetRequiredService<ISecretProtector>();
        gw.Provider.AuthScheme = scheme;
        gw.Provider.ApiKeyEnc = protector.Protect(secret);
        gw.Provider.Endpoints = [.. endpoints];
        gw.Provider.PreferredUpstreamProtocols = endpoints.Select(e => e.Protocol).ToList();
        gw.Provider.Settings = settings ?? new JsonObject();
        await gw.Host.Db.Providers.UpdateAsync(gw.Provider);
    }

    private static ProviderEndpoint Ep(ApiProtocol p, string url) => new() { Protocol = p, BaseUrl = url };

    private static HttpResponseMessage Answer(FakeUpstream.Seen seen) => seen.Url.AbsolutePath switch
    {
        var p when p.EndsWith("/token", StringComparison.Ordinal) => FakeUpstream.Json("""{"access_token":"ya29.fake","expires_in":3600}"""),
        var p when p.EndsWith("/chat/completions", StringComparison.Ordinal) => FakeUpstream.Json(ChatReply),
        var p when p.Contains("generateContent", StringComparison.Ordinal) => FakeUpstream.Json(GeminiReply),
        _ => FakeUpstream.Json(AnthropicReply),
    };

    /// <summary>Signs the request the way the gateway should have, from what the upstream received.</summary>
    private static string ExpectedAuthorization(FakeUpstream.Seen seen, AwsCredentials credentials, string region, string service)
    {
        var at = DateTimeOffset.ParseExact(seen.Headers["x-amz-date"], "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        using var again = new HttpRequestMessage(seen.Method, seen.Url);
        AwsSigV4.Sign(again, Encoding.UTF8.GetBytes(seen.Body), credentials, region, service, at);
        return again.Headers.GetValues("Authorization").Single();
    }

    [Fact]
    public async Task Bedrock_Iam_Requests_Are_Signed_Over_The_Bytes_Actually_Sent()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.ClaudeCode, ApiProtocol.Anthropic, Answer);
        await BecomeAsync(gw, AuthSchemes.AwsSigV4, IamKey, null,
            Ep(ApiProtocol.Anthropic, "https://bedrock-runtime.eu-west-1.amazonaws.com/anthropic"),
            Ep(ApiProtocol.OpenAIChat, "https://bedrock-runtime.eu-west-1.amazonaws.com/openai/v1"));

        var response = await gw.PostAsync("/v1/messages", Messages.Replace("{MODEL}", "global.anthropic.claude-sonnet-5-5"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var seen = Assert.Single(gw.Upstream.Requests);
        Assert.Equal("https://bedrock-runtime.eu-west-1.amazonaws.com/anthropic/v1/messages", seen.Url.ToString());
        var authorization = seen.Headers["authorization"];
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/", authorization);
        Assert.Contains("/eu-west-1/bedrock/aws4_request", authorization);
        Assert.Contains("SignedHeaders=host;x-amz-date;x-amz-security-token,", authorization);
        Assert.Equal("session/token==", seen.Headers["x-amz-security-token"]);
        Assert.Equal(ExpectedAuthorization(seen, new AwsCredentials("AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "session/token=="), "eu-west-1", "bedrock"), authorization);
        // no key of any other scheme rides along
        Assert.False(seen.Headers.ContainsKey("x-api-key"));
        Assert.DoesNotContain("wJalrXUtnFEMI", string.Join("|", seen.Headers.Values));
    }

    [Fact]
    public async Task A_Messages_Client_Asking_For_A_Non_Claude_Bedrock_Model_Is_Translated_To_Chat_Completions()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.ClaudeCode, ApiProtocol.Anthropic, Answer);
        await BecomeAsync(gw, AuthSchemes.AwsSigV4, IamKey, null,
            Ep(ApiProtocol.Anthropic, "https://bedrock-runtime.us-east-1.amazonaws.com/anthropic"),
            Ep(ApiProtocol.OpenAIChat, "https://bedrock-runtime.us-east-1.amazonaws.com/openai/v1"));

        var response = await gw.PostAsync("/v1/messages", Messages.Replace("{MODEL}", "openai.gpt-oss-120b-1:0"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"text\":\"OK\"", await response.Content.ReadAsStringAsync());

        var seen = Assert.Single(gw.Upstream.Requests);
        Assert.Equal("https://bedrock-runtime.us-east-1.amazonaws.com/openai/v1/chat/completions", seen.Url.ToString());
        Assert.Equal("openai.gpt-oss-120b-1:0", seen.Json["model"]!.GetValue<string>());
        Assert.Equal(ExpectedAuthorization(seen, new AwsCredentials("AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "session/token=="), "us-east-1", "bedrock"),
            seen.Headers["authorization"]);
    }

    [Fact]
    public async Task A_Bedrock_Api_Key_Is_A_Plain_Bearer()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.ClaudeCode, ApiProtocol.Anthropic, Answer);
        await BecomeAsync(gw, AuthSchemes.Bearer, "ABSKQmVkcm9ja0FQSUtleQ==", null,
            Ep(ApiProtocol.Anthropic, "https://bedrock-runtime.us-east-1.amazonaws.com/anthropic"),
            Ep(ApiProtocol.OpenAIChat, "https://bedrock-runtime.us-east-1.amazonaws.com/openai/v1"));
        Assert.Equal(HttpStatusCode.OK, (await gw.PostAsync("/v1/messages", Messages.Replace("{MODEL}", "global.anthropic.claude-opus-5"))).StatusCode);
        Assert.Equal("Bearer ABSKQmVkcm9ja0FQSUtleQ==", Assert.Single(gw.Upstream.Requests).Headers["authorization"]);
    }

    [Fact]
    public async Task A_Host_That_Names_No_Region_Needs_The_Providers_Own_Scope()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.ClaudeCode, ApiProtocol.Anthropic, Answer);
        await BecomeAsync(gw, AuthSchemes.AwsSigV4, IamKey, null, Ep(ApiProtocol.Anthropic, "https://bedrock.internal.example/anthropic"));
        var refused = await gw.PostAsync("/v1/messages", Messages.Replace("{MODEL}", "global.anthropic.claude-opus-5"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Empty(gw.Upstream.Requests);

        await BecomeAsync(gw, AuthSchemes.AwsSigV4, IamKey, new JsonObject { ["aws"] = new JsonObject { ["region"] = "us-west-2", ["service"] = "bedrock" } },
            Ep(ApiProtocol.Anthropic, "https://bedrock.internal.example/anthropic"));
        Assert.Equal(HttpStatusCode.OK, (await gw.PostAsync("/v1/messages", Messages.Replace("{MODEL}", "global.anthropic.claude-opus-5"))).StatusCode);
        Assert.Contains("/us-west-2/bedrock/aws4_request", Assert.Single(gw.Upstream.Requests).Headers["authorization"]);
    }

    [Fact]
    public async Task Vertex_Claude_Goes_To_RawPredict_With_A_Service_Account_Token_And_A_Shaped_Body()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.ClaudeCode, ApiProtocol.Anthropic, Answer);
        await BecomeAsync(gw, AuthSchemes.GcpServiceAccount, ServiceAccountKey(),
            new JsonObject { ["vertex"] = new JsonObject { ["project"] = "demo-project", ["location"] = "us-central1" } },
            Ep(ApiProtocol.Gemini, "https://aiplatform.googleapis.com"), Ep(ApiProtocol.Anthropic, "https://aiplatform.googleapis.com"),
            Ep(ApiProtocol.OpenAIChat, "https://aiplatform.googleapis.com"));

        var response = await gw.PostAsync("/v1/messages", Messages.Replace("{MODEL}", "claude-sonnet-5-5"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal("https://oauth2.googleapis.com/token", gw.Upstream.Requests[0].Url.ToString());
        var seen = gw.Upstream.Requests[1];
        Assert.Equal("https://us-central1-aiplatform.googleapis.com/v1/projects/demo-project/locations/us-central1/publishers/anthropic/models/claude-sonnet-5-5:rawPredict",
            seen.Url.ToString());
        Assert.Equal("Bearer ya29.fake", seen.Headers["authorization"]);
        Assert.False(seen.Json.ContainsKey("model"));
        Assert.Equal("vertex-2023-10-16", seen.Json["anthropic_version"]!.GetValue<string>());
        Assert.False(seen.Headers.ContainsKey("anthropic-version"));
        Assert.Equal("hi", seen.Json["messages"]![0]!["content"]!.GetValue<string>());

        // the token is minted once for the next request
        Assert.Equal(HttpStatusCode.OK, (await gw.PostAsync("/v1/messages", Messages.Replace("{MODEL}", "claude-sonnet-5-5"))).StatusCode);
        Assert.Single(gw.Upstream.Requests, r => r.Url.AbsolutePath == "/token");
    }

    [Fact]
    public async Task Vertex_Gemini_And_Partner_Models_Take_Their_Own_Paths()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.ClaudeCode, ApiProtocol.Anthropic, Answer);
        await BecomeAsync(gw, AuthSchemes.GcpServiceAccount, ServiceAccountKey(),
            new JsonObject { ["vertex"] = new JsonObject { ["project"] = "demo-project", ["location"] = "global" } },
            Ep(ApiProtocol.Gemini, "https://aiplatform.googleapis.com"), Ep(ApiProtocol.Anthropic, "https://aiplatform.googleapis.com"),
            Ep(ApiProtocol.OpenAIChat, "https://aiplatform.googleapis.com"));

        // a Messages client asking for Gemini is translated to generateContent
        Assert.Equal(HttpStatusCode.OK, (await gw.PostAsync("/v1/messages", Messages.Replace("{MODEL}", "gemini-2.5-pro"))).StatusCode);
        var gemini = gw.Upstream.Requests.Single(r => r.Url.AbsolutePath.Contains("generateContent", StringComparison.Ordinal));
        Assert.Equal("https://aiplatform.googleapis.com/v1/projects/demo-project/locations/global/publishers/google/models/gemini-2.5-pro:generateContent", gemini.Url.ToString());
        Assert.Equal("Bearer ya29.fake", gemini.Headers["authorization"]);

        // …and a partner model is translated to the project's OpenAI-compatible chat completions
        Assert.Equal(HttpStatusCode.OK, (await gw.PostAsync("/v1/messages", Messages.Replace("{MODEL}", "deepseek-ai/deepseek-v4.1-flash-maas"))).StatusCode);
        var partner = gw.Upstream.Requests.Single(r => r.Url.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal));
        Assert.Equal("https://aiplatform.googleapis.com/v1/projects/demo-project/locations/global/endpoints/openapi/chat/completions", partner.Url.ToString());
        Assert.Equal("deepseek-ai/deepseek-v4.1-flash-maas", partner.Json["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_Refused_Service_Account_Is_A_Gateway_Error_That_Never_Reaches_The_Model_Endpoint()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.ClaudeCode, ApiProtocol.Anthropic,
            seen => seen.Url.AbsolutePath == "/token" ? FakeUpstream.Json("""{"error":"invalid_grant","error_description":"Invalid JWT Signature."}""", HttpStatusCode.BadRequest) : Answer(seen));
        await BecomeAsync(gw, AuthSchemes.GcpServiceAccount, ServiceAccountKey(),
            new JsonObject { ["vertex"] = new JsonObject { ["project"] = "demo-project" } }, Ep(ApiProtocol.Anthropic, "https://aiplatform.googleapis.com"));
        var response = await gw.PostAsync("/v1/messages", Messages.Replace("{MODEL}", "claude-sonnet-5-5"));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid_grant", text);
        Assert.DoesNotContain("PRIVATE KEY", text);
        Assert.DoesNotContain(gw.Upstream.Requests, r => r.Url.AbsolutePath.Contains("rawPredict", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Azure_Takes_The_Key_In_The_Api_Key_Header_Not_As_A_Bearer()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIChat, Answer, AuthSchemes.ApiKeyHeader);
        var response = await gw.PostAsync("/v1/chat/completions", """{"model":"my-deployment","messages":[{"role":"user","content":"hi"}]}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = Assert.Single(gw.Upstream.Requests);
        Assert.Equal("sk-upstream-secret", seen.Headers["api-key"]);
        Assert.False(seen.Headers.ContainsKey("authorization"));
    }
}
