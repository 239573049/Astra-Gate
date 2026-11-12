using System.Reflection;
using System.Text.Json.Nodes;
using Jaunt;

namespace Astra.Plugins.V2.Runtime;

/// <summary>The v2 runtime's embedded files: the sandbox globals, the boot script, the <c>astra</c> module and the UI component schema.</summary>
internal static class V2Assets
{
    // the boot runs after globals.js, which gets the same synchronous door and hands the boot its byte helpers
    private static readonly Lazy<string> BootText = new(() =>
        "(call, sync, done, context) => (" + Read("Astra.Plugins.V2.boot.js") + ")(call, sync, done, context, (" + Read("Astra.Plugins.V2.globals.js") + ")(sync))");
    private static readonly Lazy<JsModule> AstraModule = new(() =>
        JsCompiler.CompileModule(Read("Astra.Plugins.V2.astra.js"), new JsCompileOptions { SourceName = "astra:astra.js" }));
    private static readonly Lazy<string> ComponentsText = new(() => Read("Astra.Plugins.V2.components.json"));

    /// <summary>The boot function's source (an expression).</summary>
    public static string Boot => BootText.Value;

    /// <summary>The module id the plugin's <c>import … from 'astra'</c> resolves to.</summary>
    public const string AstraId = "astra:astra.js";

    /// <summary>The <c>astra</c> module, compiled once (Jaunt's bytecode is shared by every engine).</summary>
    public static JsModule Astra => AstraModule.Value;

    private static readonly Lazy<JsModule> VitestModule = new(() =>
        JsCompiler.CompileModule(Read("Astra.Plugins.V2.vitest.js"), new JsCompileOptions { SourceName = "astra:vitest.js" }));
    private static readonly Lazy<JsModule> TestingModule = new(() =>
        JsCompiler.CompileModule(Read("Astra.Plugins.V2.testing.js"), new JsCompileOptions { SourceName = "astra:testing.js" }));

    /// <summary>
    /// What a plugin's tests import under <c>astra plugin test</c> (06 §4): the plugin's own modules plus <c>vitest</c> and
    /// <c>@astragate/plugin-sdk/testing</c>, both running in the sandbox. Only a test run gets these.
    /// </summary>
    public static IReadOnlyDictionary<string, (string Id, JsModule Module)> TestModules =>
        new Dictionary<string, (string, JsModule)>
        {
            ["astra"] = (AstraId, Astra), ["astra/jsx-runtime"] = (AstraId, Astra),
            ["vitest"] = ("astra:vitest.js", VitestModule.Value),
            ["@astragate/plugin-sdk/testing"] = ("astra:testing.js", TestingModule.Value),
            ["@astragate/plugin-sdk/runtime"] = (AstraId, Astra),
        };

    /// <summary>The virtual modules a v2 plugin can import.</summary>
    public static IReadOnlyDictionary<string, (string Id, JsModule Module)> VirtualModules =>
        new Dictionary<string, (string, JsModule)> { ["astra"] = (AstraId, Astra), ["astra/jsx-runtime"] = (AstraId, Astra) };

    /// <summary>The UI 1.0 component schema (components.json).</summary>
    public static string ComponentsJson => ComponentsText.Value;

    public static JsonObject Components() => (JsonObject)JsonNode.Parse(ComponentsJson)!;

    /// <summary>The UI scene set (scenes.json, generated from tests/fixtures/plugin-ui): what the gallery shows.</summary>
    public static string ScenesJson => Read("Astra.Plugins.V2.scenes.json");

    private static string Read(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                      ?? throw new InvalidOperationException($"missing embedded resource {name}");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
