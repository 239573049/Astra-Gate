using Tomlyn.Model;
using Tomlyn.Serialization;

namespace Astra.Clients.Editing;

/// <summary>
/// Source-generated TOML metadata for <see cref="TomlTable"/>, the untyped model behind TOML editing and
/// canonicalization (drift detection in the client-config restore path). Tomlyn's reflection-based
/// serializer is annotated <c>RequiresUnreferencedCode</c>/<c>RequiresDynamicCode</c>, so every
/// <c>TomlSerializer</c> call must resolve a <c>TomlTypeInfo</c> from this context instead. The generated
/// metadata produces byte-identical canonical output to the reflection path (probed with a NativeAOT
/// osx-arm64 publish), which is what keeps restore/drift decisions unchanged.
/// </summary>
[TomlSerializable(typeof(TomlTable))]
internal partial class ClientTomlContext : TomlSerializerContext
{
}
