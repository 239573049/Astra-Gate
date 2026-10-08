using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Astra.Core.Billing;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Privacy;
using Astra.Core.Seed;

namespace Astra.Core;

/// <summary>
/// Source-generated metadata for the types <see cref="Astra.Core"/> serializes, plus the framework
/// and <see cref="JsonNode"/> types the repositories store. Registered into <see cref="JsonContexts"/>
/// by its static constructor; other assemblies append their own contexts.
///
/// Nesting is resolved by the generator, so only the root types of a document need an entry here —
/// but collections and dictionaries used as roots (a JSON column holding a list, say) are listed too.
/// </summary>
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(EffortBudgets))]
[JsonSerializable(typeof(PricingSchedule))]
[JsonSerializable(typeof(PriceSet))]
[JsonSerializable(typeof(ServiceTierRule))]
[JsonSerializable(typeof(ContextTier))]
[JsonSerializable(typeof(TimeWindowRule))]
[JsonSerializable(typeof(PrivacySettings))]
[JsonSerializable(typeof(PrivacyCustomRule))]
[JsonSerializable(typeof(PrivacyHit))]
[JsonSerializable(typeof(PrivacyReport))]
[JsonSerializable(typeof(SeedFile))]
[JsonSerializable(typeof(SeedModel))]
[JsonSerializable(typeof(SeedProviderPrice))]
[JsonSerializable(typeof(SystemModel))]
[JsonSerializable(typeof(ModelCapabilities))]
[JsonSerializable(typeof(ModelPrice))]
[JsonSerializable(typeof(ModelOverrides))]
[JsonSerializable(typeof(Provider))]
[JsonSerializable(typeof(ProviderEndpoint))]
[JsonSerializable(typeof(ProviderAccount))]
[JsonSerializable(typeof(ClientRecord))]
[JsonSerializable(typeof(ClientConfigStateEntry))]
[JsonSerializable(typeof(BillingTraceStep))]
[JsonSerializable(typeof(ApiProtocol))]
// Framework / node types used as roots or as dictionary values.
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(decimal?))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<ProviderEndpoint>))]
[JsonSerializable(typeof(List<ApiProtocol>))]
[JsonSerializable(typeof(List<BillingTraceStep>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, decimal>))]
[JsonSerializable(typeof(Dictionary<string, decimal?>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, SeedProviderPrice>))]
[JsonSerializable(typeof(Dictionary<string, ServiceTierRule>))]
internal partial class JsonContext : JsonSerializerContext
{
}
