using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Privacy;
using Astra.Server.Alerts;
using Astra.Server.Api;
using Astra.Server.Hosting;

namespace Astra.Server;

/// <summary>
/// Source-generated JSON metadata for everything the server serializes: the admin API DTOs and the
/// server-owned documents (<c>config.json</c>, <c>runtime.json</c>, settings blobs, the update feed).
/// Registered into <see cref="Astra.Core.JsonContexts"/> on module load so both
/// <see cref="Astra.Core.Json"/> and the ASP.NET HTTP JSON options resolve them without reflection
/// (Native AOT / trimming).
///
/// When you add a DTO that leaves the process — a new endpoint payload, a new persisted blob, a new
/// settings value — add it here; an unregistered type silently falls back to reflection, which works
/// today and fails under Native AOT.
/// </summary>
// server-owned documents
[JsonSerializable(typeof(ServerOptions))]
[JsonSerializable(typeof(RuntimeInfo))]
[JsonSerializable(typeof(UpdateManifest))]
[JsonSerializable(typeof(UpdateCheckState))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonNode))]
internal partial class ServerJsonContext : JsonSerializerContext
{
    // CA2255 warns about module initializers in libraries. Registration here is deliberate and is the
    // point of the type: Core cannot reference this assembly, so the context announces itself instead.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage", "CA2255:The ModuleInitializer attribute should not be used in libraries",
        Justification = "Self-registration into JsonContexts is the intended contract; Core must not reference this assembly.")]
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register()
    {
        // Astra.Api.Contracts registers its own context from its module initializer; make sure that module is
        // initialised before the first serialization, even one that touches no instance of its types.
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(Astra.Api.Contracts.TokenDto).Module.ModuleHandle);
        Astra.Core.JsonContexts.Add(Default);
    }
}
