using System.Text;
using Astra.Providers.Cloud;

namespace Astra.Providers.Tests;

/// <summary>SigV4 against AWS's published test material (the SigV4 test suite and the IAM documentation's key derivation example).</summary>
public class AwsSigV4Tests
{
    // The example credentials of AWS's signature test suite (not real).
    private static readonly AwsCredentials Example = new("AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", null);
    private static readonly DateTimeOffset SuiteTime = new(2015, 8, 30, 12, 36, 0, TimeSpan.Zero);

    [Fact]
    public void Signing_Key_Matches_The_IAM_Documentation_Example()
    {
        // docs.aws.amazon.com/IAM/latest/UserGuide/signing-elements.html (derive a signing key)
        var key = AwsSigV4.SigningKey("wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "20120215", "us-east-1", "iam");
        Assert.Equal("f4780e2d9f65fa895f9c67b32ce1baf0b0d8a43505a000a1a9e090d414db404d", Convert.ToHexStringLower(key));
    }

    [Fact]
    public void Get_Vanilla_Matches_The_Test_Suite()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.amazonaws.com/");
        AwsSigV4.Sign(request, [], Example, "us-east-1", "service", SuiteTime);
        Assert.Equal("20150830T123600Z", request.Headers.GetValues("x-amz-date").Single());
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, SignedHeaders=host;x-amz-date, " +
                     "Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31",
            request.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public void Query_Parameters_Are_Sorted_As_In_The_Test_Suite()
    {
        // get-vanilla-query-order-key-case
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.amazonaws.com/?Param2=value2&Param1=value1");
        AwsSigV4.Sign(request, [], Example, "us-east-1", "service", SuiteTime);
        Assert.EndsWith("Signature=b97d918cfa904a5beff61c982a1b6f458b799221646efd99d3219ec94cdf2500", request.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public void A_Session_Token_Is_Sent_And_Signed()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://bedrock-runtime.us-east-1.amazonaws.com/openai/v1/chat/completions");
        AwsSigV4.Sign(request, Encoding.UTF8.GetBytes("{}"), Example with { SessionToken = "tok/en+1==" }, "us-east-1", "bedrock", SuiteTime);
        Assert.Equal("tok/en+1==", request.Headers.GetValues("x-amz-security-token").Single());
        var authorization = request.Headers.GetValues("Authorization").Single();
        Assert.Contains("SignedHeaders=host;x-amz-date;x-amz-security-token,", authorization);
        Assert.Contains("Credential=AKIDEXAMPLE/20150830/us-east-1/bedrock/aws4_request", authorization);
    }

    [Fact]
    public void The_Signature_Covers_The_Body_And_The_Path()
    {
        string Sign(string path, string body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://bedrock-runtime.us-east-1.amazonaws.com" + path);
            AwsSigV4.Sign(request, Encoding.UTF8.GetBytes(body), Example, "us-east-1", "bedrock", SuiteTime);
            return request.Headers.GetValues("Authorization").Single();
        }
        var baseline = Sign("/anthropic/v1/messages", "{\"a\":1}");
        Assert.Equal(baseline, Sign("/anthropic/v1/messages", "{\"a\":1}"));
        Assert.NotEqual(baseline, Sign("/anthropic/v1/messages", "{\"a\":2}"));
        Assert.NotEqual(baseline, Sign("/openai/v1/chat/completions", "{\"a\":1}"));
    }

    [Fact]
    public void A_Colon_In_The_Path_Is_Encoded_Twice_As_Every_Service_But_S3_Wants()
    {
        // /model/anthropic.claude-v2:1/invoke is sent as …%3A1… and canonicalized as …%253A1…; a signature over either spelling differs.
        using var a = new HttpRequestMessage(HttpMethod.Post, "https://bedrock-runtime.us-east-1.amazonaws.com/model/anthropic.claude-v2%3A1/invoke");
        using var b = new HttpRequestMessage(HttpMethod.Post, "https://bedrock-runtime.us-east-1.amazonaws.com/model/anthropic.claude-v2/invoke");
        AwsSigV4.Sign(a, [], Example, "us-east-1", "bedrock", SuiteTime);
        AwsSigV4.Sign(b, [], Example, "us-east-1", "bedrock", SuiteTime);
        Assert.NotEqual(a.Headers.GetValues("Authorization").Single(), b.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public void Signing_Twice_Replaces_The_Previous_Headers()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.amazonaws.com/");
        AwsSigV4.Sign(request, [], Example, "us-east-1", "service", SuiteTime);
        AwsSigV4.Sign(request, [], Example, "us-east-1", "service", SuiteTime);
        Assert.Single(request.Headers.GetValues("Authorization"));
        Assert.Single(request.Headers.GetValues("x-amz-date"));
    }

    [Theory]
    [InlineData("bedrock-runtime.us-east-1.amazonaws.com", "us-east-1", "bedrock")]
    [InlineData("bedrock-runtime.eu-west-3.amazonaws.com", "eu-west-3", "bedrock")]
    [InlineData("bedrock-mantle.ap-southeast-4.api.aws", "ap-southeast-4", "bedrock-mantle")]
    [InlineData("bedrock-agent-runtime.us-west-2.amazonaws.com", "us-west-2", "bedrock")]
    public void Region_And_Service_Come_From_The_Host(string host, string region, string service)
    {
        Assert.Equal(region, AwsSigV4.RegionOf(host));
        Assert.Equal(service, AwsSigV4.ServiceOf(host));
    }

    [Theory]
    [InlineData("api.openai.com")]
    [InlineData("localhost")]
    [InlineData("amazonaws.com")]
    public void A_Host_That_Is_Not_AWS_Has_No_Region(string host) => Assert.Null(AwsSigV4.RegionOf(host));

    [Fact]
    public void SignIfRequested_Does_Nothing_Without_Credentials_And_Signs_With_Them()
    {
        using var plain = new HttpRequestMessage(HttpMethod.Post, "https://bedrock-runtime.us-east-1.amazonaws.com/x");
        AwsSigV4.SignIfRequested(plain, []);
        Assert.False(plain.Headers.Contains("Authorization"));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://bedrock-runtime.us-east-1.amazonaws.com/x");
        request.Options.Set(AwsSigning.Option, new AwsSigning(Example, "us-east-1", "bedrock"));
        AwsSigV4.SignIfRequested(request, []);
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/", request.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public void Credentials_Parse_From_Our_Shape_The_CLI_Shape_And_Snake_Case()
    {
        Assert.Equal(new AwsCredentials("AKIA1234567890", "secret", "tok"),
            AwsCredentials.TryParse("""{"accessKeyId":"AKIA1234567890","secretAccessKey":"secret","sessionToken":"tok"}""", out _));
        Assert.Equal(new AwsCredentials("AKIA1234567890", "secret", "tok"),
            AwsCredentials.TryParse("""{"Credentials":{"AccessKeyId":"AKIA1234567890","SecretAccessKey":"secret","SessionToken":"tok","Expiration":"x"}}""", out _));
        Assert.Equal(new AwsCredentials("AKIA1234567890", "secret", null),
            AwsCredentials.TryParse("""{"access_key_id":"AKIA1234567890","secret_access_key":"secret"}""", out _));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"accessKeyId":"AKIA"}""")]
    [InlineData("""{"accessKeyId":"AKIA","secretAccessKey":"has space"}""")]
    public void Malformed_Credentials_Are_Refused_Without_Echoing_The_Secret(string json)
    {
        Assert.Null(AwsCredentials.TryParse(json, out var error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.DoesNotContain("has space", error);
    }

    [Fact]
    public void The_Mask_Shows_Only_The_Access_Key_Id_Ends() =>
        Assert.Equal("AKIA…WXYZ", new AwsCredentials("AKIA1234567890WXYZ", "never-shown", null).Mask());
}
