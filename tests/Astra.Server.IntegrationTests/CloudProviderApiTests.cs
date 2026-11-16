using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Astra.Server.IntegrationTests;

/// <summary>The admin API for the cloud credential schemes: IAM access keys, Google service account keys, Azure's api-key.</summary>
public class CloudProviderApiTests
{
    private static string ServiceAccountKey(string? project = "from-key-file")
    {
        using var rsa = RSA.Create(2048);
        var o = new JsonObject
        {
            ["type"] = "service_account", ["client_email"] = "astra@demo-project.iam.gserviceaccount.com",
            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
        };
        if (project is not null) o["project_id"] = project;
        // as downloaded: pretty-printed, so the document carries newlines the plain key rule would refuse
        return o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    [Fact]
    public async Task The_Catalog_Lists_The_New_Templates_With_Their_Schemes()
    {
        await using var host = await TestHost.StartAsync();
        var templates = (await host.GetJsonAsync("/api/provider-templates")).AsArray().ToDictionary(t => t!["id"]!.GetValue<string>(), t => t!);
        Assert.Equal(63, templates.Count);
        Assert.Equal("aws-sigv4", templates["bedrock-iam"]["authScheme"]!.GetValue<string>());
        Assert.Equal("gcp-service-account", templates["vertex-ai"]["authScheme"]!.GetValue<string>());
        Assert.Equal("api-key", templates["azure-openai"]["authScheme"]!.GetValue<string>());
        Assert.Contains(templates["bedrock-iam"]["variants"]!.AsArray(), v => v!["id"]!.GetValue<string>() == "eu-west-1");
    }

    [Fact]
    public async Task An_Iam_Key_Pasted_As_Json_Is_Stored_Compact_Masked_And_Never_Returned()
    {
        await using var host = await TestHost.StartAsync();
        const string pasted = """
            {
              "accessKeyId": "AKIAIOSFODNN7EXAMPLE",
              "secretAccessKey": "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY"
            }
            """;
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "bedrock-iam", apiKey = pasted });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("aws-sigv4", body!["authScheme"]!.GetValue<string>());
        Assert.True(body["hasApiKey"]!.GetValue<bool>());
        Assert.Equal("AKIA…MPLE", body["apiKeyMasked"]!.GetValue<string>());
        Assert.DoesNotContain("wJalrXUtnFEMI", body.ToJsonString());
        var stored = await host.Db.Providers.GetAsync(body["id"]!.GetValue<string>());
        Assert.DoesNotContain("wJalrXUtnFEMI", stored!.ApiKeyEnc);
        var models = await host.Db.Providers.ListModelsAsync(stored.Id);
        Assert.Equal(16, models.Count);
        // each model is told the protocol its maker's API speaks on Bedrock
        Assert.Equal([Astra.Core.ApiProtocol.Anthropic], models.Single(m => m.ModelId == "global.anthropic.claude-sonnet-5-5").UpstreamProtocols);
        Assert.Equal([Astra.Core.ApiProtocol.OpenAIChat], models.Single(m => m.ModelId == "openai.gpt-oss-120b-1:0").UpstreamProtocols);
    }

    [Theory]
    [InlineData("""{"accessKeyId":"AKIAIOSFODNN7EXAMPLE"}""", "secretAccessKey")]
    [InlineData("AKIAIOSFODNN7EXAMPLE", "JSON")]
    public async Task A_Malformed_Iam_Key_Is_Refused(string key, string reason)
    {
        await using var host = await TestHost.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "bedrock-iam", apiKey = key });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(reason, body!["error"]!.GetValue<string>());
        Assert.Empty((await host.GetJsonAsync("/api/providers")).AsArray());
    }

    [Fact]
    public async Task A_Service_Account_Key_Fills_The_Project_And_Shows_Only_The_Account()
    {
        await using var host = await TestHost.StartAsync();
        var key = ServiceAccountKey();
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "vertex-ai", apiKey = key });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("from-key-file", body!["settings"]!["vertex"]!["project"]!.GetValue<string>());
        Assert.Equal("global", body["settings"]!["vertex"]!["location"]!.GetValue<string>());
        Assert.Equal("astra@demo-project.iam.gserviceaccount.com", body["apiKeyMasked"]!.GetValue<string>());
        Assert.DoesNotContain("PRIVATE KEY", body.ToJsonString());
        var id = body["id"]!.GetValue<string>();

        // a project the user chose stays when the key is replaced; the location is the user's to change
        (status, body) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}",
            new { settings = new { vertex = new { project = "my-own", location = "us-central1" } }, apiKey = ServiceAccountKey("another") });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("my-own", body!["settings"]!["vertex"]!["project"]!.GetValue<string>());
        Assert.Equal("us-central1", body["settings"]!["vertex"]!["location"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"vertex":{"project":"Bad Project"}}""", "project")]
    [InlineData("""{"vertex":{"location":"us central"}}""", "location")]
    [InlineData("""{"aws":{"region":"nowhere"}}""", "region")]
    public async Task Bad_Cloud_Settings_Are_Refused(string settingsJson, string field)
    {
        await using var host = await TestHost.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "openai", apiKey = "sk-a-test-key-0001" });
        Assert.Equal(HttpStatusCode.OK, status);
        (status, body) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{body!["id"]!.GetValue<string>()}", new { settings = JsonNode.Parse(settingsJson) });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(field, body!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_Service_Account_Token_Only_Goes_To_Google_And_An_Iam_Signature_Only_To_Aws()
    {
        await using var host = await TestHost.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new
        {
            templateId = "vertex-ai", apiKey = ServiceAccountKey(),
            endpoints = new[] { new { protocol = "gemini", baseUrl = "https://evil.example" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("googleapis.com", body!["error"]!.GetValue<string>());

        (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new
        {
            templateId = "bedrock-iam", apiKey = """{"accessKeyId":"AKIAIOSFODNN7EXAMPLE","secretAccessKey":"s3cret"}""",
            endpoints = new[] { new { protocol = "anthropic", baseUrl = "https://evil.example/anthropic" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("AWS", body!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_Wizard_Chooses_Vertex_Project_And_Location_At_Creation()
    {
        await using var host = await TestHost.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new
        {
            templateId = "vertex-ai", apiKey = ServiceAccountKey(project: null),
            settings = new { vertex = new { project = "chosen", location = "europe-west4" } },
        });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("chosen", body!["settings"]!["vertex"]!["project"]!.GetValue<string>());
        Assert.Equal("europe-west4", body["settings"]!["vertex"]!["location"]!.GetValue<string>());

        // only the cloud groups may be set that way
        (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "openai", apiKey = "sk-a-test-key-0001", settings = new { first_byte_timeout_sec = 5 } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("first_byte_timeout_sec", body!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Switching_Between_The_Bedrock_Templates_Drops_The_Key_That_Cannot_Carry_Over()
    {
        await using var host = await TestHost.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "bedrock", apiKey = "ABSKQmVkcm9ja0FQSUtleQ==" });
        Assert.Equal(HttpStatusCode.OK, status);
        var id = body!["id"]!.GetValue<string>();
        (status, body) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/template-switch", new { templateId = "bedrock-iam" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("aws-sigv4", body!["authScheme"]!.GetValue<string>());
        Assert.False(body["hasApiKey"]!.GetValue<bool>());
        Assert.Equal(16, body["modelCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_Bedrock_Api_Key_Provider_Is_An_Ordinary_Bearer_Provider()
    {
        await using var host = await TestHost.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "bedrock", apiKey = "ABSKQmVkcm9ja0FQSUtleQ==" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("bearer", body!["authScheme"]!.GetValue<string>());
        Assert.Equal("bedrock", body["templateId"]!.GetValue<string>());
    }
}
