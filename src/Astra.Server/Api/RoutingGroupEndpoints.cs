using Astra.Core;
using Astra.Core.Models;
using Astra.Core.Routing;
using Astra.Data;
using Astra.Gateway.Routing;

namespace Astra.Server.Api;

/// <summary>
/// /api/routing/groups (magpie parity): named virtual models <c>group/&lt;slug&gt;</c> whose members are <c>provider/model</c> targets or
/// other groups, ordered per request by a mode (<see cref="GroupModes"/>). Writes are checked as a whole (<see cref="RoutingGroupValidator"/>:
/// a refusal is a 400 — 409 for a duplicate slug — whose <c>details.problems</c> hold every reason with a stable code) and saved one at a
/// time. A member's provider may be named by id, alias or name; it is stored as the id. The slug never changes once saved, so nothing that
/// names the group (another group's member, a smart-routing grade, a client's model) is left dangling by a rename; a group other groups
/// contain cannot be deleted.
/// </summary>
public static class RoutingGroupEndpoints
{
    private static readonly SemaphoreSlim Writes = new(1, 1);

    public static void MapRoutingGroupEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/routing/groups").AddEndpointFilter<AdminApiErrorFilter>();

        api.MapGet("", async (AstraDatabase db, CancellationToken ct) =>
            Results.Ok((await db.RoutingGroups.ListAsync(ct)).Select(ToDto).ToList()));

        // The live state of every group (cooling, quota left, last used) — one poll for the console's page.
        api.MapGet("/status", async (AstraDatabase db, GroupRouter router, CancellationToken ct) =>
        {
            var list = new List<RoutingGroupStatusDto>();
            foreach (var g in await db.RoutingGroups.ListAsync(ct)) list.Add(await StatusOfAsync(g, router, ct));
            return Results.Ok(list);
        });

        api.MapGet("/{id}", async (string id, AstraDatabase db, CancellationToken ct) =>
            Results.Ok(ToDto(await db.RoutingGroups.GetAsync(id, ct) ?? throw NotFound())));

        api.MapGet("/{id}/status", async (string id, AstraDatabase db, GroupRouter router, CancellationToken ct) =>
            Results.Ok(await StatusOfAsync(await db.RoutingGroups.GetAsync(id, ct) ?? throw NotFound(), router, ct)));

        api.MapPost("", async (RoutingGroupWrite body, AstraDatabase db, CancellationToken ct) =>
        {
            await Writes.WaitAsync(ct);
            try
            {
                var now = DateTimeOffset.UtcNow;
                var name = body.Name?.Trim() ?? "";
                var group = new RoutingGroup
                {
                    Id = Ulid.NewUlid(), Name = name, Slug = body.Slug?.Trim() is { Length: > 0 } slug ? slug : RoutingGroup.SlugFrom(name),
                    Mode = body.Mode ?? GroupModes.Smart, Stays = body.Stays ?? Stays.Auto, Enabled = body.Enabled ?? true, Listed = body.Listed ?? true,
                    CreatedAt = now, UpdatedAt = now,
                };
                group.SetMembers(await ResolveMembersAsync(body.Members ?? [], db, ct));
                await SaveCheckedAsync(group, db, insert: true, ct);
                return Results.Created($"/api/routing/groups/{group.Id}", ToDto(group));
            }
            finally
            {
                Writes.Release();
            }
        });

        api.MapPatch("/{id}", async (string id, RoutingGroupWrite body, AstraDatabase db, CancellationToken ct) =>
        {
            await Writes.WaitAsync(ct);
            try
            {
                var group = await db.RoutingGroups.GetAsync(id, ct) ?? throw NotFound();
                if (body.Slug is { } slug && slug != group.Slug)
                    throw new AdminApiException(400, "a group's slug cannot be changed", Problems([new RoutingProblem(RoutingGroupValidator.Codes.SlugInvalid, "a group's slug cannot be changed", "slug")]));
                if (body.Name is not null) group.Name = body.Name.Trim();
                if (body.Mode is not null) group.Mode = body.Mode;
                if (body.Stays is not null) group.Stays = body.Stays;
                if (body.Enabled is { } enabled) group.Enabled = enabled;
                if (body.Listed is { } listed) group.Listed = listed;
                if (body.Members is not null) group.SetMembers(await ResolveMembersAsync(body.Members, db, ct));
                group.UpdatedAt = DateTimeOffset.UtcNow;
                await SaveCheckedAsync(group, db, insert: false, ct);
                return Results.Ok(ToDto(group));
            }
            finally
            {
                Writes.Release();
            }
        });

        api.MapDelete("/{id}", async (string id, AstraDatabase db, CancellationToken ct) =>
        {
            await Writes.WaitAsync(ct);
            try
            {
                var group = await db.RoutingGroups.GetAsync(id, ct) ?? throw NotFound();
                var users = (await db.RoutingGroups.ListAsync(ct)).Where(g => g.Id != id && g.Members().Any(m => m.Group == group.Slug)).Select(g => g.Slug).ToList();
                if (users.Count > 0)
                    throw new AdminApiException(409, $"group '{group.Slug}' is a member of {string.Join(", ", users)}; remove it there first",
                        Problems([new RoutingProblem("group_in_use", string.Join(", ", users), "id")]));
                await db.RoutingGroups.DeleteAsync(id, ct);
                return Results.NoContent();
            }
            finally
            {
                Writes.Release();
            }
        });
    }

    private static async Task SaveCheckedAsync(RoutingGroup group, AstraDatabase db, bool insert, CancellationToken ct)
    {
        var others = (await db.RoutingGroups.ListAsync(ct)).Where(g => g.Id != group.Id).ToList();
        var providers = (await db.Providers.ListAsync(ct)).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var problems = RoutingGroupValidator.Check(group, others, providers);
        if (problems.Count > 0)
            throw new AdminApiException(problems.Any(p => p.Code == RoutingGroupValidator.Codes.SlugDuplicate) ? 409 : 400, problems[0].Message, Problems(problems));
        if (insert) await db.RoutingGroups.InsertAsync(group, ct);
        else await db.RoutingGroups.UpdateAsync(group, ct);
    }

    // A provider named by alias or name becomes its id; one that matches nothing stays as written (the validator then refuses it).
    private static async Task<List<GroupMember>> ResolveMembersAsync(List<RoutingGroupMemberDto> members, AstraDatabase db, CancellationToken ct)
    {
        var providers = (await db.Providers.ListAsync(ct)).ToList();
        string? Resolve(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim();
            if (providers.FirstOrDefault(p => p.Id == name) is { } byId) return byId.Id;
            var named = providers.Where(p => string.Equals(p.Alias(), name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count == 0) named = providers.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            return named.Count == 1 ? named[0].Id : name;
        }
        return members.Select(m => new GroupMember
        {
            Provider = Resolve(m.Provider), Model = m.Model?.Trim(), Group = string.IsNullOrWhiteSpace(m.Group) ? null : m.Group.Trim(),
            Weight = m.Weight,
        }).ToList();
    }

    private static async Task<RoutingGroupStatusDto> StatusOfAsync(RoutingGroup group, GroupRouter router, CancellationToken ct)
    {
        var (members, last) = await router.StatusAsync(group, ct);
        return new RoutingGroupStatusDto(group.Id, group.Slug, last?.At, last?.Reason, members.Select(m =>
        {
            var window = m.Standing.Known ? m.Standing.Deciding : null;
            return new RoutingGroupMemberStatusDto(
                m.Member.Key, m.Provider?.Id ?? m.Member.Provider, m.Provider?.Name, m.Member.Model, m.Member.Group, m.Member.Weight, m.Health, m.CoolingUntil,
                m.Standing.Known ? Math.Round(m.Standing.Used, 1) : null, window?.Kind, window?.ResetsAt,
                m.Standing.Pace(DateTimeOffset.UtcNow) is { } pace ? Math.Round(pace, 2) : null, m.Activity.LastUsed, m.Activity.Recent, m.Problem);
        }).ToList());
    }

    private static RoutingGroupDto ToDto(RoutingGroup g) =>
        new(g.Id, g.Slug, g.ModelId, g.Name, g.Mode, g.Stays, g.Enabled, g.Listed,
            g.Members().Select(m => new RoutingGroupMemberDto(m.Provider, m.Model, m.Group, m.Weight)).ToList(), g.CreatedAt, g.UpdatedAt);

    private static RoutingProblemsDetails Problems(IReadOnlyList<RoutingProblem> problems) =>
        new(problems.Select(p => new RoutingProblemDto(p.Code, p.Message, p.Field)).ToList());

    private static AdminApiException NotFound() => new(404, "Routing group not found");
}
