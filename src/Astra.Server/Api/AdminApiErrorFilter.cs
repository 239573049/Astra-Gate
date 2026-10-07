using System.Text.Json;
using Astra.Clients.Editing;

namespace Astra.Server.Api;

public sealed class AdminApiException(int statusCode, string message, object? details = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public object? Details { get; } = details;
}

/// <summary>Consistent JSON errors for admin operations (validation or client configuration IO).</summary>
public sealed class AdminApiErrorFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (AdminApiException ex) { return Results.Json(new ErrorBody(ex.Message, ex.Details), statusCode: ex.StatusCode); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or EditorException)
        { return Results.Json(new ErrorBody(ex.Message), statusCode: 400); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Results.Json(new ErrorBody("Configuration file could not be updated: " + ex.Message), statusCode: 409); }
    }
}
