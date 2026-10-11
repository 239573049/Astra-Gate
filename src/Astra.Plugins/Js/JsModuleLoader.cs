using System.Collections.Concurrent;
using Acornima.Ast;
using Jint;
using Jint.Runtime.Modules;
using Module = Jint.Runtime.Modules.Module;

namespace Astra.Plugins.Js;

/// <summary>
/// Loads a plugin's ES module and the modules it imports, the way magpie's jsmod does: there is no
/// node_modules for in-process plugins, so an import must be a relative file ("./x.js", "../x.js")
/// inside the plugin's root, and a package has to be bundled into the module. Each file is parsed
/// once per <see cref="JsModuleLoader"/>; every engine that loads it builds its own module record
/// from that parse, so a pool of engines shares one compile.
/// </summary>
internal sealed class JsModuleLoader : IModuleLoader
{
    private readonly string _root;
    private readonly string _entry;
    private readonly string _what;
    private readonly ConcurrentDictionary<string, Prepared<Acornima.Ast.Module>> _parsed = new(PathComparer);

    /// <param name="root">The folder imports must stay inside: the plugin's package folder, or the
    /// entry file's own folder for a single-file plugin.</param>
    /// <param name="entry">The module the plugin names (package.json magpie.middleware / magpie.agent).</param>
    /// <param name="what">How the error messages name the kind of module, e.g. "a middleware".</param>
    public JsModuleLoader(string root, string entry, string what)
    {
        _root = Path.GetFullPath(root);
        _entry = Path.GetFullPath(entry);
        _what = what;
    }

    /// <summary>The entry module's absolute path, the specifier <see cref="JsRuntime.Load"/> imports.</summary>
    public string Entry => _entry;

    public ResolvedSpecifier Resolve(string? referencingModuleLocation, ModuleRequest moduleRequest)
    {
        var spec = moduleRequest.Specifier;
        string path;
        if (referencingModuleLocation is null)
        {
            // the root import, made by JsRuntime.Load with the entry's own path
            path = Path.GetFullPath(spec);
        }
        else
        {
            if (!spec.StartsWith("./", StringComparison.Ordinal) && !spec.StartsWith("../", StringComparison.Ordinal))
                throw new JsLoadException($"\"{spec}\": {_what} imports only files beside it; bundle packages into it");
            var from = Path.GetDirectoryName(referencingModuleLocation) ?? _root;
            path = Path.GetFullPath(Path.Combine(from, spec.Replace('/', Path.DirectorySeparatorChar)));
        }
        if (!Inside(path) || !Inside(FinalTarget(path)))
            throw new JsLoadException($"\"{spec}\" is outside the plugin");
        return new ResolvedSpecifier(moduleRequest, path, new Uri(path), SpecifierType.RelativeOrAbsolute);
    }

    public Module LoadModule(Engine engine, ResolvedSpecifier resolved)
    {
        var prepared = _parsed.GetOrAdd(resolved.Key, static key =>
            Engine.PrepareModule(File.ReadAllText(key), key));
        return ModuleFactory.BuildSourceTextModule(engine, prepared);
    }

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
