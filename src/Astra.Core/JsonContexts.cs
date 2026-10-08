using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Astra.Core;

/// <summary>
/// Registry of source-generated <see cref="JsonSerializerContext"/>s backing <see cref="Json"/>.
///
/// A registered type is resolved from generated metadata — no reflection, no runtime code generation —
/// which is what keeps the type usable under Native AOT and trimming. Each assembly that owns JSON
/// types contributes its own context by calling <see cref="Add"/> from a <c>[ModuleInitializer]</c>;
/// the registry is the only Core-side hook, so Core never references the assemblies that feed it.
///
/// An unregistered type falls back to the reflection resolver <em>only where dynamic code is available</em>
/// (the JIT runtimes), so an incremental migration keeps working. That fallback does not exist in a
/// Native AOT build: there an unregistered type throws <see cref="NotSupportedException"/> naming itself,
/// and the AOT analyzer reports the call site. Either way the failure is loud. When you add a type that
/// crosses a JSON boundary, add it to the owning assembly's context.
/// </summary>
public static class JsonContexts
{
    private static readonly object Gate = new();
    private static readonly List<IJsonTypeInfoResolver> Parts = [];

    /// <summary>Types that had to go through the reflection fallback, i.e. were never registered.</summary>
    private static readonly HashSet<Type> Fallbacks = [];

    private static IJsonTypeInfoResolver? _reflection;

    /// <summary>The resolver handed to every <see cref="Json"/> option set.</summary>
    public static IJsonTypeInfoResolver Resolver { get; } = new Registry();

    static JsonContexts()
    {
        // Core's own types; other assemblies append themselves via [ModuleInitializer].
        Parts.Add(JsonContext.Default);
    }

    /// <summary>Appends a context to the registry. Safe to call from more than one thread.</summary>
    public static void Add(IJsonTypeInfoResolver context)
    {
        lock (Gate) Parts.Add(context);
    }

    /// <summary>
    /// Types resolved through reflection because no context registered them. Empty on a correct build;
    /// a test asserts this after exercising the API, which is what catches a DTO that was never added to
    /// its assembly's context (a mistake that is otherwise silent on the JIT and fatal under Native AOT).
    /// </summary>
    public static IReadOnlyList<Type> FallbackTypes
    {
        get { lock (Gate) return [.. Fallbacks.OrderBy(t => t.FullName, StringComparer.Ordinal)]; }
    }

    /// <summary>Forgets the types seen so far, so a test can assert against only what it exercised.</summary>
    public static void ResetFallbackTracking()
    {
        lock (Gate) Fallbacks.Clear();
    }

    /// <summary>
    /// Resolves the source-generated metadata for <typeparamref name="T"/> under
    /// <paramref name="options"/>. This is the non-reflective counterpart of the
    /// <c>JsonSerializer.Serialize&lt;T&gt;(value, options)</c> family, which is annotated
    /// <c>RequiresDynamicCode</c>/<c>RequiresUnreferencedCode</c>.
    /// </summary>
    public static JsonTypeInfo<T> Info<T>(JsonSerializerOptions options) =>
        (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T))!;

    /// <summary>
    /// The reflection resolver, or null where dynamic code is unavailable. The analyzer understands
    /// <see cref="RuntimeFeature.IsDynamicCodeSupported"/> and prunes this whole branch for Native AOT,
    /// which is why the fallback contributes no IL3050 warning; the remaining
    /// <c>RequiresUnreferencedCode</c> (trim) warning needs the explicit suppression below.
    /// </summary>
    [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2026:RequiresUnreferencedCode",
        Justification = "Reachable only when dynamic code is supported; the whole branch is pruned in Native AOT builds.")]
    private static IJsonTypeInfoResolver? ReflectionFallback() =>
        RuntimeFeature.IsDynamicCodeSupported ? new DefaultJsonTypeInfoResolver() : null;

    private sealed class Registry : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            lock (Gate)
            {
                foreach (var part in Parts)
                {
                    if (part.GetTypeInfo(type, options) is { } info) return info;
                }
                // Not migrated yet: keep the old reflection behavior on the JIT. Under Native AOT this
                // returns null and System.Text.Json throws NotSupportedException naming the type.
                var reflection = _reflection ??= ReflectionFallback();
                if (reflection is null) return null;
                Fallbacks.Add(type);
                return reflection.GetTypeInfo(type, options);
            }
        }
    }
}
