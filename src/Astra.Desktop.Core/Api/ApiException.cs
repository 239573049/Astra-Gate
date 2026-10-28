using System.Text.Json.Nodes;

namespace Astra.Desktop.Core.Api;

/// <summary>
/// A failed admin-API call. Port of <c>ApiError</c> in web/src/api/client.ts: <see cref="Status"/> 0 means the server
/// could not be reached (or the call timed out), anything else is the HTTP status of the error answer. The message is the
/// server's <c>{ "error": "..." }</c> text (see <c>AdminApiErrorFilter</c>), else the plain-text body, else <c>HTTP n</c>.
/// </summary>
public sealed class ApiException(int status, string message, JsonNode? details = null, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>HTTP status, or 0 when no answer arrived.</summary>
    public int Status { get; } = status;

    /// <summary>The error body's <c>details</c> (for example <c>details.problems[]</c> of a validation refusal), if any.</summary>
    public JsonNode? Details { get; } = details;

    /// <summary>True when the server could not be reached at all (web: <c>isNetwork</c>).</summary>
    public bool IsNetwork => Status == 0;

    /// <summary>True for a 4xx answer: retrying the same request cannot help (what web's QueryClient default retry skips).</summary>
    public bool IsClientError => Status is >= 400 and < 500;
}
