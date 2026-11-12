using System.Collections.Concurrent;
using Jaunt;

namespace Astra.Plugins.Js;

/// <summary>
/// Loads a plugin's ES module and the modules it imports, the way magpie's jsmod does: there is no
/// node_modules for in-process plugins, so an import must be a relative file ("./x.js", "../x.js")
/// inside the plugin's root, and a package has to be bundled into the module. Each file is compiled
/// once per <see cref="JsModuleLoader"/>; Jaunt's bytecode is immutable and shared, so every engine of a
/// pool that loads it instantiates the same compile.
/// </summary>
internal sealed class JsModuleLoader : IJsModuleResolver
{
    private readonly string _root;
    private readonly string _entry;
    private readonly string _what;
    private readonly ConcurrentDictionary<string, JsModule> _compiled = new(PathComparer);
    private readonly IReadOnlyDictionary<string, (string Id, JsModule Module)>? _virtual;

    /// <param name="root">The folder imports must stay inside: the plugin's package folder, or the
    /// entry file's own folder for a single-file plugin.</param>
    /// <param name="entry">The module the plugin names (package.json magpie.middleware / magpie.agent).</param>
    /// <param name="what">How the error messages name the kind of module, e.g. "a middleware".</param>
    /// <param name="virtualModules">Bare specifiers the host provides itself (v2's <c>astra</c>): specifier → (module id, compiled module).</param>
    public JsModuleLoader(string root, string entry, string what, IReadOnlyDictionary<string, (string Id, JsModule Module)>? virtualModules = null)
    {
        _virtual = virtualModules;
        _root = Path.GetFullPath(root);
        _entry = Path.GetFullPath(entry);
        _what = what;
    }

    /// <summary>The entry module's absolute path, its module id.</summary>
    public string Entry => _entry;

    /// <summary>The entry module, compiled (once).</summary>
    public JsModule CompileEntry()
    {
        if (!Inside(_entry) || !Inside(FinalTarget(_entry)))
            throw new JsLoadException($"\"{_entry}\" is outside the plugin");
        return Compile(_entry);
    }

    /// <summary>An import of a loaded module: <paramref name="referrerId"/> is the importing file's path.</summary>
    public JsModuleSource? Resolve(string specifier, string? referrerId)
    {
        if (_virtual is not null && _virtual.TryGetValue(specifier, out var provided)) return new JsModuleSource(provided.Id, provided.Module);
        if (!specifier.StartsWith("./", StringComparison.Ordinal) && !specifier.StartsWith("../", StringComparison.Ordinal))
            throw new JsLoadException($"\"{specifier}\": {_what} imports only files beside it; bundle packages into it");
        var from = (referrerId is null ? null : Path.GetDirectoryName(referrerId)) ?? _root;
        var path = Path.GetFullPath(Path.Combine(from, specifier.Replace('/', Path.DirectorySeparatorChar)));
        if (!Inside(path) || !Inside(FinalTarget(path)))
            throw new JsLoadException($"\"{specifier}\" is outside the plugin");
        return new JsModuleSource(path, Compile(path));
    }

    private JsModule Compile(string path) =>
        _compiled.GetOrAdd(path, static key => JsCompiler.CompileModule(File.ReadAllText(key), new JsCompileOptions { SourceName = key }));

    private bool Inside(string path)
    {
        var rel = Path.GetRelativePath(_root, path);
        return rel != ".." && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(rel);
    }

    // A symlink inside the plugin that points out of it is outside too.
    private static string FinalTarget(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (IOException)
        {
            return path;
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

/// <summary>A plugin module that could not be resolved or loaded; its message is shown as the load error.</summary>
internal sealed class JsLoadException(string message) : Exception(message);
