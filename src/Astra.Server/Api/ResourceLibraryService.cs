using Astra.Clients.Config;
using Astra.Clients.Editing;
using Astra.Clients.Library;
using Astra.Core;
using Astra.Core.Library;
using Astra.Data;

namespace Astra.Server.Api;

/// <summary>
/// The admin side of the rest of the resource library (instructions, sub-agents, commands, hooks, environment variables, projects):
/// maps <see cref="ResourceLibrary"/>'s results to DTOs. Writing client files is local-origin only; the endpoints check that.
/// </summary>
public sealed class ResourceLibraryService(AstraDatabase db, ClientEnvironment env, AstraPaths paths)
{
    private readonly ResourceLibrary _lib = new(env, paths, db.LibraryResources, db.ClientConfigState,
        new ClientConfigApplier(db.ClientConfigState, env, paths.ClientBackupsDir));

    /// <summary>The resource kind a URL segment names.</summary>
    public static string? KindOf(string segment) => segment switch
    {
        "instructions" => ResourceKinds.Instruction,
        "agents" => ResourceKinds.Agent,
        "commands" => ResourceKinds.Command,
        "hooks" => ResourceKinds.Hook,
        "env" => ResourceKinds.Env,
        _ => null,
    };

    private static async Task<T> GuardAsync<T>(Func<Task<T>> run)
    {
        try
        {
            return await run();
        }
        catch (ResourceLibraryException e)
        {
            throw new AdminApiException(e.Failure switch { ResourceFailure.NotFound => 404, ResourceFailure.Conflict => 409, _ => 400 }, e.Message);
        }
        catch (EditorException e)
        {
            throw new AdminApiException(409, e.Message);
        }
    }

    private ResourceItemDto ToDto(ResourceItemInfo info)
    {
        var extra = ResourceText.Extra(info.Item.ExtraJson);
        var errors = new Dictionary<string, string>();
        foreach (var t in info.Targets)
            if (_lib.LastError(info.Item.Id, t.Client, t.Project) is { } e) errors[t.Project.Length > 0 ? t.Client + "@" + t.Project : t.Client] = e;
        return new ResourceItemDto(info.Item.Id, info.Item.Kind, info.Item.Name, info.Item.Description, info.Item.Body,
            ResourceText.ExtraString(extra, "event"), ResourceText.ExtraString(extra, "matcher"), ResourceText.ExtraInt(extra, "timeout"),
            ResourceText.ExtraString(extra, "model"), info.Item.Enabled,
            [.. info.Targets.Select(t => new ResourceTargetStateDto(t.Client, t.Project, t.State, t.Path, t.Message))], info.Item.UpdatedAt,
            errors.Count == 0 ? null : errors);
    }

    public Task<ResourceLibraryDto> StateAsync(string kind, bool canWrite, CancellationToken ct) => GuardAsync(async () =>
    {
        var s = await _lib.StateAsync(kind, ct);
        return new ResourceLibraryDto(kind, [.. s.Clients.Select(c => new ResourceClientDto(c.Kind, c.Detected, c.Path, c.Project))],
            [.. s.Items.Select(ToDto)], [.. s.Projects.Select(p => new ResourceProjectDto(p.Path, p.Name, p.Exists))], canWrite);
    });

    private static ResourceInput ToInput(string kind, ResourceInputDto d, LibraryItemRecord? existing)
    {
        var extra = existing is null ? new System.Text.Json.Nodes.JsonObject() : ResourceText.Extra(existing.ExtraJson);
        return new(
            kind, d.Name?.Trim() ?? "", d.Description?.Trim() ?? existing?.Description ?? "", d.Body ?? existing?.Body ?? "",
            d.Event ?? ResourceText.ExtraString(extra, "event"), d.Matcher ?? ResourceText.ExtraString(extra, "matcher"),
            d.Timeout ?? ResourceText.ExtraInt(extra, "timeout"), d.Model ?? ResourceText.ExtraString(extra, "model"),
            (d.Targets?.Select(t => new LibraryTarget(t.Client, t.Project)) ?? (existing is null ? [] : ResourceText.ParseTargets(existing.TargetsJson)))
                .Select(t => new LibraryTarget(t.Client, NormalizeProject(t.Project))).ToList(),
            d.Enabled ?? existing?.Enabled ?? true);
    }

    private static string NormalizeProject(string project) =>
        project.Length == 0 || !Path.IsPathRooted(project) ? project : Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar);

    public Task<ResourceItemDto> CreateAsync(string kind, ResourceInputDto input, bool force, CancellationToken ct) =>
        GuardAsync(async () => ToDto(await _lib.SaveAsync(null, ToInput(kind, input, null), force, ct)));

    public Task<ResourceItemDto> UpdateAsync(string kind, string id, ResourceInputDto input, bool force, CancellationToken ct) => GuardAsync(async () =>
    {
        var item = await db.LibraryResources.GetItemAsync(id, ct);
        if (item is null || item.Kind != kind) throw new AdminApiException(404, "No such item");
        return ToDto(await _lib.SaveAsync(id, ToInput(kind, input with { Name = string.IsNullOrWhiteSpace(input.Name) ? item.Name : input.Name }, item), force, ct));
    });

    public Task<ResourceItemDto> ApplyAsync(string kind, string id, bool force, CancellationToken ct) => GuardAsync(async () =>
    {
        var item = await db.LibraryResources.GetItemAsync(id, ct);
        if (item is null || item.Kind != kind) throw new AdminApiException(404, "No such item");
        return ToDto(await _lib.SyncAsync(id, force, ct));
    });

    public Task DeleteAsync(string kind, string id, bool force, CancellationToken ct) => GuardAsync(async () =>
    {
        var item = await db.LibraryResources.GetItemAsync(id, ct);
        if (item is null || item.Kind != kind) throw new AdminApiException(404, "No such item");
        await _lib.DeleteAsync(id, force, ct);
        return true;
    });

    public Task<List<ResourceProjectDto>> ProjectsAsync(CancellationToken ct) => GuardAsync(async () =>
        (await db.LibraryResources.ListProjectsAsync(ct)).Select(p => new ResourceProjectDto(p.Path, p.Name, Directory.Exists(p.Path))).ToList());

    public Task<ResourceProjectDto> AddProjectAsync(ProjectInputDto input, CancellationToken ct) => GuardAsync(async () =>
    {
        var p = await _lib.AddProjectAsync(input.Path, input.Name, ct);
        return new ResourceProjectDto(p.Path, p.Name, p.Exists);
    });

    public Task RemoveProjectAsync(string path, CancellationToken ct) => GuardAsync(async () =>
    {
        // a Skill placement or an own MCP server may still use the project: those are listed in the ledger / the server rows
        if ((await db.Library.ListPlacementsAsync(ct)).Any(p => p.Project == path))
            throw new AdminApiException(409, "A Skill of Astra's is still placed in this project; switch it off first");
        if ((await db.McpLocal.ListAsync(ct)).Any(s => (System.Text.Json.JsonSerializer.Deserialize(s.ProjectsJson, Astra.Api.Contracts.ContractsJsonContext.Default.ListString) ?? []).Contains(path)))
            throw new AdminApiException(409, "An MCP server still lists this project; remove it there first");
        await _lib.RemoveProjectAsync(path, ct);
        return true;
    });
}
