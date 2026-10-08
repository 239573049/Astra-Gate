using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Astra.Core;
using Astra.Core.Models;
using Astra.Providers.Quota;

namespace Astra.Providers.Templates;

/// <summary>
/// Source-generated JSON metadata for the types this assembly serializes (provider templates and
/// their snapshots, built-in quota templates). Registered into <see cref="JsonContexts"/> on module load, so
/// <see cref="Astra.Core.Json.Api"/> can resolve them without reflection (Native AOT / trimming).
/// </summary>
[JsonSerializable(typeof(ProviderTemplate))]
[JsonSerializable(typeof(TemplateAuth))]
[JsonSerializable(typeof(TemplateVariant))]
[JsonSerializable(typeof(TemplateModelListEndpoint))]
[JsonSerializable(typeof(ProviderEndpoint))]
[JsonSerializable(typeof(QuotaTemplate))]
[JsonSerializable(typeof(QuotaRequestSpec))]
[JsonSerializable(typeof(QuotaParamSpec))]
[JsonSerializable(typeof(List<QuotaTemplate>))]
[JsonSerializable(typeof(IReadOnlyList<QuotaTemplate>))]
[JsonSerializable(typeof(List<ProviderTemplate>))]
[JsonSerializable(typeof(IReadOnlyList<ProviderTemplate>))]
internal partial class ProviderJsonContext : JsonSerializerContext
{
    // CA2255 warns about module initializers in libraries. Registration here is deliberate and is the
    // point of the type: Core cannot reference this assembly, so the context announces itself instead.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage", "CA2255:The ModuleInitializer attribute should not be used in libraries",
        Justification = "Self-registration into JsonContexts is the intended contract; Core must not reference this assembly.")]
    [ModuleInitializer]
    internal static void Register() => JsonContexts.Add(Default);
}
