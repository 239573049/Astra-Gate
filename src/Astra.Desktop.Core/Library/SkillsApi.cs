using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Settings;

namespace Astra.Desktop.Core.Library;

/// <summary>How one client's chip on a Skill looks and what a click does (web <c>badgeLook</c>).</summary>
public enum SkillChipLook
{
    /// <summary>A link in the client's own Skills directory; a click switches it off.</summary>
    On,

    /// <summary>Visible through the shared <c>~/.agents/skills</c>; a click switches it off (after a confirmation when others read it too).</summary>
    Shared,

    /// <summary>Visible through another client's directory; switched off there, never here.</summary>
    Locked,

    /// <summary>A copy, because no link could be made.</summary>
    Copy,

    /// <summary>Not on; a click switches it on.</summary>
    Off,

    /// <summary>The client is not detected or not supported.</summary>
    Disabled,
}

/// <summary>
/// The resource library's Skills over the local server's <c>/api/library/skills</c> (plan resources-library; the web's <c>useSkillLibrary</c>
/// and friends): the library with each Skill's clients, what the client directories hold that Astra does not manage yet, and the commands.
/// Every change refreshes the one query, because it can change what any client sees.
/// </summary>
public sealed class SkillsApi
{
    private const string Key = "library-skills";
    private readonly ApiClient _api;
    private readonly QueryClient _queries;

    /// <summary>Creates the facade over the shared client and cache.</summary>
    public SkillsApi(ApiClient api, QueryClient queries)
    {
        _api = api;
        _queries = queries;
        Library = queries.Query<SkillLibraryDto>(Key, async ct => await api.GetAsync<SkillLibraryDto>("/api/library/skills", ct) ?? throw new ApiException(0, "empty response"),
            new QueryOptions<SkillLibraryDto> { RefetchInterval = _ => TimeSpan.FromSeconds(20) });
    }

    /// <summary>['library-skills']: the targets, the Skills, the Skills found in client directories (polled every 20 s while a page shows it).</summary>
    public Query<SkillLibraryDto> Library { get; }

    private static string Name(string name) => Uri.EscapeDataString(name);

    /// <summary>PUT …/{name}/clients/{client}: switches a client on or off. <c>NeedsConfirm</c> = nothing changed, repeat with <paramref name="confirm"/>.</summary>
    public async Task<SkillSwitchResultDto> SwitchAsync(string name, string client, bool on, bool confirm = false, CancellationToken ct = default)
    {
        var result = await _api.PutAsync<SkillSwitchDto, SkillSwitchResultDto>($"/api/library/skills/{Name(name)}/clients/{Uri.EscapeDataString(client)}",
            new SkillSwitchDto(on, confirm), ct) ?? throw new ApiException(0, "empty response");
        Changed();
        return result;
    }

    /// <summary>POST …/probe: looks into a Git repository or a folder for Skills (local requests only).</summary>
    public async Task<SkillProbeResultDto> ProbeAsync(string kind, string source, string? gitRef, CancellationToken ct = default)
        => await _api.PostAsync<SkillProbeRequestDto, SkillProbeResultDto>("/api/library/skills/probe",
            new SkillProbeRequestDto(kind, source.Trim(), string.IsNullOrWhiteSpace(gitRef) ? null : gitRef.Trim()), ct) ?? throw new ApiException(0, "empty response");

    /// <summary>POST …/install: installs the picked Skills of a probe and switches the clients on.</summary>
    public async Task<List<SkillOutcomeDto>> InstallAsync(string probeId, IEnumerable<string> names, IEnumerable<string> clients, CancellationToken ct = default)
    {
        var result = await _api.PostAsync<SkillInstallRequestDto, List<SkillOutcomeDto>>("/api/library/skills/install",
            new SkillInstallRequestDto(probeId, names.ToList(), clients.ToList()), ct) ?? [];
        Changed();
        return result;
    }

    /// <summary>POST …/migrate/preview: what migrating would do (nothing changes).</summary>
    public async Task<List<SkillMigratePreviewItemDto>> MigratePreviewAsync(IEnumerable<string>? names, CancellationToken ct = default)
        => await _api.PostAsync<SkillMigratePreviewRequestDto, List<SkillMigratePreviewItemDto>>("/api/library/skills/migrate/preview",
            new SkillMigratePreviewRequestDto(names?.ToList()), ct) ?? [];

    /// <summary>POST …/migrate: moves the Skills into the library and links them back in place.</summary>
    public async Task<List<SkillOutcomeDto>> MigrateAsync(IEnumerable<SkillMigrateItemDto> items, CancellationToken ct = default)
    {
        var result = await _api.PostAsync<SkillMigrateRequestDto, List<SkillOutcomeDto>>("/api/library/skills/migrate", new SkillMigrateRequestDto(items.ToList()), ct) ?? [];
        Changed();
        return result;
    }

    /// <summary>POST …/{name}/return or …/return-all (<paramref name="name"/> null): links become real folders.</summary>
    public async Task<List<SkillOutcomeDto>> ReturnAsync(string? name, CancellationToken ct = default)
    {
        var path = name is null ? "/api/library/skills/return-all" : $"/api/library/skills/{Name(name)}/return";
        var result = await _api.PostAsync<List<SkillOutcomeDto>>(path, ct) ?? [];
        Changed();
        return result;
    }

    /// <summary>DELETE …/{name}: removes Astra's links and the library folder (backed up first).</summary>
    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        await _api.DeleteAsync($"/api/library/skills/{Name(name)}", ct);
        Changed();
    }

    /// <summary>POST …/{name}/take-edits or …/overwrite-copy: settles a copy that was edited.</summary>
    public async Task CopyActionAsync(string name, bool takeEdits, CancellationToken ct = default)
    {
        await _api.PostAsync($"/api/library/skills/{Name(name)}/{(takeEdits ? "take-edits" : "overwrite-copy")}", ct);
        Changed();
    }

    private void Changed() => _queries.Invalidate(Key);

    /// <summary>How a client's chip looks, from what the server says it sees (<c>own</c> | <c>via-shared</c> | <c>via-&lt;kind&gt;</c> | <c>off</c>).</summary>
    public static SkillChipLook LookOf(SkillClientDto view, SkillTargetDto? target)
    {
        var usable = target is { Supported: true, Detected: true };
        if (view.State == "off") return usable ? SkillChipLook.Off : SkillChipLook.Disabled;
        if (view.How == "copy") return SkillChipLook.Copy;
        return view.State switch { "own" => SkillChipLook.On, "via-shared" => SkillChipLook.Shared, _ => SkillChipLook.Locked };
    }

    /// <summary>The text of a Skill's source: the repository and short commit, or the folder it came from.</summary>
    public static string SourceText(string? sourceJson)
    {
        if (string.IsNullOrEmpty(sourceJson)) return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(sourceJson);
            string? Get(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
            if (Get("url") is { Length: > 0 } url)
            {
                var shown = url.Replace("https://", "").Replace("http://", "");
                if (shown.EndsWith(".git", StringComparison.Ordinal)) shown = shown[..^4];
                return Get("commit") is { Length: >= 7 } commit ? $"{shown} · {commit[..7]}" : shown;
            }
            return Get("path") ?? Get("from") ?? "";
        }
        catch (System.Text.Json.JsonException)
        {
            return "";
        }
    }
}
