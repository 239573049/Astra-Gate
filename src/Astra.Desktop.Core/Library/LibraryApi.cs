using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.Library;

/// <summary>
/// The rest of the resource library over <c>/api/library/{instructions|agents|commands|hooks|env}</c> and <c>/api/library/projects</c> (the web's
/// <c>useResourceLibrary</c> and friends): one query per kind, polled while a page shows it, refreshed by every change. Writing client files is
/// refused off this machine by the server.
/// </summary>
public sealed class LibraryApi
{
    /// <summary>The URL segment of each kind, in display order.</summary>
    public static IReadOnlyList<string> Segments { get; } = ["instructions", "agents", "commands", "hooks", "env"];

    private readonly ApiClient _api;
    private readonly QueryClient _queries;
    private readonly Dictionary<string, Query<ResourceLibraryDto>> _byKind = [];

    /// <summary>Creates the facade over the shared client and cache.</summary>
    public LibraryApi(ApiClient api, QueryClient queries)
    {
        _api = api;
        _queries = queries;
        Projects = queries.Query<List<ResourceProjectDto>>("library-projects", async ct => await api.GetAsync<List<ResourceProjectDto>>("/api/library/projects", ct) ?? []);
    }

    /// <summary>['library-projects']: the project directories things can be placed into.</summary>
    public Query<List<ResourceProjectDto>> Projects { get; }

    private static string Key(string segment) => "library-resource:" + segment;

    /// <summary>The query of one kind (polled every 20 s while a page shows it).</summary>
    public Query<ResourceLibraryDto> For(string segment)
    {
        if (_byKind.TryGetValue(segment, out var have)) return have;
        var query = _queries.Query<ResourceLibraryDto>(Key(segment),
            async ct => await _api.GetAsync<ResourceLibraryDto>($"/api/library/{segment}", ct) ?? throw new ApiException(0, "empty response"),
            new QueryOptions<ResourceLibraryDto> { RefetchInterval = _ => TimeSpan.FromSeconds(20) });
        _byKind[segment] = query;
        return query;
    }

    /// <summary>POST (new) or PUT (<paramref name="id"/>): saves an item and writes it into its targets. <paramref name="force"/> overwrites what the user changed.</summary>
    public async Task<ResourceItemDto> SaveAsync(string segment, string? id, ResourceInputDto body, bool force = false, CancellationToken ct = default)
    {
        var suffix = force ? "?force=true" : "";
        var result = id is null
            ? await _api.PostAsync<ResourceInputDto, ResourceItemDto>($"/api/library/{segment}{suffix}", body, ct)
            : await _api.PutAsync<ResourceInputDto, ResourceItemDto>($"/api/library/{segment}/{Uri.EscapeDataString(id)}{suffix}", body, ct);
        Changed(segment);
        return result ?? throw new ApiException(0, "empty response");
    }

    /// <summary>POST …/{id}/apply: writes the item into its targets again.</summary>
    public async Task<ResourceItemDto> ApplyAsync(string segment, string id, bool force, CancellationToken ct = default)
    {
        var result = await _api.PostAsync<ResourceItemDto>($"/api/library/{segment}/{Uri.EscapeDataString(id)}/apply{(force ? "?force=true" : "")}", ct);
        Changed(segment);
        return result ?? throw new ApiException(0, "empty response");
    }

    /// <summary>DELETE …/{id}: takes what Astra wrote out of the clients (a changed one stays unless <paramref name="force"/>) and deletes the item.</summary>
    public async Task DeleteAsync(string segment, string id, bool force, CancellationToken ct = default)
    {
        await _api.DeleteAsync($"/api/library/{segment}/{Uri.EscapeDataString(id)}{(force ? "?force=true" : "")}", ct);
        Changed(segment);
    }

    /// <summary>POST /api/library/projects.</summary>
    public async Task<ResourceProjectDto> AddProjectAsync(string path, CancellationToken ct = default)
    {
        var result = await _api.PostAsync<ProjectInputDto, ResourceProjectDto>("/api/library/projects", new ProjectInputDto(path.Trim(), null), ct)
            ?? throw new ApiException(0, "empty response");
        ChangedProjects();
        return result;
    }

    /// <summary>DELETE /api/library/projects?path=: forgets a project (refused while something of Astra's is placed in it).</summary>
    public async Task RemoveProjectAsync(string path, CancellationToken ct = default)
    {
        await _api.DeleteAsync($"/api/library/projects?path={Uri.EscapeDataString(path)}", ct);
        ChangedProjects();
    }

    private void Changed(string segment) => _queries.Invalidate(Key(segment));

    private void ChangedProjects()
    {
        _queries.Invalidate("library-projects");
        foreach (var segment in Segments) _queries.Invalidate(Key(segment));
    }

    /// <summary>The badge tone of a target state: <c>applied</c> green, a changed / conflicting / removed one orange or red, the rest neutral.</summary>
    public static string ToneOf(string state) => state switch
    {
        "applied" => "green",
        "drifted" or "missing" or "project-missing" => "orange",
        "conflict" => "red",
        _ => "neutral",
    };
}

/// <summary>The fields of the add / edit sheet of a library item and what they become (web <c>ResourceSheet</c>).</summary>
public sealed class ResourceDraft
{
    /// <summary>The portable hook events, in display order.</summary>
    public static IReadOnlyList<string> HookEvents { get; } = ["turn-end", "after-tool", "before-tool", "prompt", "session-start"];

    /// <summary>Creates a draft for <paramref name="kind"/> (<c>instruction</c>, <c>agent</c>, <c>command</c>, <c>hook</c>, <c>env</c>); <paramref name="existing"/> null = a new item.</summary>
    public ResourceDraft(string kind, ResourceItemDto? existing = null)
    {
        Kind = kind;
        Existing = existing;
        if (existing is null) return;
        Name = existing.Name;
        Description = existing.Description;
        Body = existing.Body;
        Event = existing.Event ?? "turn-end";
        Matcher = existing.Matcher ?? "";
        Timeout = existing.Timeout;
        Model = existing.Model ?? "";
        Enabled = existing.Enabled;
        foreach (var t in existing.Targets) Targets.Add((t.Client, t.Project));
    }

    /// <summary>The kind of item.</summary>
    public string Kind { get; }

    /// <summary>The item being edited; null = a new one.</summary>
    public ResourceItemDto? Existing { get; }

    /// <summary>The name.</summary>
    public string Name { get; set; } = "";

    /// <summary>The one-line description (sub-agents, commands and hooks).</summary>
    public string Description { get; set; } = "";

    /// <summary>The text, prompt, command or value.</summary>
    public string Body { get; set; } = "";

    /// <summary>A hook's event.</summary>
    public string Event { get; set; } = "turn-end";

    /// <summary>A hook's matcher.</summary>
    public string Matcher { get; set; } = "";

    /// <summary>A hook's timeout in seconds; 0 is the client's own.</summary>
    public int Timeout { get; set; }

    /// <summary>A sub-agent's model (Codex's role file only).</summary>
    public string Model { get; set; } = "";

    /// <summary>Whether the item is written at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The chosen targets: a client and a project directory (empty = user-wide).</summary>
    public HashSet<(string Client, string Project)> Targets { get; } = [];

    /// <summary>Whether the fields hold something Astra can save.</summary>
    public bool IsValid => Name.Trim().Length > 0 && (Kind == "env" || Body.Trim().Length > 0) && (Kind != "agent" || Description.Trim().Length > 0);

    /// <summary>The create / update body.</summary>
    public ResourceInputDto ToBody()
    {
        var hook = Kind == "hook";
        var needsMatcher = hook && (Event.Contains(':') || Event is not ("turn-end" or "prompt"));
        return new ResourceInputDto(
            Name.Trim(), Kind is "agent" or "command" or "hook" ? Description.Trim() : null, Body, hook ? Event : null,
            hook ? (needsMatcher ? Matcher.Trim() : "") : null, hook ? Timeout : null, Kind == "agent" && Model.Trim().Length > 0 ? Model.Trim() : null, Enabled,
            [.. Targets.OrderBy(t => t.Client, StringComparer.Ordinal).ThenBy(t => t.Project, StringComparer.Ordinal).Select(t => new ResourceTargetDto(t.Client, t.Project))]);
    }
}
