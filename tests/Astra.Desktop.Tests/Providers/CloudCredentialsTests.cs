using System.Text.Json.Nodes;
using Astra.Desktop.Core.Providers;

namespace Astra.Desktop.Tests.Providers;

/// <summary>The cloud credential schemes' rules (web <c>cloud-credentials.test.tsx</c>): Bedrock's IAM key and Vertex's service account key.</summary>
public sealed class CloudCredentialsTests
{
    [Fact]
    public void The_iam_document_needs_both_required_parts_and_carries_the_optional_token()
    {
        Assert.Equal("", CloudCredentials.AwsKeyDocument("", "secret", ""));
        Assert.Equal("", CloudCredentials.AwsKeyDocument("AKIA1", " ", ""));
        var plain = JsonNode.Parse(CloudCredentials.AwsKeyDocument(" AKIA1 ", " secret ", ""))!.AsObject();
        Assert.Equal("AKIA1", (string?)plain["accessKeyId"]);
        Assert.Equal("secret", (string?)plain["secretAccessKey"]);
        Assert.False(plain.ContainsKey("sessionToken"));
        Assert.Equal("tok", (string?)JsonNode.Parse(CloudCredentials.AwsKeyDocument("AKIA1", "secret", "tok"))!["sessionToken"]);
    }

    [Fact]
    public void A_rebuilt_form_gets_back_what_was_typed()
    {
        var doc = CloudCredentials.AwsKeyDocument("AKIA1", "secret", "tok");
        Assert.Equal(("AKIA1", "secret", "tok"), CloudCredentials.ReadAwsParts(doc));
        Assert.Equal(("", "", ""), CloudCredentials.ReadAwsParts(""));
        Assert.Equal(("", "", ""), CloudCredentials.ReadAwsParts("not json"));
    }

    [Theory]
    [InlineData("https://bedrock-runtime.eu-west-1.amazonaws.com/openai/v1", "eu-west-1")]
    [InlineData("https://bedrock-mantle.ap-southeast-4.api.aws/v1", "ap-southeast-4")]
    [InlineData("https://api.openai.com/v1", null)]
    [InlineData("not a url", null)]
    [InlineData(null, null)]
    public void The_region_comes_from_the_address(string? url, string? region) => Assert.Equal(region, CloudCredentials.AwsRegionOf(url));

    [Fact]
    public void A_service_account_key_is_recognised_and_nothing_else()
    {
        var key = new JsonObject { ["type"] = "service_account", ["client_email"] = "a@p.iam.gserviceaccount.com", ["private_key"] = "x", ["project_id"] = "p" }.ToJsonString();
        Assert.Equal(new ServiceAccountFacts("a@p.iam.gserviceaccount.com", "p"), CloudCredentials.ReadServiceAccount(key));
        Assert.Null(CloudCredentials.ReadServiceAccount("""{"type":"authorized_user","client_email":"a","private_key":"x"}"""));
        Assert.Null(CloudCredentials.ReadServiceAccount("""{"client_email":"a"}"""));
        Assert.Null(CloudCredentials.ReadServiceAccount("nope"));
        Assert.Null(CloudCredentials.ReadServiceAccount(null));
    }

    [Fact]
    public void Vertex_target_defaults_to_global_and_keeps_an_unlisted_location()
    {
        Assert.Equal(("", "global"), CloudCredentials.VertexTarget(null));
        Assert.Equal(("p", "us-central1"), CloudCredentials.VertexTarget(new JsonObject { ["vertex"] = new JsonObject { ["project"] = "p", ["location"] = "us-central1" } }));
        Assert.DoesNotContain("me-west1", CloudCredentials.VertexLocations);
        Assert.Contains("me-west1", CloudCredentials.LocationChoices("me-west1"));
        Assert.Equal(CloudCredentials.VertexLocations, CloudCredentials.LocationChoices("us"));
    }

    private static ProviderTemplateInfo Template(string id, string scheme) =>
        new(id, id, "official", null, null, [new TemplateEndpoint("anthropic", "https://x.test")], ["anthropic"], scheme, true, 0, id, null, [], [], []);

    [Fact]
    public void The_wizard_sends_vertex_project_and_location_beside_the_key()
    {
        var w = new AddProviderWizard();
        w.Pick(Template("vertex-ai", CloudCredentials.GcpServiceAccount));
        Assert.False(w.CanSubmit);
        w.ApiKey = "{\"client_email\":\"a\"}";
        w.VertexProject = " my-project ";
        w.VertexLocation = "us-central1";
        Assert.True(w.CanSubmit);
        var body = w.BuildRequest();
        Assert.Equal("gcp-service-account", (string?)body["authScheme"]);
        Assert.Equal("my-project", (string?)body["settings"]!["vertex"]!["project"]);
        Assert.Equal("us-central1", (string?)body["settings"]!["vertex"]!["location"]);
    }

    [Fact]
    public void The_wizard_sends_no_settings_for_other_schemes()
    {
        var w = new AddProviderWizard();
        w.Pick(Template("bedrock-iam", CloudCredentials.AwsSigV4));
        w.ApiKey = CloudCredentials.AwsKeyDocument("AKIA1", "secret", "");
        Assert.True(w.CanSubmit);
        Assert.False(w.BuildRequest().ContainsKey("settings"));
    }

    [Fact]
    public void A_typed_key_stays_across_Back_except_where_a_cloud_document_is_involved()
    {
        var w = new AddProviderWizard();
        w.Pick(Template("groq", "bearer"));
        w.ApiKey = "gsk-1";
        w.Back();
        w.Pick(Template("mistral", "bearer"));
        Assert.Equal("gsk-1", w.ApiKey);
        w.Back();
        w.Pick(Template("bedrock-iam", CloudCredentials.AwsSigV4));
        Assert.Equal("", w.ApiKey);
        w.ApiKey = "{\"accessKeyId\":\"a\"}";
        w.Back();
        w.Pick(Template("groq", "bearer"));
        Assert.Equal("", w.ApiKey);
    }

    [Fact]
    public void Auth_options_list_the_new_schemes()
    {
        var values = ProviderProtocols.AuthOptions.Select(o => o.Value).ToList();
        Assert.Contains("api-key", values);
        Assert.Contains("aws-sigv4", values);
        Assert.Contains("gcp-service-account", values);
    }
}
