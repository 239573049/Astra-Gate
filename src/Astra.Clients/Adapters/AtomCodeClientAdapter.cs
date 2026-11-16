using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// AtomCode (atomgit.com/atomgit_atomcode/atomcode, a Rust terminal agent, npm <c>@atomgit.com/atomcode</c>; Coexist) keeps its settings in
/// <c>~/.atomcode/config.toml</c> (<c>ATOMCODE_HOME</c> when absolute). A model is a <c>[models."&lt;key&gt;"]</c> table naming a
/// <c>[provider_accounts.&lt;account&gt;]</c> table, as its own CodingPlan sign-in writes since 5.0.7: the account holds the protocol, the
/// gateway's <c>/v1</c> and the key; the model table holds the model string, the context window (AtomCode compacts as it nears it), the
/// output limit, whether it sees images. Astra adds the account <c>astra</c> (<c>provider = "openai-compatible"</c>) and one model table
/// per bound model, keyed <c>astra-&lt;id&gt;</c> (a bare TOML key: the editor writes no quoted table names, so characters other than letters,
/// digits, <c>_</c> and <c>-</c> become <c>-</c> in the key; the real id stays in <c>model</c>), and points <c>default_provider</c> and
/// <c>default_model</c> (<c>default_model</c> wins when both are set) at the chosen one; the defaults the user had are recorded and put
/// back. A model the provider no longer offers is taken out again. Reasoning levels are not written (the catalog has no per-model list).
/// AtomCode reads its settings at start-up. Verified against magpie's wiring only.
/// </summary>
public sealed class AtomCodeClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : FileClientAdapter(env, store)
{
    private const string AccountTable = "provider_accounts.astra";

    private const string AliasPrefix = "astra-";

    /// <summary>The window AtomCode is told for a model whose context is unknown (it has to be told one).</summary>
    public const long DefaultContext = 128_000;

    private string Dir => Env.GetEnvironmentVariable("ATOMCODE_HOME") is { Length: > 0 } home && Path.IsPathRooted(home) ? home : Env.Combine(".atomcode");

    private string ConfigFile => Path.Combine(Dir, "config.toml");

    public override string Kind => ClientKinds.AtomCode;

    protected override string MarkerFile => ConfigFile;

    protected override string MarkerKey => AccountTable;

    protected override bool IsTableKeyPath(string keyPath) => keyPath == AccountTable || IsModelTable(keyPath);

    protected override IReadOnlyList<string> EnabledWarnings =>
        ["AtomCode reads its settings at start-up: restart open atomcode sessions to use this."];

    public override ClientDetection Detect()
    {
        var dir = Dir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("atomcode");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no atomcode executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ConfigFile;
        var changes = new List<ConfigChange>();
        changes.AddRange(TableChanges(file, AccountTable,
        [
            ("provider", Raw("openai-compatible")),
            ("base_url", Raw(Gateway(ctx))),
            ("api_key", Raw(ctx.LocalKey)),
        ]));

        var hints = ModelHints(ctx);
        var aliases = Aliases(AliasPrefix, ModelEntries(ctx).Select(e => e.Id));
        foreach (var (id, alias) in aliases)
        {
            var info = hints.GetValueOrDefault(id);
            var context = PositiveLong(info?["contextWindow"]) ?? DefaultContext;
            long? output = PositiveLong(info?["maxOutputTokens"]);
            // An output above the window AtomCode is told is cut to it.
            if (output is { } o && o > context) output = context;
            changes.AddRange(TableChanges(file, $"models.{alias}",
            [
                ("account", Raw(ProviderId)),
                ("model", Raw(id)),
                ("context_window", Num(context)),
                ("max_tokens", output is { } max ? Num(max) : null),
                ("supports_vision", BoolHint(info?["vision"]) == true ? "true" : "false"),
            ]));
        }

        // Models Astra registered earlier that the bound provider no longer offers are taken out again.
        foreach (var entry in Store.List(Kind).Where(e => e.FilePath == file && IsModelTable(e.KeyPath)).OrderBy(e => e.KeyPath, StringComparer.Ordinal).ToList())
        {
            if (aliases.ContainsValue(entry.KeyPath["models.".Length..])) continue;
            if (CurrentValue(file, ConfigFileFormat.Toml, entry.KeyPath, ConfigChangeKind.Table) is { } current)
                changes.Add(new ConfigChange(file, ConfigFileFormat.Toml, entry.KeyPath, current, null, ConfigChangeKind.Table));
        }

        if (!string.IsNullOrEmpty(ctx.Model) && aliases.TryGetValue(ctx.Model, out var selected))
        {
            changes.Add(TomlSet(file, "default_provider", Raw(selected)));
            changes.Add(TomlSet(file, "default_model", Raw(selected)));
        }
        return BuildPlan(changes);
    }

    private static bool IsModelTable(string keyPath) =>
        keyPath.StartsWith("models." + AliasPrefix, StringComparison.Ordinal) && keyPath.IndexOf('.', "models.".Length) < 0;
}
