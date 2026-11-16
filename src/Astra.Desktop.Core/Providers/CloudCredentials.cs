using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Astra.Desktop.Core.Providers;

/// <summary>What a pasted service account key file says about itself (never the private key).</summary>
public sealed record ServiceAccountFacts(string Email, string ProjectId);

/// <summary>
/// The cloud credential schemes (web <c>components/CloudCredentials.tsx</c>): an Amazon Bedrock IAM access key and a Google service
/// account key file are JSON documents kept in the provider's one encrypted secret (<c>CloudSecrets</c> on the server); these rules
/// assemble and read them for the wizard and the API-key tab. No UI types.
/// </summary>
public static partial class CloudCredentials
{
    /// <summary>The auth scheme of an IAM access key (SigV4-signed requests).</summary>
    public const string AwsSigV4 = "aws-sigv4";

    /// <summary>The auth scheme of a Google service account key (traded for access tokens).</summary>
    public const string GcpServiceAccount = "gcp-service-account";

    /// <summary>Schemes whose secret is not one string typed by the user.</summary>
    public static bool IsCloud(string? scheme) => scheme is AwsSigV4 or GcpServiceAccount;

    /// <summary>Vertex AI locations offered (global, the multi-regions, common regions); a stored other one is kept.</summary>
    public static readonly IReadOnlyList<string> VertexLocations =
    [
        "global", "us", "eu", "us-central1", "us-east1", "us-east4", "us-east5", "us-south1", "us-west1", "us-west4", "northamerica-northeast1",
        "europe-west1", "europe-west4", "europe-west9", "asia-northeast1", "asia-southeast1", "australia-southeast1", "southamerica-east1",
    ];

    /// <summary>The location choices with <paramref name="current"/> included when it is not a known one.</summary>
    public static IReadOnlyList<string> LocationChoices(string current) =>
        current.Length == 0 || VertexLocations.Contains(current) ? VertexLocations : [.. VertexLocations, current];

    /// <summary>The IAM access key as the server stores it, or "" while the two required parts are not both there.</summary>
    public static string AwsKeyDocument(string accessKeyId, string secretAccessKey, string sessionToken)
    {
        var id = accessKeyId.Trim();
        var secret = secretAccessKey.Trim();
        if (id.Length == 0 || secret.Length == 0) return "";
        var o = new JsonObject { ["accessKeyId"] = id, ["secretAccessKey"] = secret };
        if (sessionToken.Trim() is { Length: > 0 } token) o["sessionToken"] = token;
        return o.ToJsonString();
    }

    /// <summary>The parts of a document <see cref="AwsKeyDocument"/> made (a rebuilt form shows what was typed); empty parts for anything else.</summary>
    public static (string AccessKeyId, string SecretAccessKey, string SessionToken) ReadAwsParts(string? document)
    {
        try
        {
            var o = JsonNode.Parse(document ?? "") as JsonObject;
            return (Text(o, "accessKeyId") ?? "", Text(o, "secretAccessKey") ?? "", Text(o, "sessionToken") ?? "");
        }
        catch (JsonException)
        {
            return ("", "", "");
        }
    }

    /// <summary>The region an AWS endpoint address names (<c>bedrock-runtime.us-east-1.amazonaws.com</c>, <c>….us-east-1.api.aws</c>); null for any other.</summary>
    public static string? AwsRegionOf(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        if (!host.EndsWith(".amazonaws.com", StringComparison.Ordinal) && !host.EndsWith(".api.aws", StringComparison.Ordinal)) return null;
        var labels = host.Split('.');
        return labels.Length >= 4 && RegionShape().IsMatch(labels[^3]) ? labels[^3] : null;
    }

    [GeneratedRegex("^[a-z]{2,}(-[a-z0-9]+)+$")]
    private static partial Regex RegionShape();

    /// <summary>A pasted key file's account and project, or null when the text is not a service account key.</summary>
    public static ServiceAccountFacts? ReadServiceAccount(string? text)
    {
        try
        {
            if (JsonNode.Parse(text ?? "") is not JsonObject o) return null;
            if (Text(o, "client_email") is not { } email || Text(o, "private_key") is null) return null;
            if (o["type"] is not null && Text(o, "type") != "service_account") return null;
            return new ServiceAccountFacts(email, Text(o, "project_id") ?? "");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The Vertex project and location stored in a provider's settings (location "global" when unset).</summary>
    public static (string Project, string Location) VertexTarget(JsonObject? settings)
    {
        var vertex = settings?["vertex"] as JsonObject;
        var location = Text(vertex, "location") ?? "";
        return (Text(vertex, "project") ?? "", location.Length == 0 ? "global" : location);
    }

    private static string? Text(JsonObject? o, string name) =>
        o?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
