namespace Astra.Server.Power;

/// <summary>
/// Counts gateway requests for <see cref="KeepAwakeService"/> from their start until the endpoint returns — for a streamed
/// response that is when the stream ended or the client left. Registered in <c>AstraApp.Create</c> only, so the relay host
/// has none and <c>PipelineContracts</c> are untouched.
/// </summary>
public sealed class KeepAwakeMiddleware(RequestDelegate next, KeepAwakeService keepAwake)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsGatewayWork(context.Request))
        {
            await next(context);
            return;
        }
        keepAwake.Enter();
        try
        {
            await next(context);
        }
        finally
        {
            keepAwake.Exit();
        }
    }

    /// <summary>
    /// A POST to the gateway under <c>/v1</c> or <c>/v1beta</c> (conversations and token counts) — not a model list, and not
    /// the token self-service views under <c>/v1/astra</c>.
    /// </summary>
    internal static bool IsGatewayWork(HttpRequest request) =>
        HttpMethods.IsPost(request.Method)
        && (request.Path.StartsWithSegments("/v1") || request.Path.StartsWithSegments("/v1beta"))
        && !request.Path.StartsWithSegments("/v1/astra");
}
