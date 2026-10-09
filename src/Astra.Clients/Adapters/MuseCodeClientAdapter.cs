using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Muse Code (Meta's terminal agent, native <c>muse</c> binary; Switch). Muse reads its endpoint only from the per-run
/// <c>--base-url</c> flag (accepted by <c>exec</c> and the interactive TUI, not by <c>resume</c>) and its key only from
/// <c>META_API_KEY</c>, so there is no config key to write. Astra therefore takes over one managed block of the user's
/// shell rc file (<c>~/.zshrc</c>, or <c>~/.bashrc</c> when only that exists) that defines a <c>muse</c> shell function:
/// <c>exec</c> and the TUI run with <c>--base-url &lt;gateway&gt;/v1</c>, <c>META_API_KEY</c> (this client's local gateway
/// key) and <c>MUSE_MODEL</c> (the selected model) set for that one invocation only. Other subcommands (<c>resume</c>,
/// <c>login</c>, …) run unchanged and keep using the user's Meta account. Disabling removes the block; the applier restores
/// the rest of the file like any other take-over.
/// </summary>
public sealed class MuseCodeClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    /// <summary>The block id, i.e. the key path inside the rc file.</summary>
    public const string BlockId = "muse-code";

    private const string BlockNote = "# Muse Code is routed through Astra. Turn the Muse Code client off in Astra to remove this block.";

    /// <summary>Subcommands that never talk to the model through the gateway (or do not accept <c>--base-url</c>).</summary>
    private const string PassThroughSubcommands = "resume|export|trace|skills|sandbox|session-message|auth|login|logout|init|config|help";

    /// <summary>The rc file: <c>~/.zshrc</c> (macOS default shell), or <c>~/.bashrc</c> when only that one exists.</summary>
    private string RcFile
    {
        get
        {
            var zshrc = Env.Combine(".zshrc");
            var bashrc = Env.Combine(".bashrc");
            return !Env.FileExists(zshrc) && Env.FileExists(bashrc) ? bashrc : zshrc;
        }
    }

    public override string Kind => ClientKinds.MuseCode;

    public override ClientMode Mode => ClientMode.Switch;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = Env.Combine(".config", "muse");
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("muse");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no muse executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [RcFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = RcFile;
        var before = CurrentValue(file, ConfigFileFormat.ShellBlock, BlockId);
        return BuildPlan([new ConfigChange(file, ConfigFileFormat.ShellBlock, BlockId, before, JsonString(BlockBody(ctx)))]);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var file = RcFile;
        var changes = new List<ConfigChange>();
        if (CurrentValue(file, ConfigFileFormat.ShellBlock, BlockId) is { } before)
            changes.Add(new ConfigChange(file, ConfigFileFormat.ShellBlock, BlockId, before, null));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var enabled = StillOurs(RcFile, ConfigFileFormat.ShellBlock, BlockId);
        return BuildStatus(detection, enabled, []);
    }

    /// <summary>
    /// The block body: a <c>muse</c> function. The key, model and gateway URL are passed per invocation (env prefix and
    /// <c>--base-url</c>), never exported, so the user's login is untouched by every other command.
    /// </summary>
    private static string BlockBody(EnableContext ctx)
    {
        // Muse's default-model path fails ("no visible models") against a gateway catalog, so always pin a model: the
        // selected one, else the first model the bound providers list.
        var model = string.IsNullOrEmpty(ctx.Model) ? ModelEntries(ctx).FirstOrDefault().Id : ctx.Model;
        var envPrefix = $"META_API_KEY={ShellQuote(ctx.LocalKey)}"
            + (string.IsNullOrEmpty(model) ? "" : $" MUSE_MODEL={ShellQuote(model)}");
        var baseUrl = ShellQuote(ctx.GatewayBaseUrl.TrimEnd('/') + "/v1");
        return string.Join('\n',
            BlockNote,
            "muse() {",
            "  case \"${1-}\" in",
            "    exec)",
            "      shift",
            $"      {envPrefix} command muse exec --base-url {baseUrl} \"$@\"",
            "      ;;",
            $"    {PassThroughSubcommands})",
            "      command muse \"$@\"",
            "      ;;",
            "    *)",
            $"      {envPrefix} command muse --base-url {baseUrl} \"$@\"",
            "      ;;",
            "  esac",
            "}");
    }

    /// <summary>POSIX single-quoting: wraps the value in single quotes and escapes embedded quotes.</summary>
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
