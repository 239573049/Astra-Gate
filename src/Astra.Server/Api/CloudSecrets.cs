using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Core.Models;
using Astra.Providers.Cloud;

namespace Astra.Server.Api;

/// <summary>
/// The two cloud auth schemes keep a JSON document in the provider's one encrypted secret (IAM access key, Google service
/// account key). This checks and normalizes it on the way in (compact JSON, so the control-character rule on keys still
/// holds), says how it is masked, and validates the non-secret settings that go with it (<c>settings.vertex</c>,
/// <c>settings.aws</c>). Nothing here ever returns or logs the secret.
/// </summary>
internal static partial class CloudSecrets
{
    /// <summary>The secret as it is stored. Other schemes keep the key as typed.</summary>
    /// <exception cref="AdminApiException">The document is not a usable key for the scheme.</exception>
    public static string Normalize(string scheme, string key, Provider provider)
    {
        switch (scheme)
        {
            case AuthSchemes.AwsSigV4:
            {
                var credentials = AwsCredentials.TryParse(key, out var error) ?? throw new AdminApiException(400, error!);
                var node = new JsonObject { ["accessKeyId"] = credentials.AccessKeyId, ["secretAccessKey"] = credentials.SecretAccessKey };
                if (credentials.SessionToken is not null) node["sessionToken"] = credentials.SessionToken;
                return node.ToJsonString();
            }
            case AuthSchemes.GcpServiceAccount:
            {
                var account = GoogleServiceAccount.TryParse(key, out var error) ?? throw new AdminApiException(400, error!);
                // the key file names its project: the provider's own setting, unless the user chose another one
                var vertex = provider.Settings["vertex"] as JsonObject ?? new JsonObject();
                if (account.ProjectId.Length > 0 && (vertex["project"] is not JsonValue v || !v.TryGetValue<string>(out var set) || set.Trim().Length == 0))
                    vertex["project"] = account.ProjectId;
                provider.Settings["vertex"] = vertex;
                return JsonNode.Parse(key)!.ToJsonString();
            }
            default:
                return key;
        }
    }

    /// <summary>Whether the scheme keeps a JSON document in the secret rather than one typed key.</summary>
    public static bool IsCloud(string scheme) => scheme is AuthSchemes.AwsSigV4 or AuthSchemes.GcpServiceAccount;

    /// <summary>What the admin UI shows instead of the secret; null for a scheme whose key is masked the usual way.</summary>
    public static string? Mask(string scheme, string secret) => scheme switch
    {
        AuthSchemes.AwsSigV4 => AwsCredentials.TryParse(secret, out _)?.Mask() ?? "••••",
        AuthSchemes.GcpServiceAccount => GoogleServiceAccount.TryParse(secret, out _)?.Mask() ?? "••••",
        _ => null,
    };

    /// <summary>The wizard's <c>settings</c> on create: only the cloud groups (<c>vertex</c>, <c>aws</c>) are taken, each replacing the template's.</summary>
    public static void MergeCreateSettings(Provider provider, JsonObject given)
    {
        foreach (var (key, value) in given)
        {
            if (key is not ("vertex" or "aws")) throw new AdminApiException(400, $"settings.{key} cannot be set when a provider is created");
            if (value is not JsonObject) throw new AdminApiException(400, $"settings.{key} must be an object");
            provider.Settings[key] = value.DeepClone();
        }
    }

    /// <summary>The non-secret half of the cloud schemes: Vertex's project and location, an AWS region override.</summary>
    public static void ValidateSettings(Provider p)
    {
        if (p.Settings["vertex"] is { } vertexNode)
        {
            if (vertexNode is not JsonObject vertex) throw new AdminApiException(400, "settings.vertex must be an object");
            var project = Text(vertex, "project");
            var location = Text(vertex, "location");
            if (!string.IsNullOrEmpty(project) && !ProjectId().IsMatch(project))
                throw new AdminApiException(400, "settings.vertex.project is not a Google Cloud project id");
            if (!string.IsNullOrEmpty(location) && !Location().IsMatch(location))
                throw new AdminApiException(400, "settings.vertex.location must be global, us, eu or a region such as us-central1");
        }
        if (p.Settings["aws"] is { } awsNode)
        {
            if (awsNode is not JsonObject aws) throw new AdminApiException(400, "settings.aws must be an object");
            if (Text(aws, "region") is { Length: > 0 } region && !Region().IsMatch(region))
                throw new AdminApiException(400, "settings.aws.region is not an AWS region such as us-east-1");
            if (Text(aws, "service") is { Length: > 0 } service && !Service().IsMatch(service))
                throw new AdminApiException(400, "settings.aws.service is not an AWS signing service name");
        }
        // The service account's access token goes to the provider's endpoints, so they must be Google's
        if (p.AuthScheme == AuthSchemes.GcpServiceAccount
            && p.Endpoints.Any(e => !Uri.TryCreate(e.BaseUrl, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps
                || !u.Host.EndsWith(".googleapis.com", StringComparison.OrdinalIgnoreCase)))
            throw new AdminApiException(400, "A Google service account's endpoints must be https addresses at googleapis.com");
        // An IAM key's signature is scoped to its region: only AWS hosts get it
        if (p.AuthScheme == AuthSchemes.AwsSigV4
            && p.Endpoints.Any(e => !Uri.TryCreate(e.BaseUrl, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps
                || !(u.Host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".api.aws", StringComparison.OrdinalIgnoreCase))))
            throw new AdminApiException(400, "An AWS IAM key signs requests for AWS hosts only (amazonaws.com or api.aws)");
    }

    private static string? Text(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;

    [GeneratedRegex("^[a-z0-9][a-z0-9.:-]*[a-z0-9]$")] private static partial Regex ProjectId();
    [GeneratedRegex("^[a-z]+(-[a-z0-9]+)*$")] private static partial Regex Location();
    [GeneratedRegex("^[a-z]{2,}(-[a-z0-9]+)+$")] private static partial Regex Region();
    [GeneratedRegex("^[a-z][a-z0-9-]{1,40}$")] private static partial Regex Service();
}
