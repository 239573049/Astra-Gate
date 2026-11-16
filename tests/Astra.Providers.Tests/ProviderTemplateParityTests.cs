using Astra.Core;
using Astra.Core.Models;
using Astra.Providers.Templates;

namespace Astra.Providers.Tests;

public class ProviderTemplateParityTests
{
    private static readonly ProviderTemplateCatalog Catalog = new();

    // Presets taken from magpie's list (MIT): API-key vendors that fit the template schema. Voyage AI (embeddings and rerank
    // only) is the one deliberate omission: the gateway serves neither API.
    private static readonly string[] MagpieParity =
    [
        "mistral", "groq", "together", "fireworks", "fireworks-nexus", "nvidia-nim", "modelscope", "stepfun", "longcat", "baidu-qianfan",
        "tencent-cloud", "huaweicloud", "xiaomi-mimo", "azure-openai", "vercel-ai-gateway", "gmicloud", "nous", "ollama-cloud",
        "kimi-code", "alibaba-token-plan", "opencode-go", "opencode-zen", "clinepass", "commandcode", "aihubmix", "pipellm", "302ai",
        "cherryin", "aigocode", "yylx", "kilo", "bedrock", "bedrock-iam", "bedrock-mantle", "vertex-ai",
    ];

    [Fact]
    public void Every_Parity_Template_Is_In_The_Catalog() =>
        Assert.All(MagpieParity, id => Assert.NotNull(Catalog.Get(id)));

    [Fact]
    public void Every_Template_Has_Valid_Endpoints_Auth_And_Variants()
    {
        foreach (var t in Catalog.All)
        {
            Assert.Contains(t.AuthScheme, AuthSchemes.All);
            Assert.Equal(t.Endpoints.Count, t.Endpoints.Select(e => e.Protocol).Distinct().Count());
            foreach (var e in t.Endpoints)
                Assert.True(Uri.TryCreate(e.BaseUrl, UriKind.Absolute, out var u) && u.Scheme is "http" or "https", $"{t.Id}: {e.BaseUrl}");
            Assert.All(t.PreferredUpstreamProtocols, p => Assert.Contains(t.Endpoints, e => e.Protocol == p));
            Assert.Equal(t.Variants?.Count ?? 0, t.Variants?.Select(v => v.Id).Distinct().Count() ?? 0);
            foreach (var v in t.Variants ?? [])
            {
                var applied = t.WithVariant(v.Id);
                Assert.NotEmpty(applied.Endpoints);
                Assert.Equal(applied.Endpoints.Count, applied.Endpoints.Select(e => e.Protocol).Distinct().Count());
                Assert.All(applied.Endpoints, e => Assert.True(Uri.TryCreate(e.BaseUrl, UriKind.Absolute, out _), $"{t.Id}/{v.Id}: {e.BaseUrl}"));
            }
            if (t.ModelListEndpoint is { } list) Assert.Contains(t.Endpoints, e => e.Protocol == list.Protocol);
        }
    }

    [Fact]
    public void Templates_Without_A_Model_List_Ship_Their_Default_Models()
    {
        foreach (var id in new[] { "bedrock", "bedrock-iam", "vertex-ai", "fireworks-nexus", "clinepass", "alibaba-token-plan" })
        {
            var t = Catalog.Get(id)!;
            Assert.Null(t.ModelListEndpoint);
            Assert.NotEmpty(t.Models);
        }
    }

    [Fact]
    public void Bedrock_Comes_Three_Ways_With_The_Regions_As_Variants()
    {
        var apiKey = Catalog.Get("bedrock")!;
        var iam = Catalog.Get("bedrock-iam")!;
        var mantle = Catalog.Get("bedrock-mantle")!;
        Assert.Equal(AuthSchemes.Bearer, apiKey.AuthScheme);
        Assert.Equal(AuthSchemes.AwsSigV4, iam.AuthScheme);
        Assert.Equal(AuthSchemes.Bearer, mantle.AuthScheme);
        // the API-key and IAM templates serve the same service on keys the other refuses: they name each other
        Assert.Equal(["bedrock-iam"], apiKey.KeyAlternatives);
        Assert.Equal(["bedrock"], iam.KeyAlternatives);
        Assert.Equal("https://bedrock-runtime.us-east-1.amazonaws.com/anthropic", iam.Endpoints.Single(e => e.Protocol == ApiProtocol.Anthropic).BaseUrl);
        Assert.Equal("https://bedrock-runtime.eu-west-1.amazonaws.com/openai/v1",
            iam.WithVariant("eu-west-1").Endpoints.Single(e => e.Protocol == ApiProtocol.OpenAIChat).BaseUrl);
        Assert.Equal("https://bedrock-mantle.sa-east-1.api.aws/v1",
            mantle.WithVariant("sa-east-1").Endpoints.Single(e => e.Protocol == ApiProtocol.OpenAIChat).BaseUrl);
    }

    [Fact]
    public void Vertex_Declares_Gemini_Claude_And_Partner_Endpoints_At_Googles_Host()
    {
        var t = Catalog.Get("vertex-ai")!;
        Assert.Equal(AuthSchemes.GcpServiceAccount, t.AuthScheme);
        Assert.Equal([ApiProtocol.Gemini, ApiProtocol.Anthropic, ApiProtocol.OpenAIChat], t.PreferredUpstreamProtocols);
        Assert.All(t.Endpoints, e => Assert.EndsWith(".googleapis.com", new Uri(e.BaseUrl).Host));
        Assert.Equal("global", t.Settings["vertex"]!["location"]!.GetValue<string>());
    }

    [Fact]
    public void Azure_Takes_Its_Key_In_The_Api_Key_Header()
    {
        var t = Catalog.Get("azure-openai")!;
        Assert.Equal(AuthSchemes.ApiKeyHeader, t.AuthScheme);
        Assert.Contains(t.Endpoints, e => e.Protocol == ApiProtocol.OpenAIResponses);
    }

    [Fact]
    public void Plans_And_Regions_Are_Variants_Of_Their_Vendor()
    {
        Assert.Contains(Catalog.Get("zhipuai")!.Variants!, v => v.Id == "coding");
        Assert.Contains(Catalog.Get("stepfun")!.Variants!, v => v.Id == "plan");
        Assert.Equal("https://api.stepfun.ai/v1", Catalog.Get("stepfun")!.WithVariant("intl").Endpoints.Single(e => e.Protocol == ApiProtocol.OpenAIChat).BaseUrl);
        Assert.Equal("https://tokenhub-intl.tencentmaas.com/plan/anthropic",
            Catalog.Get("tencent-cloud")!.WithVariant("plan-enterprise-intl").Endpoints.Single(e => e.Protocol == ApiProtocol.Anthropic).BaseUrl);
    }

    [Fact]
    public void Templates_That_Need_No_Key_Say_So()
    {
        Assert.False(Catalog.Get("opencode-zen")!.RequiresApiKey);
        Assert.False(Catalog.Get("kilo")!.RequiresApiKey);
        Assert.True(Catalog.Get("groq")!.RequiresApiKey);
    }
}
