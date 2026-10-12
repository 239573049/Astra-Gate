using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Astra.Core;
using Astra.Plugins.Packages;
using Astra.Plugins.Runtime;

namespace Astra.Plugins;

/// <summary>
/// Source-generated JSON metadata for the types this assembly hands to the admin API. Registered into
/// <see cref="JsonContexts"/> on module load (Native AOT / trimming: an unregistered type throws there).
/// </summary>
[JsonSerializable(typeof(PluginJob))]
[JsonSerializable(typeof(BunStatus))]
[JsonSerializable(typeof(Middleware.MiddlewareState))]
[JsonSerializable(typeof(Host.PluginHostStatus))]
[JsonSerializable(typeof(Host.PluginHostMessage))]
[JsonSerializable(typeof(Market.MarketListing))]
[JsonSerializable(typeof(List<Market.MarketListing>))]
[JsonSerializable(typeof(Market.MarketTarball))]
[JsonSerializable(typeof(List<Market.MarketTarball>))]
[JsonSerializable(typeof(Market.MarketSearchResult))]
[JsonSerializable(typeof(List<Market.MarketSearchResult>))]
[JsonSerializable(typeof(List<string>))]
internal partial class PluginsJsonContext : JsonSerializerContext
{
    // CA2255 warns about module initializers in libraries. Registration here is deliberate and is the
    // point of the type: Core cannot reference this assembly, so the context announces itself instead.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage", "CA2255:The ModuleInitializer attribute should not be used in libraries",
        Justification = "Self-registration into JsonContexts is the intended contract; Core must not reference this assembly.")]
    [ModuleInitializer]
    internal static void Register() => JsonContexts.Add(Default);
}
