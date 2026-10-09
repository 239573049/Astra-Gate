using System.Net;
using Astra.Clients.Config;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Data;
using Astra.Gateway.Http;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Protocol;
using Astra.Providers.Quota;
using Astra.Providers.Subscription;
using Astra.Providers.Templates;
using Astra.Server.Api;
using Astra.Server.Security;
using Microsoft.AspNetCore.DataProtection;

namespace Astra.Server.Hosting;

/// <summary>Builds the Astra web application (used by `serve` and by integration tests).</summary>
public static class AstraApp
{
    public sealed class BuildOptions
    {
        public required AstraPaths Paths { get; init; }
        public required ServerOptions Server { get; init; }

        /// <summary>Integration tests plug in TestServer / fake upstreams here.</summary>
        public Action<WebApplicationBuilder>? ConfigureBuilder { get; init; }

        public bool WriteRuntimeFile { get; init; } = true;

        /// <summary>Pre-initialized database (tests); otherwise migrated + seeded from <see cref="Paths"/>.</summary>
        public AstraDatabase? Database { get; init; }
    }

    public static WebApplication Create(BuildOptions o)
    {
        var paths = o.Paths;
        var server = o.Server;
        paths.EnsureCreated();
        // Migrations + seed import run before the host starts (console context: no sync-context deadlock risk).
        var database = o.Database ?? AstraDatabase.InitializeAsync(paths).GetAwaiter().GetResult();

        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Directory.Exists(webRoot) ? webRoot : null,
        });

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            if (server.Host is "localhost") k.ListenLocalhost(server.Port);
            else if (server.Host is "0.0.0.0" or "*") k.ListenAnyIP(server.Port);
            else k.Listen(IPAddress.Parse(server.Host.Trim('[', ']')), server.Port);
        });

        var level = Enum.TryParse<LogLevel>(server.LogLevel, true, out var l) ? l : LogLevel.Information;
        builder.Logging.SetMinimumLevel(level);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
        // Keys live in ~/.astra/keys with owner-only permissions; the "unencrypted key" warning is expected.
        builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error);
        builder.Logging.AddProvider(new FileLoggerProvider(paths.LogsDir, level));

        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(paths.KeysDir))
            .SetApplicationName("Astra");

        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(server);
        builder.Services.AddSingleton(database);
        builder.Services.AddSingleton(ClientEnvironment.Real);
        builder.Services.AddSingleton<ClientInstallService>();
        builder.Services.AddSingleton<ClientService>();
        builder.Services.AddSingleton<TokenService>();
        builder.Services.AddHostedService<TokenMigrationWorker>();
        builder.Services.AddSingleton<ProviderTemplateCatalog>();
        builder.Services.AddSingleton<ProviderProbe>();
        builder.Services.AddSingleton<ImportService>();
        builder.Services.AddSingleton<IModelProtocolResolver>(sp => sp.GetRequiredService<ProviderProbe>());
        builder.Services.AddSingleton<ModelSyncService>();
        builder.Services.AddSingleton<Astra.Core.Models.IProviderAccountStore>(database.Accounts);
        builder.Services.AddSingleton<SettingsService>();
        builder.Services.AddSingleton<PrivacyGuardService>();
        builder.Services.AddSingleton<EffectiveModelResolver>();
        builder.Services.AddSingleton<BodyStore>();
        builder.Services.AddSingleton<LiveRequestFeed>();
        builder.Services.AddSingleton<UsageWriter>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<UsageWriter>());
        builder.Services.AddHostedService<RetentionService>();
        builder.Services.AddSingleton<OAuthClient>();
        builder.Services.AddSingleton<SubscriptionTokenService>();
        builder.Services.AddSingleton<SubscriptionQuotaService>();
        builder.Services.AddSingleton<UpstreamAuthResolver>();
        // Gateway (plan §6): codecs are registered by GatewayCodecs.Register; the pipeline picks them per protocol.
        GatewayCodecs.Register(builder.Services);
        builder.Services.AddSingleton<CodecRegistry>();
        builder.Services.AddSingleton<ClientRouting>();
        builder.Services.AddSingleton<GatewayRouter>();
        builder.Services.AddSingleton<GatewayHttpClients>();
        builder.Services.AddSingleton<GatewayPipeline>();
        builder.Services.AddSingleton<GatewayAuxiliary>();
        builder.Services.AddSingleton<PendingSubscriptionLogins>();
        builder.Services.AddHostedService<SubscriptionRefreshWorker>();
        // Balance / quota queries of API-key providers: built-in templates, the query service, and its worker.
        builder.Services.AddSingleton<QuotaTemplateCatalog>();
        builder.Services.AddSingleton<ProviderQuotaService>();
        builder.Services.AddSingleton<ProviderQuotaManager>();
        builder.Services.AddHostedService<ProviderQuotaWorker>();
        builder.Services.AddSingleton<UpdateCheckService>();
        builder.Services.AddHostedService<UpdateCheckWorker>();
        builder.Services.AddSingleton<RuntimeFile>();
        builder.Services.AddSingleton<AdminSessions>();
        builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        builder.Services.AddHttpClient();
        // 订阅登录（OAuthClient 用默认客户端）、额度、模型列表、更新检查都走系统代理；
        // NO_PROXY 里的地址（回环）自动豁免。
        builder.Services.ConfigureHttpClientDefaults(b =>
            b.ConfigurePrimaryHttpMessageHandler(() => SystemProxy.CreateHandler(TimeSpan.FromSeconds(15))));
        builder.Services.AddHttpClient(ProviderProbe.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(130))
            .ConfigurePrimaryHttpMessageHandler(() => SystemProxy.CreateHandler(TimeSpan.FromSeconds(15)))
            .RemoveAllLoggers();
        builder.Services.AddHttpClient(ModelSyncService.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => SystemProxy.CreateHandler(TimeSpan.FromSeconds(15)))
            .RemoveAllLoggers();
        builder.Services.AddHttpClient(SubscriptionQuotaService.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => SystemProxy.CreateHandler(TimeSpan.FromSeconds(15)))
            .RemoveAllLoggers();
        // Per-query deadlines come from settings.quota.timeout_sec (≤ 60 s); this is only the outer bound.
        builder.Services.AddHttpClient(ProviderQuotaService.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(90))
            .ConfigurePrimaryHttpMessageHandler(() => SystemProxy.CreateHandler(TimeSpan.FromSeconds(15)))
            .RemoveAllLoggers();
        builder.Services.AddHttpClient(UpdateCheckService.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => SystemProxy.CreateHandler(TimeSpan.FromSeconds(15)))
            .RemoveAllLoggers();
        builder.Services.AddHttpClient(ClientInstallService.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => SystemProxy.CreateHandler(TimeSpan.FromSeconds(15)))
            .RemoveAllLoggers();
        builder.Services.AddOpenApi();
        builder.Services.ConfigureHttpJsonOptions(j =>
        {
            j.SerializerOptions.PropertyNamingPolicy = Json.Api.PropertyNamingPolicy;
            j.SerializerOptions.DefaultIgnoreCondition = Json.Api.DefaultIgnoreCondition;
            j.SerializerOptions.Encoder = Json.Api.Encoder;
            // Source-generated metadata for endpoint payloads; unregistered types fall back to reflection.
            j.SerializerOptions.TypeInfoResolver = JsonContexts.Resolver;
        });
        builder.Services.AddCors(c => c.AddDefaultPolicy(p => p
            .WithOrigins(SecurityMiddleware.DesktopOrigin)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials()));

        o.ConfigureBuilder?.Invoke(builder);

        var app = builder.Build();
        app.Services.GetRequiredService<SettingsService>().LoadAsync().GetAwaiter().GetResult();
        app.Services.GetRequiredService<PrivacyGuardService>().LoadAsync().GetAwaiter().GetResult();
        // Tokens: the 0006 migration inserts the default token without a key; generate it before the gateway serves.
        app.Services.GetRequiredService<TokenService>().EnsureDefaultAsync().GetAwaiter().GetResult();

        app.UseCors();
        app.UseMiddleware<SecurityMiddleware>();
        if (app.Environment.WebRootPath is not null)
        {
            app.UseDefaultFiles();
            // Hashed bundles are immutable; everything else (index.html) must revalidate so upgrades show up immediately.
            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = c => c.Context.Response.Headers.CacheControl =
                    c.Context.Request.Path.StartsWithSegments("/assets") ? "public, max-age=31536000, immutable" : "no-cache",
            });
        }

        app.MapOpenApi("/api/openapi.json");
        app.MapSystemEndpoints();
        app.MapModelEndpoints();
        app.MapModelSyncEndpoints();
        app.MapRequestEndpoints();
        app.MapClientEndpoints();
        app.MapTokenEndpoints();
        app.MapProviderEndpoints();
        app.MapPrivacyEndpoints();
        app.MapSubscriptionEndpoints();
        app.MapUpdateEndpoints();
        app.MapGatewayEndpoints();

        // SPA fallback for everything that is not an API / gateway route.
        app.MapFallback(async ctx =>
        {
            var p = ctx.Request.Path;
            var index = app.Environment.WebRootPath is { } root ? Path.Combine(root, "index.html") : null;
            if (p.StartsWithSegments("/api") || p.StartsWithSegments("/v1") || p.StartsWithSegments("/v1beta") || index is null || !File.Exists(index))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsJsonAsync(new Api.ErrorOnlyDto($"Not found: {ctx.Request.Method} {p}"), Api.ApiJson.Info<Api.ErrorOnlyDto>());
                return;
            }
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-cache";
            await ctx.Response.SendFileAsync(index);
        });

        if (o.WriteRuntimeFile)
        {
            var runtime = app.Services.GetRequiredService<RuntimeFile>();
            app.Lifetime.ApplicationStarted.Register(() => runtime.Write(server));
            app.Lifetime.ApplicationStopped.Register(runtime.Delete);
        }

        return app;
    }
}
