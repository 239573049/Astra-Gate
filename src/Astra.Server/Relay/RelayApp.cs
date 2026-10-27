using System.Net;
using System.Text.Json;
using Astra.Core;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Protocol;
using Astra.Gateway.Tracing;
using Astra.Providers.Templates;
using Astra.Server.Api;
using Astra.Server.Hosting;

namespace Astra.Server.Relay;

/// <summary>
/// <c>astra-server relay</c>: a gateway without storage, for one provider template. Every request brings its own
/// upstream credential — a subscription's access token or an API key — and runs through the same
/// <see cref="GatewayPipeline"/> as the server's requests (protocol conversion, streaming, the template's headers and
/// model handling); the model list, embeddings and token counting are relayed as the upstream answers them. There are
/// no tokens, accounts, request log, console or plugins, nothing is written to disk, and callers share no state, so
/// any number of copies can serve behind a load balancer.
/// </summary>
public static class RelayApp
{
    /// <summary>Builds the relay. Tests pass <paramref name="configure"/> (TestServer) and <paramref name="transport"/> (a fake upstream).</summary>
    /// <exception cref="ArgumentException">The template or variant is unknown, or the template cannot be relayed.</exception>
    public static WebApplication Create(RelayOptions options, Action<WebApplicationBuilder>? configure = null,
        Func<HttpMessageHandler>? transport = null)
    {
        var target = RelayTarget.Create(options, new ProviderTemplateCatalog());
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes;
            // The body deadline (RequestBodyLimits) and the stream timeouts replace the minimum data rates.
            k.Limits.MinRequestBodyDataRate = null;
            k.Limits.MinResponseDataRate = null;
            if (options.Host is "localhost") k.ListenLocalhost(options.Port);
            else if (options.Host is "0.0.0.0" or "*") k.ListenAnyIP(options.Port);
            else k.Listen(IPAddress.Parse(options.Host.Trim('[', ']')), options.Port);
        });
        // Request summaries only: credentials, bodies and upstream addresses are never logged.
        builder.Logging.AddFilter((string?)null, LogLevel.Warning);
        builder.Logging.AddFilter("Astra.Server.Relay", LogLevel.Information);
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(options.ShutdownTimeoutSeconds));
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(target);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(sp => new GatewayHttpClients
        {
            HandlerFactory = _ => new RelayTransport(options, sp.GetRequiredService<IHttpContextAccessor>(),
                transport?.Invoke() ?? RelayTransport.NetworkHandler()),
        });
        GatewayCodecs.Register(builder.Services);
        builder.Services.AddSingleton<CodecRegistry>();
        builder.Services.AddSingleton<RelayCatalog>();
        builder.Services.AddScoped<RelayRuntime>();
        // What the server keeps for the life of the process — account and provider scheduling, request linking, the live
        // request feed — lives for one request here: callers bring different credentials and share nothing.
        builder.Services.AddScoped<AccountScheduler>();
        builder.Services.AddScoped<LiveRequestFeed>();
        builder.Services.AddScoped<TraceLinker>();
        builder.Services.AddScoped<ProviderPoolScheduler>();
        builder.Services.AddScoped<ProviderHealth>();
        builder.Services.AddScoped(sp =>
        {
            var runtime = sp.GetRequiredService<RelayRuntime>();
            return new GatewayPipeline(router: runtime, codecs: sp.GetRequiredService<CodecRegistry>(),
                http: sp.GetRequiredService<GatewayHttpClients>(), auth: runtime, scheduler: sp.GetRequiredService<AccountScheduler>(),
                models: runtime, modelProtocols: runtime, settings: runtime, privacy: null, bodies: null, usageWriter: runtime,
                live: sp.GetRequiredService<LiveRequestFeed>(), middleware: null, pluginTransport: null, limiter: null,
                linker: sp.GetRequiredService<TraceLinker>(), pools: null, poolScheduler: sp.GetRequiredService<ProviderPoolScheduler>(),
                health: sp.GetRequiredService<ProviderHealth>(), smartRouter: runtime, logger: sp.GetRequiredService<ILogger<GatewayPipeline>>(),
                bodyLimits: new RequestBodyLimits(options.MaxRequestBodyBytes, TimeSpan.FromSeconds(options.RequestBodyTimeoutSeconds)));
        });
        configure?.Invoke(builder);

        var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Astra.Server.Relay");
        // In flight at once: one more is refused at once rather than queued; health checks are never counted.
        var slots = new SemaphoreSlim(options.MaxConcurrentRequests, options.MaxConcurrentRequests);
        app.Lifetime.ApplicationStopped.Register(slots.Dispose);
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                context.Abort();
            }
            catch (GatewayException e)
            {
                await ErrorAsync(context, logger, e.Status, e.Type, e.Message);
            }
            catch (BadHttpRequestException e)
            {
                await ErrorAsync(context, logger, e.StatusCode, "invalid_request_error", "请求体无效。");
            }
            catch (JsonException)
            {
                await ErrorAsync(context, logger, 400, "invalid_request_error", "请求体不是有效的 JSON。");
            }
            catch (HttpRequestException e)
            {
                await ErrorAsync(context, logger, 502, "api_error",
                    $"无法连接上游：{e.Message}{(e.InnerException is { } inner ? " " + inner.Message : "")}");
            }
            catch (IOException)
            {
                await ErrorAsync(context, logger, 502, "api_error", "与上游或客户端的连接中断。");
            }
            catch (Exception e)
            {
                logger.LogError(e, "Relay request failed");
                await ErrorAsync(context, logger, 500, "api_error", "中转内部错误。");
            }
        });

        app.MapGet("/healthz", () => Results.Text("ok"));
        app.MapGet("/readyz", () => app.Lifetime.ApplicationStopping.IsCancellationRequested ? Results.StatusCode(503) : Results.Text("ok"));
        foreach (var prefix in new[] { "", "/v1" })
        {
            app.MapPost(prefix + "/chat/completions", context => HandleAsync(context, ApiProtocol.OpenAIChat));
            app.MapPost(prefix + "/responses", context => HandleAsync(context, ApiProtocol.OpenAIResponses));
            app.MapPost(prefix + "/messages", context => HandleAsync(context, ApiProtocol.Anthropic));
            app.MapPost(prefix + "/messages/count_tokens", context => HandleAsync(context, ApiProtocol.Anthropic, Relayed.CountTokens));
            app.MapPost(prefix + "/embeddings", context => HandleAsync(context, ApiProtocol.OpenAIChat, Relayed.Embeddings));
            app.MapGet(prefix + "/models", context => HandleAsync(context, ApiProtocol.OpenAIChat, Relayed.Models));
        }
        app.MapPost("/v1beta/models/{**action}", context => HandleAsync(context, ApiProtocol.Gemini));
        return app;

        async Task HandleAsync(HttpContext context, ApiProtocol protocol, Relayed? relayed = null)
        {
            context.Features.Set(new Inbound(protocol));
            var caller = target.Caller(context.Request);
            if (!await slots.WaitAsync(0, context.RequestAborted))
                throw new GatewayException(503, "overloaded_error", "中转的并发请求已满，请稍后重试。");
            context.Features.Set(caller);
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                if (relayed is { } kind)
                {
                    await RelayAsync(context, caller, kind, options);
                    return;
                }
                string? pathModel = null;
                bool? stream = null;
                if (protocol == ApiProtocol.Gemini)
                {
                    var action = context.Request.RouteValues["action"]?.ToString() ?? "";
                    var separator = action.LastIndexOf(':');
                    if (separator <= 0 || separator > 512 || action[(separator + 1)..] is not ("generateContent" or "streamGenerateContent"))
                        throw new GatewayException(404, "not_found_error", "中转只支持 Gemini 的 generateContent 与 streamGenerateContent。");
                    pathModel = action[..separator];
                    stream = action[(separator + 1)..] == "streamGenerateContent";
                }
                await context.RequestServices.GetRequiredService<GatewayPipeline>().HandleAsync(context, protocol, pathModel, stream,
                    context.Request.Query["alt"] == "sse");
            }
            finally
            {
                context.Features.Set<RelayCaller>(null);
                slots.Release();
            }
        }
    }

    /// <summary>Runs the relay until it is stopped (SIGTERM / Ctrl+C).</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            Console.WriteLine(RelayOptions.Usage);
            return 0;
        }
        var options = RelayOptions.Load(args, Environment.GetEnvironmentVariable);
        var app = Create(options);
        try
        {
            await app.StartAsync();
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Cannot listen on {options.Host}:{options.Port}: {ex.Message}");
            return 3;
        }
        var target = app.Services.GetRequiredService<RelayTarget>();
        Console.WriteLine($"Astra {ServerOptions.Version} relay for {target.Template.Id}"
                          + $"{(target.Variant == RelayTarget.DefaultVariant ? "" : $" ({target.Variant})")}"
                          + $" listening on http://{(options.Host.Contains(':') ? $"[{options.Host}]" : options.Host)}:{options.Port}");
        await app.WaitForShutdownAsync();
        return 0;
    }

    /// <summary>The routes the relay answers by relaying the upstream's own answer.</summary>
    private enum Relayed
    {
        Models,
        Embeddings,
        CountTokens,
    }

    /// <summary>The inbound protocol, for errors raised before the pipeline runs.</summary>
    private sealed record Inbound(ApiProtocol Protocol);

    /// <summary>
    /// The model list, embeddings and Anthropic token counting: sent to the template's own endpoint for them, with the
    /// same headers as a generation request, and answered with the upstream's status, body and diagnostics as they are.
    /// </summary>
    private static async Task RelayAsync(HttpContext context, RelayCaller caller, Relayed kind, RelayOptions options)
    {
        var ct = context.RequestAborted;
        var provider = caller.Provider;
        using var body = kind == Relayed.Models
            ? null
            : await RequestBody.ReadAsync(context.Request, options.MaxRequestBodyBytes, ct, TimeSpan.FromSeconds(options.RequestBodyTimeoutSeconds));
        string? model = null;
        if (body is not null)
        {
            if (body.Parse() is not { } parsed || !parsed.Root.TryGetProperty("model", out var field)
                || field.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(model = field.GetString()))
                throw new GatewayException(400, "invalid_request_error", "请求体必须是带 model 的 JSON 对象。");
        }
        var openAi = provider.EndpointFor(ApiProtocol.OpenAIChat) ?? provider.EndpointFor(ApiProtocol.OpenAIResponses);
        var anthropic = provider.EndpointFor(ApiProtocol.Anthropic);
        var url = kind switch
        {
            Relayed.Models => caller.ModelListUrl,
            Relayed.Embeddings => openAi is { FullUrl: false } ? ProviderProbe.AppendPath(openAi.BaseUrl, "/embeddings") : null,
            _ => anthropic is null ? null : UpstreamUrls.CountTokens(anthropic, model!),
        };
        if (url is null)
        {
            if (kind == Relayed.CountTokens)
            {
                // No Anthropic endpoint to ask: a rough local count (≈ 4 characters per token), as the server gives.
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync($"{{\"input_tokens\":{Math.Max(1, System.Text.Encoding.UTF8.GetCharCount(body!.Utf8) / 4)}}}", ct);
                return;
            }
            throw new GatewayException(404, "not_found_error",
                kind == Relayed.Models ? $"{provider.Name} 没有模型列表接口。" : $"{provider.Name} 没有 embeddings 接口。");
        }
        using var request = caller.Request(body is null ? HttpMethod.Get : HttpMethod.Post, url + context.Request.QueryString.Value, perRequest: true);
        if (kind == Relayed.Models) caller.ModelList?.ApplyHeaders(request);
        if (kind == Relayed.CountTokens)
        {
            foreach (var name in new[] { "anthropic-version", "anthropic-beta" })
                if (context.Request.Headers.TryGetValue(name, out var value) && !RelayHttp.IsHopHeader(name, context.Request.Headers.Connection))
                    request.Headers.TryAddWithoutValidation(name, value.ToArray());
            if (!request.Headers.Contains("anthropic-version")) request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        if (body is not null) request.Content = new PooledContent(body.Buffer);
        using var response = await context.RequestServices.GetRequiredService<GatewayHttpClients>().For(provider)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        context.Response.StatusCode = (int)response.StatusCode;
        RelayHttp.CopyDiagnostics(response.Headers, context.Response);
        foreach (var (name, values) in response.Content.Headers)
            if (!RelayHttp.IsHopHeader(name, response.Headers.Connection))
                context.Response.Headers[name] = values.ToArray();
        context.Response.Headers.CacheControl = "no-store";
        await RelayHttp.CopyAsync(await response.Content.ReadAsStreamAsync(ct), context.Response.Body, options, ct);
    }

    /// <summary>An error in the inbound protocol's shape; once the answer has started, the connection is cut instead.</summary>
    private static async Task ErrorAsync(HttpContext context, ILogger logger, int status, string type, string message)
    {
        logger.LogWarning("Request failed: status={Status} type={Type}", status, type);
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }
        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        if (status == 503) context.Response.Headers.RetryAfter = "1";
        var protocol = context.Features.Get<Inbound>()?.Protocol ?? ApiProtocol.OpenAIChat;
        var codec = context.RequestServices.GetRequiredService<CodecRegistry>().Require(protocol);
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(codec.EncodeError(status, type, message).ToJsonString(GatewayJson.Options), context.RequestAborted);
    }
}
