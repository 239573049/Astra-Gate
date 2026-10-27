using System.Diagnostics;
using System.Net;
using Astra.Core;
using Astra.Gateway.Pipeline;

namespace Astra.Server.Relay;

/// <summary>
/// Every upstream request of the relay passes here: it must go to one of the current caller's provider origins and carry
/// that caller's credential — never another caller's. Response headers have a deadline, and the upstream's diagnostics
/// (retry hints, rate limits, request ids) are handed to the caller.
/// </summary>
internal sealed class RelayTransport(RelayOptions options, IHttpContextAccessor accessor, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    /// <summary>
    /// The network: no redirects (a credential follows no Location), no cookies (callers share nothing), the system proxy,
    /// and no trace context sent upstream.
    /// </summary>
    internal static HttpMessageHandler NetworkHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        Proxy = SystemProxy.Dynamic,
        ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator(),
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var caller = RelayCaller.Current(accessor);
        if (request.RequestUri is not { } uri || !caller.MaySend(uri))
            throw new GatewayException(502, "api_error", "拒绝把凭据发往模板之外的地址。");
        if (!caller.Carries(request))
            throw new InvalidOperationException("An upstream request must carry the current caller's credential.");
        request.Version = HttpVersion.Version20;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        request.Headers.AcceptEncoding.Clear();
        request.Headers.AcceptEncoding.ParseAdd("identity");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.HeadersTimeoutSeconds));
        try
        {
            var response = await base.SendAsync(request, deadline.Token);
            if ((request.Method != HttpMethod.Get || !response.IsSuccessStatusCode)
                && accessor.HttpContext is { } context && !context.Response.HasStarted)
                RelayHttp.CopyDiagnostics(response.Headers, context.Response);
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GatewayException(504, "timeout", "等待上游响应头超时。");
        }
    }
}
