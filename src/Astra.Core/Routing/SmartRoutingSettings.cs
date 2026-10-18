using System.Text.Json.Serialization;

namespace Astra.Core.Routing;

/// <summary>
/// When the judge is consulted (plan §5.4): once per session — the first request pins a grade for the whole session —
/// or for every new user turn (requests that only answer tool calls reuse the turn's decision).
/// </summary>
public static class RoutingModes
{
    public const string Session = "session";
    public const string Turn = "turn";

    public static readonly IReadOnlyList<string> All = [Session, Turn];
}

/// <summary>
/// The wire dialects a routing judge can speak (plan §5.3). Every dialect asks one multiple-choice question — which
/// grade fits the request — of a "decision model": a classifier that answers with probabilities instead of text.
/// </summary>
public static class JudgeProtocols
{
    /// <summary>TypeSafe System One (Jev): <c>POST {base}/v1/systemone</c>. OpenRouter, Vercel AI Gateway and LiteLLM host the same protocol.</summary>
    public const string TypeSafe = "typesafe";

    /// <summary>OpenAI Decisions: <c>POST {base}/v1/decisions</c>.</summary>
    public const string OpenAI = "openai";

    public static readonly IReadOnlyList<string> All = [TypeSafe, OpenAI];

    public static string DefaultBaseUrl(string protocol) => protocol == OpenAI ? "https://api.openai.com" : "https://api.typesafe.ai";

    /// <summary>The model a new judge starts with; empty when the dialect has no stable default and the user must choose.</summary>
    public static string DefaultModel(string protocol) => protocol == OpenAI ? "" : "jev-latest";
}

/// <summary>
/// One rung of the ladder the judge chooses from: the words it reads to decide a request belongs here, and the model
/// requests of this grade go to. Grades are ordered cheapest first. (Named "grade", not "tier": a token pool member's
/// <c>Tier</c> already means its failover priority.)
/// </summary>
public sealed class RoutingGrade
{
    /// <summary>Stable id the judge answers with and rules refer to: 1–32 characters of a–z, 0–9, "_" and "-".</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>What the judge reads to decide a request belongs to this grade (sent as the option's description).</summary>
    public string Description { get; set; } = "";

    /// <summary>The model requests of this grade go to: an id some enabled provider serves. Empty until the user chooses one.</summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// The provider <see cref="Model"/> was chosen from, which the grade's requests go to — not the first of the caller's
    /// own providers that happens to serve the same id. Null for a grade saved before grades named one: the service
    /// fills in the first usable provider serving the model, the one the console showed next to it.
    /// </summary>
    public string? ProviderId { get; set; }
}

/// <summary>Where the decision model lives and how it is asked.</summary>
public sealed class RoutingJudgeSettings
{
    /// <summary>One of <see cref="JudgeProtocols.All"/>.</summary>
    public string Protocol { get; set; } = JudgeProtocols.TypeSafe;

    /// <summary>Service address without the path; null = the dialect's default. Hosted copies of a dialect (OpenRouter …) differ only here.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>The API key protected with ISecretProtector; the admin API only reports whether one is set.</summary>
    public string? ApiKeyProtected { get; set; }

    public string Model { get; set; } = JudgeProtocols.DefaultModel(JudgeProtocols.TypeSafe);

    /// <summary>Deadline of one judgement; a slower answer counts as a failure and the fallback grade is used.</summary>
    public int TimeoutMs { get; set; } = 2000;

    /// <summary>Replaces the built-in question; null = <see cref="SmartRoutingSettings.DefaultInstructions"/>.</summary>
    public string? Instructions { get; set; }

    [JsonIgnore]
    public string EffectiveBaseUrl => BaseUrl ?? JudgeProtocols.DefaultBaseUrl(Protocol);
}

/// <summary>How and when the judge's decision is made and kept.</summary>
public sealed class RoutingRules
{
    /// <summary>One of <see cref="RoutingModes.All"/>; the default is one judgement per session.</summary>
    public string Mode { get; set; } = RoutingModes.Session;

    /// <summary>How long an idle session keeps its grade (the lifetime renews on every request).</summary>
    public int SessionTtlMinutes { get; set; } = 60;

    /// <summary>The grade used when the judge fails, is not confident enough or has nothing to read. A fallback is never pinned.</summary>
    public string FallbackGrade { get; set; } = "standard";

    /// <summary>
    /// The least confidence an answer needs (0–1; the judge's concentration of probability, not a chance of being right).
    /// TypeSafe publishes no recommended value, only examples.
    /// </summary>
    public double MinConfidence { get; set; } = 0.3;

    /// <summary>How many earlier user requests go to the judge as context, besides the current one (0 = none).</summary>
    public int ContextTurns { get; set; } = 2;

    /// <summary>The most characters of conversation text sent to the judge in one question.</summary>
    public int MaxChars { get; set; } = 6000;
}

/// <summary>
/// Smart routing (plan §5): the gateway judges how demanding a request is and sends it to the model of the matching
/// grade. Persisted in <c>settings</c> under <see cref="StorageKey"/>; off until the user has configured a judge and a
/// model for every grade.
/// </summary>
public sealed class SmartRoutingSettings
{
    public const string StorageKey = "smart_routing";

    /// <summary>The model id clients ask for to get smart routing.</summary>
    public const string DefaultVirtualModel = "astra-auto";

    /// <summary>
    /// The built-in question. English on purpose: TypeSafe documents lower accuracy on non-English text, and the grade
    /// descriptions are read together with it.
    /// </summary>
    public const string DefaultInstructions =
        "Which grade is the cheapest one that can complete the user's current request well? "
        + "Judge the work the request needs, not its length or tone. Earlier requests and conversation facts are context only.";

    public bool Enabled { get; set; }

    /// <summary>The model id that selects smart routing; it must not collide with a real model an enabled provider serves.</summary>
    public string VirtualModel { get; set; } = DefaultVirtualModel;

    public RoutingJudgeSettings Judge { get; set; } = new();

    /// <summary>The grades, cheapest first (2–6).</summary>
    public List<RoutingGrade> Grades { get; set; } = DefaultGrades();

    public RoutingRules Rules { get; set; } = new();

    public SmartRoutingSettings Clone() => Json.Deserialize<SmartRoutingSettings>(Json.Serialize(this))!;

    /// <summary>Three grades with descriptions and no models yet (a model is the user's choice).</summary>
    public static List<RoutingGrade> DefaultGrades() =>
    [
        new()
        {
            Id = "light", Name = "Light",
            Description = "Simple, self-contained requests: direct questions, lookups, short rewrites or translations, formatting, trivial single-file edits, small talk.",
        },
        new()
        {
            Id = "standard", Name = "Standard",
            Description = "Everyday engineering and writing work: implementing a clearly specified change, debugging with a clear error, explaining code, well-bounded multi-step tasks.",
        },
        new()
        {
            Id = "heavy", Name = "Heavy",
            Description = "Hard or high-stakes work: architecture and design, ambiguous or cross-cutting refactors, debugging with no clear cause, security review, math or proofs, long multi-file planning.",
        },
    ];
}
