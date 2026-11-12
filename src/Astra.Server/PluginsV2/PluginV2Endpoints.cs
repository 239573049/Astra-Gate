using System.Text;
using System.Text.Json.Nodes;
using Astra.Gateway.Http;
using Astra.Plugins.V2;
using Astra.Plugins.V2.Runtime;
using Astra.Plugins.V2.Ui;
using Astra.Server.Api;

namespace Astra.Server.PluginsV2;

/// <summary>
/// /api/plugins/v2 (design/plugin-v2): the v2 plugins, their permissions, settings, commands and assets; runtime
/// permission requests; the v1 → v2 update reminder; and the view sessions the shells render (open, a server-sent event
/// stream of patches / effects / questions / data, events, answers). Admin API rules apply (a session off this machine,
/// <c>X-Astra-Admin</c> on every change); installing from a folder is this machine only, as for v1.
/// </summary>
public static class PluginV2Endpoints
{
    public static void MapPluginV2Endpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/plugins/v2").AddEndpointFilter<AdminApiErrorFilter>();

        g.MapGet("", async (string? locale, PluginV2Service v2, CancellationToken ct) => Results.Ok(await v2.ListAsync(locale ?? "en", ct)));
        g.MapGet("/notifications", (long? after, PluginV2Service v2) => Results.Ok(v2.Notifications(after ?? 0)));
        g.MapGet("/successors", async (string? locale, PluginV2Service v2, CancellationToken ct) => Results.Ok(await v2.SuccessorsAsync(locale ?? "en", ct)));
        g.MapPost("/successors/dismiss", async (PluginSuccessorDismissInput body, PluginV2Service v2, CancellationToken ct) =>
        {
            await v2.DismissAsync(body, ct);
            return Results.NoContent();
        });
        g.MapPost("/upgrade", (PluginUpgradeInput body, PluginV2Service v2, CancellationToken ct) => Results.Ok(v2.Upgrade(body, ct).ToDto()));
        g.MapPost("/requests/{requestId}", (string requestId, PluginPermissionAnswerInput body, PluginV2Service v2) =>
        {
            v2.AnswerRequest(requestId, body.Allow);
            return Results.NoContent();
        });
        g.MapGet("/ui/schema", () => Results.Text(UiSchema.Json, "application/json"));
        // the component gallery's scenes (design/plugin-v2 06 §2), normalised like a plugin's tree
        g.MapGet("/ui/scenes", () => Results.Text(UiGallery.Scenes().ToJsonString(), "application/json"));

        g.MapGet("/{id}", async (string id, string? locale, PluginV2Service v2, CancellationToken ct) => Results.Ok(await v2.DetailAsync(id, locale ?? "en", ct)));
        g.MapPost("/{id}/grant", async (string id, string? locale, PluginV2GrantInput body, PluginV2Service v2, CancellationToken ct) =>
            Results.Ok(await v2.GrantAsync(id, body, locale ?? "en", ct)));
        g.MapPost("/{id}/revoke", async (string id, string? locale, PluginV2GrantInput body, PluginV2Service v2, CancellationToken ct) =>
            Results.Ok(await v2.RevokeAsync(id, body, locale ?? "en", ct)));
        g.MapPut("/{id}/settings", async (string id, string? locale, PluginV2SettingsInput body, PluginV2Service v2, CancellationToken ct) =>
            Results.Ok(await v2.SettingsAsync(id, body, locale ?? "en", ct)));
        g.MapPost("/{id}/commands/{command}", async (string id, string command, PluginV2CommandInput? body, PluginV2Service v2, CancellationToken ct) =>
            Results.Ok(new PluginV2CommandResultDto(await v2.CommandAsync(id, command, body?.Args, ct))));
        // a dev plugin's tests, in the sandbox (astra plugin test): runs the folder's code, so this machine only
        g.MapPost("/{id}/test", async (string id, PluginV2Service v2, HttpContext ctx, CancellationToken ct) =>
            Astra.Gateway.Http.RequestOrigin.IsLocal(ctx) ? Results.Ok(await v2.TestAsync(id, ct)) : throw new AdminApiException(403, "plugin tests run from this machine only"));
        g.MapPost("/{id}/rollback", (string id, PluginV2Service v2) => Results.Ok(v2.Rollback(id).ToDto()));
        g.MapGet("/{id}/assets/{**path}", async (string id, string path, PluginV2Service v2, HttpContext ctx, CancellationToken ct) =>
        {
            var (file, type) = await v2.AssetAsync(id, path, ct);
            ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(file, type);
        });

        var ui = g.MapGroup("/ui/sessions");
        ui.MapPost("", async (UiOpenInput body, PluginV2Host host, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(body.Plugin) || string.IsNullOrEmpty(body.View)) throw new AdminApiException(400, "plugin and view are required");
            try
            {
                var open = await host.Ui.OpenAsync(new UiOpenRequest(body.Plugin, body.View, body.Params, body.Client ?? new JsonObject(),
                    body.Query, body.Slot, body.Parent), ct);
                return Results.Ok(new UiOpenDto(open.SessionId, open.Rev, open.Tree, open.Warnings));
            }
            catch (PluginCallException e)
            {
                throw Mapped(e);
            }
        });
        ui.MapGet("/{session}/stream", async (string session, long? rev, PluginV2Host host, HttpContext ctx) =>
        {
            System.Threading.Channels.ChannelReader<JsonObject> reader;
            try
            {
                reader = host.Ui.Subscribe(session, rev ?? 0);
            }
            catch (PluginCallException e)
            {
                throw Mapped(e);
            }
            var ct = ctx.RequestAborted;
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            ctx.Response.ContentType = "text/event-stream; charset=utf-8";
            var gate = new SemaphoreSlim(1, 1);
            async Task WriteAsync(string text)
            {
                await gate.WaitAsync(ct);
                try
                {
                    await ctx.Response.WriteAsync(text, ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
                finally
                {
                    gate.Release();
                }
            }
            using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(15));
            var beats = Task.Run(async () =>
            {
                while (await heartbeat.WaitForNextTickAsync(ct)) await WriteAsync(": ping\n\n");
            }, ct);
            try
            {
                await WriteAsync(": connected\n\n");
                await foreach (var message in reader.ReadAllAsync(ct))
                    await WriteAsync(new StringBuilder("data: ").Append(message.ToJsonString()).Append("\n\n").ToString());
            }
            catch (Exception e) when (e is OperationCanceledException or IOException)
            {
            }
            finally
            {
                host.Ui.Unsubscribe(session, reader);
            }
            try
            {
                await beats;
            }
            catch (OperationCanceledException)
            {
            }
            return Results.Empty;
        });
        // the dev inspector: a dev plugin's session as the host holds it (404 for any other plugin)
        ui.MapGet("/{session}/inspect", async (string session, PluginV2Host host) =>
            await host.Ui.InspectAsync(session) is { } inspect ? Results.Text(inspect.ToJsonString(), "application/json") : Results.NotFound());
        ui.MapPost("/{session}/events", async (string session, UiEventInput body, PluginV2Host host, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(body.Action)) throw new AdminApiException(400, "action is required");
            try
            {
                await host.Ui.EventAsync(session, body.Action, body.Args, body.Local, body.Seq, ct);
            }
            catch (PluginCallException e)
            {
                throw Mapped(e);
            }
            return Results.NoContent();
        });
        ui.MapPost("/{session}/answers", (string session, UiAnswerInput body, PluginV2Host host) =>
            string.IsNullOrEmpty(body.Id) || !host.Ui.Answer(session, body.Id, body.Value)
                ? throw new AdminApiException(404, "no such question (it was answered or the view closed)")
                : Results.NoContent());
        ui.MapPost("/{session}/local", (string session, JsonObject body, PluginV2Host host) =>
        {
            host.Ui.SetLocal(session, body);
            return Results.NoContent();
        });
        ui.MapPost("/{session}/client", async (string session, UiClientInput body, PluginV2Host host, CancellationToken ct) =>
        {
            try
            {
                await host.Ui.SetClientAsync(session, body.Client ?? new JsonObject(), ct);
            }
            catch (PluginCallException e)
            {
                throw Mapped(e);
            }
            return Results.NoContent();
        });
        ui.MapPost("/{session}/files", (string session, UiFileInput body, PluginV2Host host) =>
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(body.Base64 ?? "");
            }
            catch (FormatException)
            {
                throw new AdminApiException(400, "base64 is not base64");
            }
            try
            {
                return Results.Ok(host.Ui.AddFile(session, body.Name ?? "file", body.Type ?? "application/octet-stream", bytes));
            }
            catch (PluginCallException e)
            {
                throw Mapped(e);
            }
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(40 * 1024 * 1024));
        ui.MapDelete("/{session}", async (string session, PluginV2Host host) =>
        {
            await host.Ui.CloseAsync(session);
            return Results.NoContent();
        });
    }

    private static AdminApiException Mapped(PluginCallException e) => new(e.Code switch
    {
        "not_found" => 404,
        "permission_denied" => 403,
        "not_supported" => 409,
        "timeout" => 504,
        "quota_exceeded" => 413,
        _ => 422,
    }, e.Message);
}
