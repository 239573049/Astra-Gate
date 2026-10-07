using Astra.Core;
using Astra.Core.Privacy;
using Astra.Core.Requests;
using Astra.Data;
using Astra.Data.Repositories;
using Astra.Gateway.Pipeline;

namespace Astra.Server.Api;

/// <summary>Admin API for the privacy guard (plan §6.7): read/update policy, dry-run detection, event log.</summary>
public static class PrivacyEndpoints
{
    public sealed record DryRunRequest(string? Text, string? Json, string? ClientKind);

    public sealed record PrivacyEventDto(
        string Id, DateTimeOffset StartedAtUtc, string? ClientKind, string? ProviderId, string? ProviderName,
        string? RequestedModel, string Status, bool DryRun, bool Blocked, int Redactions, IReadOnlyList<PrivacyHit> Hits);

    public sealed record PrivacyCategoryStatDto(
        string Category, long Requests, long Hits, long WarnHits, long BlockHits, long RedactHits);

    public sealed record PrivacyEventStatsDto(
        long Events, long Blocked, long DryRun, long Redactions, IReadOnlyList<PrivacyCategoryStatDto> Categories);

    public static void MapPrivacyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/privacy", (PrivacyGuardService guard) => Results.Ok(new
        {
            settings = guard.Settings,
            rules = PrivacyDetector.BuiltInRules.Select(r => new
            {
                id = r.Id,
                category = r.Category,
                description = r.Description,
            }),
        }));

        app.MapPut("/api/privacy", async (PrivacySettings body, PrivacyGuardService guard) =>
        {
            try
            {
                var saved = await guard.UpdateAsync(body);
                return Results.Ok(saved);
            }
            catch (ArgumentException e)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["customRules"] = [e.Message],
                });
            }
        });

        // Interception log: every request that produced a guard outcome, newest first.
        app.MapGet("/api/privacy/events", async (HttpContext http, AstraDatabase db, CancellationToken ct) =>
        {
            var q = http.Request.Query;
            var query = new PrivacyEventQuery
            {
                From = RequestEndpoints.ParseTime(q["from"]),
                To = RequestEndpoints.ParseTime(q["to"]),
                ClientKind = RequestEndpoints.Str(q["client"]),
                ProviderId = RequestEndpoints.Str(q["provider"]),
                Category = RequestEndpoints.Str(q["category"]),
                Action = RequestEndpoints.Str(q["action"]),
                Blocked = q["blocked"].Count > 0 && bool.TryParse(q["blocked"], out var b) ? b : null,
                Model = RequestEndpoints.Str(q["model"]),
                Page = int.TryParse(q["page"], out var page) ? Math.Max(1, page) : 1,
                PageSize = int.TryParse(q["pageSize"], out var size) ? Math.Clamp(size, 1, 500) : 50,
            };
            var result = await db.Requests.QueryPrivacyEventsAsync(query, ct);
            return Results.Ok(new RequestEndpoints.PageDto<PrivacyEventDto>(
                result.Items.Select(ToEvent).ToList(), result.Total, result.Page, result.PageSize));
        });

        // Aggregate counters for the same filters; range=today|7d|30d|90d|all.
        app.MapGet("/api/privacy/events/stats", async (string? range, string? client, string? provider, string? category, string? action, AstraDatabase db, CancellationToken ct) =>
        {
            var (_, from, to) = RequestEndpoints.Range(range);
            var stats = await db.Requests.PrivacyEventStatsAsync(new PrivacyEventQuery
            {
                From = from,
                To = to,
                ClientKind = client,
                ProviderId = provider,
                Category = category,
                Action = action,
            }, ct);
            return Results.Ok(new PrivacyEventStatsDto(stats.Events, stats.Blocked, stats.DryRun, stats.Redactions,
                stats.Categories.Select(c => new PrivacyCategoryStatDto(c.Category, c.Requests, c.Hits, c.WarnHits, c.BlockHits, c.RedactHits)).ToList()));
        });

        // Dry run always evaluates as if the guard were enabled and enforcing.
        app.MapPost("/api/privacy/dry-run", (DryRunRequest body, PrivacyGuardService guard) =>
        {
            var input = body.Json ?? body.Text;
            if (string.IsNullOrWhiteSpace(input))
                return Results.BadRequest(new { error = "text 或 json 必填其一" });

            var policy = guard.EffectiveFor(body.ClientKind, enabledOverride: true, dryRunOverride: false);
            var detector = new PrivacyDetector();
            var applied = body.Json is null
                ? detector.Apply(input, policy)
                : detector.ApplyJson(input, policy) ?? detector.Apply(input, policy);

            return Results.Ok(new
            {
                blocked = applied.Blocked,
                hits = applied.Hits,
                wouldRedact = applied.Redactions.Count,
                categories = applied.Redactions.Select(r => r.Category).Distinct().ToList(),
            });
        });
    }

    /// <summary>Parses the stored report (snake_case storage JSON) into a camelCase wire DTO. Originals are never included.</summary>
    private static PrivacyEventDto ToEvent(PrivacyEventRow r)
    {
        var report = Json.Deserialize<PrivacyReport>(r.PrivacyJson) ?? new PrivacyReport();
        return new PrivacyEventDto(r.Id, r.StartedAtUtc, r.ClientKind, r.ProviderId, r.ProviderName,
            r.RequestedModel, r.Status, report.DryRun, report.Blocked, report.Redactions, report.Hits);
    }
}
