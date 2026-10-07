using System.Text.Json.Nodes;
using Astra.Gateway.Protocol.Gemini;

namespace Astra.Gateway.Tests.Codecs;

public class GeminiCodecErrorTests
{
    [Theory]
    [InlineData(400, "INVALID_ARGUMENT")]
    [InlineData(401, "UNAUTHENTICATED")]
    [InlineData(403, "PERMISSION_DENIED")]
    [InlineData(404, "NOT_FOUND")]
    [InlineData(429, "RESOURCE_EXHAUSTED")]
    [InlineData(503, "UNAVAILABLE")]
    [InlineData(504, "DEADLINE_EXCEEDED")]
    [InlineData(500, "INTERNAL")]
    [InlineData(502, "INTERNAL")]
    [InlineData(418, "UNKNOWN")]
    public void EncodeError_Uses_Google_Rpc_Status_Names(int status, string expectedStatusName)
    {
        var body = new GeminiCodec().EncodeError(status, "some_type", "boom");

        var error = (JsonObject)body["error"]!;
        Assert.Equal(status, error["code"]!.GetValue<int>());
        Assert.Equal("boom", error["message"]!.GetValue<string>());
        Assert.Equal(expectedStatusName, error["status"]!.GetValue<string>());
    }

    [Fact]
    public void DecodeError_Parses_The_Gemini_Error_Shape()
    {
        var (type, message) = new GeminiCodec().DecodeError(429,
            """{"error":{"code":429,"message":"Quota exceeded for requests","status":"RESOURCE_EXHAUSTED"}}""");

        Assert.Equal("RESOURCE_EXHAUSTED", type);
        Assert.Equal("Quota exceeded for requests", message);
    }

    [Fact]
    public void DecodeError_Falls_Back_To_The_Raw_Body()
    {
        var (type, message) = new GeminiCodec().DecodeError(502, "<html>Bad Gateway</html>");

        Assert.Equal("upstream_error", type);
        Assert.Equal("<html>Bad Gateway</html>", message);
    }
}
