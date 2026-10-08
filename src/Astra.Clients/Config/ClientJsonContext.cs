using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Astra.Core;

namespace Astra.Clients.Config;

/// <summary>
/// Source-generated JSON metadata for the types this assembly serializes (client-config backup
/// manifests). Registered into <see cref="JsonContexts"/> on module load so
/// <see cref="Astra.Core.Json.Storage"/> can resolve them without reflection (Native AOT / trimming).
/// </summary>
[JsonSerializable(typeof(BackupManifest))]
[JsonSerializable(typeof(BackupFileEntry))]
internal partial class ClientJsonContext : JsonSerializerContext
{
    // CA2255 warns about module initializers in libraries. Registration here is deliberate and is the
    // point of the type: Core cannot reference this assembly, so the context announces itself instead.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage", "CA2255:The ModuleInitializer attribute should not be used in libraries",
        Justification = "Self-registration into JsonContexts is the intended contract; Core must not reference this assembly.")]
    [ModuleInitializer]
    internal static void Register() => JsonContexts.Add(Default);
}
