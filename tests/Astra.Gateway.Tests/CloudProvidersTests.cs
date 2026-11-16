using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Models;
using Astra.Gateway.Pipeline;

namespace Astra.Gateway.Tests;

/// <summary>Which protocol, address and body shape Amazon Bedrock and Google Vertex AI want for a model.</summary>
public class CloudProvidersTests
{
    private static ProviderEndpoint Endpoint(ApiProtocol p, string url) => new() { Protocol = p, BaseUrl = url };

    private static Provider Bedrock(string region = "us-east-1", string scheme = AuthSchemes.Bearer) => new()
    {
        Name = "Bedrock", AuthScheme = scheme,
        Endpoints =
        [
            Endpoint(ApiProtocol.OpenAIResponses, $"https://bedrock-runtime.{region}.amazonaws.com/openai/v1"),
            Endpoint(ApiProtocol.OpenAIChat, $"https://bedrock-runtime.{region}.amazonaws.com/openai/v1"),
            Endpoint(ApiProtocol.Anthropic, $"https://bedrock-runtime.{region}.amazonaws.com/anthropic"),
        ],
    };

    private static Provider Mantle() => new()
    {
        Name = "Mantle",
        Endpoints =
        [
            Endpoint(ApiProtocol.OpenAIChat, "https://bedrock-mantle.us-east-1.api.aws/v1"),
            Endpoint(ApiProtocol.OpenAIResponses, "https://bedrock-mantle.us-east-1.api.aws/v1"),
            Endpoint(ApiProtocol.Anthropic, "https://bedrock-mantle.us-east-1.api.aws/anthropic"),
        ],
    };

    private static Provider Vertex(string project = "demo-project", string location = "global", string host = "https://aiplatform.googleapis.com") => new()
    {
        Name = "Vertex", AuthScheme = AuthSchemes.GcpServiceAccount,
        Endpoints = [Endpoint(ApiProtocol.Gemini, host), Endpoint(ApiProtocol.Anthropic, host), Endpoint(ApiProtocol.OpenAIChat, host)],
        Settings = new JsonObject { ["vertex"] = new JsonObject { ["project"] = project, ["location"] = location } },
    };

    [Theory]
    [InlineData("global.anthropic.claude-sonnet-5-5", ApiProtocol.Anthropic)]
    [InlineData("anthropic.claude-3-5-haiku-20241022-v1:0", ApiProtocol.Anthropic)]
    [InlineData("us.anthropic.claude-opus-4-1-20250805-v1:0", ApiProtocol.Anthropic)]
    [InlineData("openai.gpt-oss-120b-1:0", ApiProtocol.OpenAIChat)]
    [InlineData("qwen.qwen3-coder-480b-a35b-v1:0", ApiProtocol.OpenAIChat)]
    [InlineData("deepseek.v3.2", ApiProtocol.OpenAIChat)]
    [InlineData("global.openai.gpt-6-sol", ApiProtocol.OpenAIResponses)]
    public void Bedrock_Models_Go_Over_The_Protocol_Their_Maker_Speaks_There(string model, ApiProtocol first)
    {
        Assert.Equal(first, CloudProviders.ProtocolsFor(Bedrock(), model)[0]);
        Assert.Equal(first, CloudProviders.ProtocolsFor(Mantle(), model)[0]);
    }

    [Theory]
    [InlineData("gemini-2.5-pro", ApiProtocol.Gemini)]
    [InlineData("claude-sonnet-5-5", ApiProtocol.Anthropic)]
    [InlineData("claude-sonnet-4-5@20250929", ApiProtocol.Anthropic)]
    [InlineData("deepseek-ai/deepseek-v4.1-flash-maas", ApiProtocol.OpenAIChat)]
    [InlineData("openai/gpt-oss-120b-maas", ApiProtocol.OpenAIChat)]
    public void Vertex_Models_Go_Over_The_Protocol_Their_Publisher_Speaks_There(string model, ApiProtocol expected) =>
        Assert.Equal([expected], CloudProviders.ProtocolsFor(Vertex(), model));

    [Fact]
    public void Other_Providers_Have_No_Cloud_Rule()
    {
        var openai = new Provider { Name = "OpenAI", Endpoints = [Endpoint(ApiProtocol.OpenAIChat, "https://api.openai.com/v1")] };
        Assert.Empty(CloudProviders.ProtocolsFor(openai, "gpt-5"));
        Assert.False(CloudProviders.IsBedrock(openai));
        Assert.False(CloudProviders.IsVertex(openai));
    }

    [Fact]
    public void A_Chosen_Endpoint_Follows_The_Model_Protocol_Not_The_Clients()
    {
        // Claude Code (Messages) asking for a gpt-oss model must reach chat completions, never /anthropic
        var bedrock = Bedrock();
        var endpoint = GatewayRouter.SelectEndpoint(bedrock, ApiProtocol.Anthropic, CloudProviders.ProtocolsFor(bedrock, "openai.gpt-oss-120b-1:0"));
        Assert.Equal(ApiProtocol.OpenAIChat, endpoint.Protocol);
        // …while Claude stays on Messages for a Messages client
        endpoint = GatewayRouter.SelectEndpoint(bedrock, ApiProtocol.Anthropic, CloudProviders.ProtocolsFor(bedrock, "global.anthropic.claude-opus-5"));
        Assert.Equal(ApiProtocol.Anthropic, endpoint.Protocol);
    }

    [Fact]
    public void Bedrock_Urls_Are_The_Plain_Compatible_Endpoints()
    {
        var bedrock = Bedrock("eu-west-1");
        Assert.Null(CloudProviders.Url(bedrock, bedrock.Endpoints[1], "openai.gpt-oss-120b-1:0", false)); // the usual builder applies
        Assert.Equal("https://bedrock-runtime.eu-west-1.amazonaws.com/openai/v1/chat/completions",
            UpstreamUrls.For(bedrock.Endpoints[1], "x", false));
        Assert.Equal("https://bedrock-runtime.eu-west-1.amazonaws.com/anthropic/v1/messages", UpstreamUrls.For(bedrock.Endpoints[2], "x", true));
    }

    [Fact]
    public void Mantle_Serves_Closed_GPT_And_Grok_On_The_OpenAI_Route()
    {
        var mantle = Mantle();
        Assert.Equal("https://bedrock-mantle.us-east-1.api.aws/openai/v1/responses",
            CloudProviders.Url(mantle, mantle.Endpoints[1], "openai.gpt-6-sol", false));
        Assert.Equal("https://bedrock-mantle.us-east-1.api.aws/openai/v1/chat/completions",
            CloudProviders.Url(mantle, mantle.Endpoints[0], "xai.grok-4.3", false));
        Assert.Null(CloudProviders.Url(mantle, mantle.Endpoints[0], "openai.gpt-oss-120b", false));
        Assert.Null(CloudProviders.Url(mantle, mantle.Endpoints[2], "anthropic.claude-sonnet-5", false));
    }

    [Fact]
    public void Vertex_Urls_Carry_Project_Location_And_Publisher()
    {
        var v = Vertex();
        Assert.Equal("https://aiplatform.googleapis.com/v1/projects/demo-project/locations/global/publishers/google/models/gemini-2.5-pro:generateContent",
            CloudProviders.Url(v, v.Endpoints[0], "gemini-2.5-pro", false));
        Assert.Equal("https://aiplatform.googleapis.com/v1/projects/demo-project/locations/global/publishers/google/models/gemini-2.5-pro:streamGenerateContent?alt=sse",
            CloudProviders.Url(v, v.Endpoints[0], "models/gemini-2.5-pro", true));
        Assert.Equal("https://aiplatform.googleapis.com/v1/projects/demo-project/locations/global/publishers/anthropic/models/claude-sonnet-5-5:streamRawPredict",
            CloudProviders.Url(v, v.Endpoints[1], "claude-sonnet-5-5", true));
        Assert.Equal("https://aiplatform.googleapis.com/v1/projects/demo-project/locations/global/publishers/anthropic/models/claude-sonnet-5-5:rawPredict",
            CloudProviders.Url(v, v.Endpoints[1], "claude-sonnet-5-5", false));
        Assert.Equal("https://aiplatform.googleapis.com/v1/projects/demo-project/locations/global/endpoints/openapi/chat/completions",
            CloudProviders.Url(v, v.Endpoints[2], "deepseek-ai/deepseek-v4.1-flash-maas", true));
    }

    [Theory]
    [InlineData("global", "aiplatform.googleapis.com")]
    [InlineData("us", "aiplatform.us.rep.googleapis.com")]
    [InlineData("eu", "aiplatform.eu.rep.googleapis.com")]
    [InlineData("us-central1", "us-central1-aiplatform.googleapis.com")]
    [InlineData("europe-west4", "europe-west4-aiplatform.googleapis.com")]
    public void Vertex_Host_Follows_The_Location(string location, string host)
    {
        Assert.Equal(host, CloudProviders.VertexHost(location));
        var v = Vertex(location: location);
        Assert.StartsWith($"https://{host}/v1/projects/demo-project/locations/{location}/", CloudProviders.Url(v, v.Endpoints[0], "gemini-2.5-pro", false));
    }

    [Fact]
    public void An_Endpoint_Off_Googles_Global_Host_Is_The_Root_As_Given()
    {
        var v = Vertex(host: "https://us-central1-aiplatform.googleapis.com", location: "us-central1");
        Assert.StartsWith("https://us-central1-aiplatform.googleapis.com/v1/projects/", CloudProviders.Url(v, v.Endpoints[0], "gemini-2.5-pro", false));
    }

    [Fact]
    public void Vertex_Without_A_Project_Is_Refused_Before_Anything_Is_Sent()
    {
        var v = Vertex(project: "");
        var e = Assert.Throws<GatewayException>(() => CloudProviders.Url(v, v.Endpoints[0], "gemini-2.5-pro", false));
        Assert.Equal(400, e.Status);
    }

    [Fact]
    public void Anthropic_On_Vertex_Drops_The_Model_And_Names_The_Api_Version_In_The_Body()
    {
        var v = Vertex();
        var tree = JsonNode.Parse("""{"model":"claude-sonnet-5-5","max_tokens":16,"stream":true,"messages":[]}""")!.AsObject();
        Assert.True(CloudProviders.ShapeBody(v, ApiProtocol.Anthropic, tree));
        Assert.False(tree.ContainsKey("model"));
        Assert.Equal("vertex-2023-10-16", tree["anthropic_version"]!.GetValue<string>());
        Assert.True(tree["stream"]!.GetValue<bool>());
        // a version the client named stays
        var named = JsonNode.Parse("""{"model":"m","anthropic_version":"vertex-2023-10-16"}""")!.AsObject();
        named["anthropic_version"] = "custom";
        CloudProviders.ShapeBody(v, ApiProtocol.Anthropic, named);
        Assert.Equal("custom", named["anthropic_version"]!.GetValue<string>());
        // the other protocols and other providers are left alone
        var gemini = JsonNode.Parse("""{"contents":[]}""")!.AsObject();
        Assert.False(CloudProviders.ShapeBody(v, ApiProtocol.Gemini, gemini));
        Assert.False(CloudProviders.ShapeBody(Bedrock(), ApiProtocol.Anthropic, JsonNode.Parse("""{"model":"m"}""")!.AsObject()));
    }

    [Fact]
    public void A_Provider_May_Name_Its_Own_Signing_Scope()
    {
        var p = Bedrock();
        Assert.Equal((null, null), CloudProviders.AwsScopeOverride(p));
        p.Settings["aws"] = new JsonObject { ["region"] = "eu-west-1", ["service"] = "bedrock" };
        Assert.Equal(("eu-west-1", "bedrock"), CloudProviders.AwsScopeOverride(p));
    }
}
