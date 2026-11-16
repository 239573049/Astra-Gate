using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// jcode (1jehuang/jcode, Coexist) keeps its settings in <c>config.toml</c> in <c>JCODE_HOME</c>, else <c>~/.jcode</c>. A provider of the
/// user's own is a named profile, <c>[providers.&lt;name&gt;]</c>: Astra adds <c>[providers.astra]</c> (<c>type = "openai-compatible"</c>,
/// <c>base_url</c> the gateway's <c>/v1</c>, a literal <c>api_key</c>, <c>requires_api_key = true</c> because jcode sends no key to a
/// loopback address otherwise, and the bound provider's models as one <c>models</c> array of inline tables with window, input kinds and
/// reasoning). When a model is chosen, <c>[provider] default_provider</c> and <c>default_model</c> point at it; what they were is
/// recorded and put back. Every other key, the user's own keys in Astra's profile included, is left alone. Verified against magpie's
/// wiring only; the models jcode itself writes as <c>[[providers.astra.models]]</c> tables are not merged into the array.
/// </summary>
public sealed class JcodeClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : FileClientAdapter(env, store)
{
    private const string ProfileTable = "providers.astra";

    private const string PickTable = "provider";

    /// <summary>The window jcode is told for a model whose context is unknown (it assumes 200k otherwise).</summary>
    public const long DefaultContext = 128_000;

    private string Dir => Env.GetEnvironmentVariable("JCODE_HOME") is { } home && home.Trim() is { Length: > 0 } trimmed ? trimmed : Env.Combine(".jcode");

    private string ConfigFile => Path.Combine(Dir, "config.toml");

    public override string Kind => ClientKinds.Jcode;

    protected override string MarkerFile => ConfigFile;

    protected override string MarkerKey => ProfileTable;

    protected override bool IsTableKeyPath(string keyPath) => keyPath is ProfileTable or PickTable;

    protected override IReadOnlyList<string> EnabledWarnings =>
        ["jcode reads its settings at start-up: restart open jcode sessions to use this."];

    public override ClientDetection Detect()
    {
        var dir = Dir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("jcode");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no jcode executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ConfigFile;
        var hints = ModelHints(ctx);
        var models = new List<string>();
        foreach (var (id, _) in ModelEntries(ctx))
        {
            var info = hints.GetValueOrDefault(id);
            var parts = new List<string> { "id = " + Raw(id), "context_window = " + Num(PositiveLong(info?["contextWindow"]) ?? DefaultContext) };
            parts.Add(BoolHint(info?["vision"]) == true ? "input = [\"text\", \"image\"]" : "input = [\"text\"]");
            if (BoolHint(info?["reasoning"]) == true) parts.Add("reasoning = true");
            models.Add("{ " + string.Join(", ", parts) + " }");
        }
        var changes = new List<ConfigChange>();
        changes.AddRange(TableChanges(file, ProfileTable,
        [
            ("type", Raw("openai-compatible")),
            ("base_url", Raw(Gateway(ctx))),
            ("api_key", Raw(ctx.LocalKey)),
            ("requires_api_key", "true"),
            ("default_model", string.IsNullOrEmpty(ctx.Model) ? null : Raw(ctx.Model)),
            ("models", "[" + string.Join(", ", models) + "]"),
        ]));
        // [provider] is a table of its own: made whole when absent (and then taken out whole), refreshed key by key when it is the user's.
        if (!string.IsNullOrEmpty(ctx.Model))
            changes.AddRange(TableChanges(file, PickTable, [("default_provider", Raw(ProviderId)), ("default_model", Raw(ctx.Model))]));
        return BuildPlan(changes);
    }
}
