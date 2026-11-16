using System.Globalization;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Dim alpha (arcships/dim, npm <c>dimcode-alpha</c>, command <c>dima</c>; Switch). Dim documents a start-up path for any OpenAI-compatible
/// endpoint: <c>DIM_BASE_URL</c> (the gateway's <c>/v1</c>), <c>DIM_API_FORMAT=openai-compatible</c> (Dim's generic provider — not
/// <c>openai-chat</c>, which it takes for OpenAI's own), <c>DIM_API_KEY</c>, <c>DIM_MODEL</c> and, for a model it does not know,
/// <c>DIM_MAX_CONTEXT_TOKENS</c> / <c>DIM_MAX_OUTPUT_TOKENS</c> (it refuses to start one without both). Dim reads them for that one run
/// only, beside a running daemon, and leaves its saved Providers and account alone, so there is no config key to write. Astra therefore
/// takes over one managed block of the user's shell rc file (<c>~/.zshrc</c>, or <c>~/.bashrc</c> when only that exists) that defines a
/// <c>dima</c> function setting those variables for that invocation only; help, version and management commands ignore them. Dim's own
/// home (<c>DIMCODE_HOME</c>, else <c>~/.dimcode-alpha</c>) is never read or written. Disabling removes the block. A terminal that was
/// open before needs <c>source</c> on the rc file. Verified against magpie's wiring only.
/// </summary>
public sealed class DimClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    /// <summary>The block id, i.e. the key path inside the rc file.</summary>
    public const string BlockId = "dim";

    private const string BlockNote = "# Dim is routed through Astra. Turn the Dim client off in Astra to remove this block.";

    /// <summary>The limits Dim is given for a model whose window Astra does not know.</summary>
    private const long DefaultContext = 128_000;

    private const long DefaultOutput = 32_000;

    private string DimDir =>
        Env.GetEnvironmentVariable("DIMCODE_HOME")?.Trim() is { Length: > 0 } dir ? dir : Env.Combine(".dimcode-alpha");

    public override string Kind => ClientKinds.Dim;

    public override ClientMode Mode => ClientMode.Switch;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = DimDir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("dima");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no dima executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ShellRc.File(Env)];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ShellRc.File(Env);
        var before = CurrentValue(file, ConfigFileFormat.ShellBlock, BlockId);
        return BuildPlan([new ConfigChange(file, ConfigFileFormat.ShellBlock, BlockId, before, JsonString(BlockBody(ctx)))]);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var file = ShellRc.File(Env);
        var changes = new List<ConfigChange>();
        if (CurrentValue(file, ConfigFileFormat.ShellBlock, BlockId) is { } before)
            changes.Add(new ConfigChange(file, ConfigFileFormat.ShellBlock, BlockId, before, null));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var enabled = StillOurs(ShellRc.File(Env), ConfigFileFormat.ShellBlock, BlockId);
        return BuildStatus(detection, enabled, enabled ? ["Open a new terminal (or source your shell rc file) so the dima function is defined."] : []);
    }

    /// <summary>
    /// The block body: a <c>dima</c> function. The variables are set per invocation, never exported, so Dim's saved Providers and
    /// account stay in use for every run outside Astra's.
    /// </summary>
    private static string BlockBody(EnableContext ctx)
    {
        // Dim cannot pick a model from the gateway's catalog, so always pin one: the selected one, else the first the bound providers list.
        var model = string.IsNullOrEmpty(ctx.Model) ? ModelEntries(ctx).FirstOrDefault().Id : ctx.Model;
        var info = model is null ? null : ModelHints(ctx).GetValueOrDefault(model);
        var context = PositiveLong(info?["contextWindow"]) ?? DefaultContext;
        var output = PositiveLong(info?["maxOutputTokens"]) ?? Math.Min(DefaultOutput, context);
        if (output > context) output = Math.Min(DefaultOutput, context);
        var vars = new List<string>
        {
            $"DIM_BASE_URL={ShellRc.Quote(ctx.GatewayBaseUrl.TrimEnd('/') + "/v1")}",
            "DIM_API_FORMAT=openai-compatible",
            $"DIM_API_KEY={ShellRc.Quote(ctx.LocalKey)}",
        };
        if (!string.IsNullOrEmpty(model)) vars.Add($"DIM_MODEL={ShellRc.Quote(model)}");
        vars.Add($"DIM_MAX_CONTEXT_TOKENS={context.ToString(CultureInfo.InvariantCulture)}");
        vars.Add($"DIM_MAX_OUTPUT_TOKENS={output.ToString(CultureInfo.InvariantCulture)}");
        return string.Join('\n',
            BlockNote,
            "unalias dima 2>/dev/null || true",
            "dima() {",
            $"  {string.Join(' ', vars)} command dima \"$@\"",
            "}");
    }
}
