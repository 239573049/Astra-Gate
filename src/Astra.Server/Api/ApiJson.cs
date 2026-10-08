using Astra.Core;
using Microsoft.AspNetCore.Http;

namespace Astra.Server.Api;

/// <summary>
/// Produces JSON results through source-generated metadata so endpoint payloads never take the
/// reflection path. The <c>Results.Json(value, JsonSerializerOptions, …)</c> family is annotated
/// RequiresDynamicCode/RequiresUnreferencedCode — a <see cref="JsonSerializerOptions.TypeInfoResolver"/>
/// cannot silence it — so any result that is not a plain <c>Results.Ok</c> should go through here.
///
/// Formatting comes from <see cref="Json.Api"/> (camelCase, relaxed encoder), the same options the
/// ASP.NET HTTP JSON pipeline is configured with, so the wire format is unchanged.
/// </summary>
internal static class ApiJson
{
    /// <summary>A JSON result carrying the status code (e.g. an error envelope).</summary>
    public static IResult Result<T>(T value, int statusCode) =>
        Results.Json(value, JsonContexts.Info<T>(Json.Api), statusCode: statusCode);

    /// <summary>Metadata for <typeparamref name="T"/> under the API options, for WriteAsJsonAsync.</summary>
    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> Info<T>() =>
        JsonContexts.Info<T>(Json.Api);
}
