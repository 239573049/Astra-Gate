using Astra.Api.Contracts;

namespace Astra.Desktop.Core.SmartRouting;

/// <summary>The wire names of the judge's dialects and the routing modes (web <c>JudgeProtocol</c>, <c>RoutingMode</c>).</summary>
public static class RoutingNames
{
    /// <summary>The judge dialect of the typesafe jev service.</summary>
    public const string Typesafe = "typesafe";

    /// <summary>The OpenAI-style dialect (a draft).</summary>
    public const string OpenAi = "openai";

    /// <summary>Judge once per session.</summary>
    public const string Session = "session";

    /// <summary>Judge every new question.</summary>
    public const string Turn = "turn";
}

/// <summary>Hosts that serve a judge dialect under their own address: choosing one fills in the address and the model (web <c>Preset</c>).</summary>
/// <param name="Id">The id; <c>smartRouting.preset.&lt;id&gt;</c> names it.</param>
/// <param name="BaseUrl">The address; empty for the dialect's own.</param>
/// <param name="Model">The model it starts with, null to keep the current one.</param>
public sealed record JudgePreset(string Id, string BaseUrl, string? Model = null);

/// <summary>The presets of each dialect and the helpers around an address.</summary>
public static class JudgePresets
{
    /// <summary>The id of "my own address".</summary>
    public const string Custom = "custom";

    private static readonly JudgePreset[] TypesafePresets =
    [
        new("typesafe", "", "jev-latest"),
        new("openrouter", "https://openrouter.ai/api", "~typesafe/jev-latest"),
        new("vercel", "https://ai-gateway.vercel.sh/typesafe", "typesafe-ai/jev"),
    ];

    private static readonly JudgePreset[] OpenAiPresets = [new("openai", "")];

    /// <summary>The presets of a dialect.</summary>
    public static IReadOnlyList<JudgePreset> For(string protocol) => protocol == RoutingNames.OpenAi ? OpenAiPresets : TypesafePresets;

    /// <summary>The preset whose address <paramref name="baseUrl"/> is (trailing slashes ignored), else <see cref="Custom"/> (web <c>presetOf</c>).</summary>
    public static string PresetOf(string protocol, string baseUrl)
    {
        var url = baseUrl.Trim().TrimEnd('/');
        return For(protocol).FirstOrDefault(p => p.BaseUrl == url)?.Id ?? Custom;
    }

    /// <summary>
    /// Scheme, host and port of the address the judge is reached at: what decides who receives its key (web <c>originOf</c>). The
    /// dialect's default address stands in for an empty one; text that is no address is returned as it is.
    /// </summary>
    public static string OriginOf(string baseUrl, string protocol, IEnumerable<JudgeProtocolDto> protocols)
    {
        var url = baseUrl.Trim();
        if (url.Length == 0) url = protocols.FirstOrDefault(p => p.Id == protocol)?.DefaultBaseUrl ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        if (u.Scheme is not ("http" or "https")) return "null";
        var host = u.HostNameType == UriHostNameType.IPv6 ? u.Host : u.IdnHost;
        return $"{u.Scheme}://{host}{(u.IsDefaultPort ? "" : ":" + u.Port)}".ToLowerInvariant();
    }

    /// <summary>A grade's choice in the model picker: one model on one provider (neither id holds a line break).</summary>
    public static string ChoiceOf(string? providerId, string model) => $"{providerId ?? ""}\n{model}";
}

/// <summary>One grade as the form edits it (web <c>SmartRoutingGrade</c>).</summary>
public sealed class GradeDraft
{
    /// <summary>The grade's id (<c>grade-n</c>).</summary>
    public string Id { get; set; } = "";

    /// <summary>The name shown; empty falls back to the id.</summary>
    public string Name { get; set; } = "";

    /// <summary>What the judge reads to choose this grade.</summary>
    public string Description { get; set; } = "";

    /// <summary>The model requests of this grade go to.</summary>
    public string Model { get; set; } = "";

    /// <summary>The provider that model was picked from; null for the first usable one.</summary>
    public string? ProviderId { get; set; }

    /// <summary>The name, else the id.</summary>
    public string DisplayName => Name.Length > 0 ? Name : Id;

    /// <summary>A copy.</summary>
    public GradeDraft Clone() => new() { Id = Id, Name = Name, Description = Description, Model = Model, ProviderId = ProviderId };

    /// <summary>All fields equal.</summary>
    public bool SameAs(GradeDraft o) => Id == o.Id && Name == o.Name && Description == o.Description && Model == o.Model && ProviderId == o.ProviderId;

    /// <summary>The update the server takes.</summary>
    public SmartRoutingGradeUpdate ToUpdate() => new(Id, Name, Description, Model, ProviderId);
}

/// <summary>
/// The page's form: what is on screen before it is saved (web <c>Draft</c>). The numbers are null while their field is being
/// retyped; <see cref="IsComplete"/> says when none is.
/// </summary>
public sealed class SmartRoutingDraft
{
    /// <summary>Fewest grades.</summary>
    public const int MinGrades = 2;

    /// <summary>Most grades.</summary>
    public const int MaxGrades = 6;

    /// <summary>The virtual model's name clients ask for.</summary>
    public string VirtualModel { get; set; } = "";

    /// <summary>The judge's dialect.</summary>
    public string Protocol { get; set; } = RoutingNames.Typesafe;

    /// <summary>The judge's address, empty for the dialect's own.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Write-only: empty means "keep the saved key".</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>The judge model.</summary>
    public string Model { get; set; } = "";

    /// <summary>The judge's question; empty uses the default.</summary>
    public string Instructions { get; set; } = "";

    /// <summary>How long a request waits for the judge.</summary>
    public int? TimeoutMs { get; set; }

    /// <summary>The grades, in order.</summary>
    public List<GradeDraft> Grades { get; set; } = [];

    /// <summary>session or turn.</summary>
    public string Mode { get; set; } = RoutingNames.Session;

    /// <summary>How long a session keeps its grade.</summary>
    public int? SessionTtlMinutes { get; set; }

    /// <summary>The grade used when the judge is unsure or fails.</summary>
    public string FallbackGrade { get; set; } = "";

    /// <summary>The confidence the judge must reach.</summary>
    public double? MinConfidence { get; set; }

    /// <summary>How many earlier turns the judge reads.</summary>
    public int? ContextTurns { get; set; }

    /// <summary>How many characters the judge reads.</summary>
    public int? MaxChars { get; set; }

    /// <summary>The form as saved (web <c>draftOf</c>).</summary>
    public static SmartRoutingDraft Of(SmartRoutingDto s) => new()
    {
        VirtualModel = s.VirtualModel,
        Protocol = s.Judge.Protocol,
        BaseUrl = s.Judge.BaseUrl ?? "",
        ApiKey = "",
        Model = s.Judge.Model,
        Instructions = s.Judge.Instructions ?? "",
        TimeoutMs = s.Judge.TimeoutMs,
        Grades = s.Grades.Select(g => new GradeDraft { Id = g.Id, Name = g.Name, Description = g.Description, Model = g.Model, ProviderId = g.ProviderId }).ToList(),
        Mode = s.Rules.Mode,
        SessionTtlMinutes = s.Rules.SessionTtlMinutes,
        FallbackGrade = s.Rules.FallbackGrade,
        MinConfidence = s.Rules.MinConfidence,
        ContextTurns = s.Rules.ContextTurns,
        MaxChars = s.Rules.MaxChars,
    };

    /// <summary>A deep copy.</summary>
    public SmartRoutingDraft Clone() => new()
    {
        VirtualModel = VirtualModel, Protocol = Protocol, BaseUrl = BaseUrl, ApiKey = ApiKey, Model = Model, Instructions = Instructions,
        TimeoutMs = TimeoutMs, Grades = Grades.Select(g => g.Clone()).ToList(), Mode = Mode, SessionTtlMinutes = SessionTtlMinutes,
        FallbackGrade = FallbackGrade, MinConfidence = MinConfidence, ContextTurns = ContextTurns, MaxChars = MaxChars,
    };

    /// <summary>Every field equal (web: <c>JSON.stringify(draft) !== JSON.stringify(draftOf(saved))</c> negated).</summary>
    public bool SameAs(SmartRoutingDraft o) =>
        VirtualModel == o.VirtualModel && Protocol == o.Protocol && BaseUrl == o.BaseUrl && ApiKey == o.ApiKey && Model == o.Model
        && Instructions == o.Instructions && TimeoutMs == o.TimeoutMs && Mode == o.Mode && SessionTtlMinutes == o.SessionTtlMinutes
        && FallbackGrade == o.FallbackGrade && MinConfidence == o.MinConfidence && ContextTurns == o.ContextTurns && MaxChars == o.MaxChars
        && Grades.Count == o.Grades.Count && Grades.Zip(o.Grades).All(p => p.First.SameAs(p.Second));

    /// <summary>No number field is empty (web <c>filled</c>).</summary>
    public bool IsComplete => TimeoutMs is not null && SessionTtlMinutes is not null && MinConfidence is not null && ContextTurns is not null && MaxChars is not null;

    /// <summary>The judge part of an update or a test (web <c>judgeOf</c>): the key only when one was typed, the timeout only when filled.</summary>
    public SmartRoutingJudgeUpdate ToJudge() => new(
        Protocol, BaseUrl, ApiKey.Length > 0 ? ApiKey : null, Model, TimeoutMs, Instructions);

    /// <summary>The whole update (web <c>updateOf</c>), with the switch when it is being flipped.</summary>
    public SmartRoutingUpdate ToUpdate(bool? enabled = null) => new(
        enabled, VirtualModel, ToJudge(), Grades.Select(g => g.ToUpdate()).ToList(),
        new SmartRoutingRulesUpdate(Mode, SessionTtlMinutes, FallbackGrade, MinConfidence, ContextTurns, MaxChars));

    /// <summary>The grades as a test request sends them.</summary>
    public List<SmartRoutingGradeUpdate> GradeUpdates() => Grades.Select(g => g.ToUpdate()).ToList();

    // ---- edits ----

    /// <summary>
    /// Replaces the grades; the fallback grade stays when it still exists, else the middle one (web <c>setGrades</c>).
    /// </summary>
    public void SetGrades(List<GradeDraft> next, string? fallbackGrade = null)
    {
        var fallback = fallbackGrade ?? FallbackGrade;
        Grades = next;
        FallbackGrade = next.Any(g => g.Id == fallback) ? fallback : (next.Count > 0 ? next[next.Count / 2].Id : "");
    }

    /// <summary>Changes grade <paramref name="index"/> (web <c>edit</c>).</summary>
    public void EditGrade(int index, Action<GradeDraft> change)
    {
        var next = Grades.Select(g => g.Clone()).ToList();
        change(next[index]);
        SetGrades(next);
    }

    /// <summary>Swaps grade <paramref name="index"/> with its neighbour (web <c>move</c>).</summary>
    public void MoveGrade(int index, int by)
    {
        var to = index + by;
        if (index < 0 || index >= Grades.Count || to < 0 || to >= Grades.Count) return;
        var next = Grades.Select(g => g.Clone()).ToList();
        (next[index], next[to]) = (next[to], next[index]);
        SetGrades(next);
    }

    /// <summary>Removes a grade, keeping at least <see cref="MinGrades"/>.</summary>
    public void RemoveGrade(int index)
    {
        if (Grades.Count <= MinGrades || index < 0 || index >= Grades.Count) return;
        SetGrades(Grades.Where((_, at) => at != index).Select(g => g.Clone()).ToList());
    }

    /// <summary>Adds an empty grade with the first free <c>grade-n</c> id (web <c>add</c>); no more than <see cref="MaxGrades"/>.</summary>
    public void AddGrade()
    {
        if (Grades.Count >= MaxGrades) return;
        var n = Grades.Count + 1;
        while (Grades.Any(g => g.Id == $"grade-{n}")) n++;
        var next = Grades.Select(g => g.Clone()).ToList();
        next.Add(new GradeDraft { Id = $"grade-{n}" });
        SetGrades(next);
    }

    /// <summary>Another dialect starts from its own address and model, and from no key — the same as the server does (web <c>changeProtocol</c>).</summary>
    public void ChangeProtocol(string protocol, IEnumerable<JudgeProtocolDto> protocols)
    {
        Protocol = protocol;
        BaseUrl = "";
        ApiKey = "";
        Model = protocols.FirstOrDefault(p => p.Id == protocol)?.DefaultModel ?? "";
    }

    /// <summary>Picks a service of the dialect: fills in its address (and model); "custom" keeps the address, starting one with https:// (web <c>choosePreset</c>).</summary>
    public void ChoosePreset(string id)
    {
        if (id == JudgePresets.Custom)
        {
            if (BaseUrl.Length == 0) BaseUrl = "https://";
            return;
        }
        var preset = JudgePresets.For(Protocol).FirstOrDefault(p => p.Id == id);
        if (preset is null) return;
        BaseUrl = preset.BaseUrl;
        if (preset.Model is not null) Model = preset.Model;
    }

    /// <summary>Picks a model for grade <paramref name="index"/> from a model choice (<see cref="JudgePresets.ChoiceOf"/>).</summary>
    public void ChooseModel(int index, string choice)
    {
        var at = choice.IndexOf('\n', StringComparison.Ordinal);
        var provider = at > 0 ? choice[..at] : "";
        var model = choice[(at + 1)..];
        EditGrade(index, g =>
        {
            g.ProviderId = provider.Length > 0 ? provider : null;
            g.Model = model;
        });
    }
}
