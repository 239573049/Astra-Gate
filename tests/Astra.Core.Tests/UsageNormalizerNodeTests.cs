using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;

namespace Astra.Core.Tests;

public class UsageNormalizerNodeTests
{
    [Fact]
    public void In_Memory_Integer_Nodes_Normalize_Like_Parsed_Json()
    {
        var built = new JsonObject { ["promptTokenCount"] = 120, ["cachedContentTokenCount"] = 100L, ["candidatesTokenCount"] = 7.0 };
        var parsed = JsonNode.Parse("""{"promptTokenCount":120,"cachedContentTokenCount":100,"candidatesTokenCount":7}""");
        var a = UsageNormalizer.Normalize(ApiProtocol.Gemini, built)!;
        var b = UsageNormalizer.Normalize(ApiProtocol.Gemini, parsed)!;
        Assert.Equal(b.Tokens, a.Tokens);
        Assert.Equal(100, a.Get(TokenTypes.CacheRead));
        Assert.Equal(20, a.Get(TokenTypes.Input));
    }
}
