using System.Text.Json.Serialization;

namespace Astra.Api.Contracts;

// Registrations for PluginMarketDtos.cs (its own partial file so the work package that added it touches no other line of ContractsJsonContext.cs).
[JsonSerializable(typeof(MarketListingDto))]
[JsonSerializable(typeof(List<MarketListingDto>))]
[JsonSerializable(typeof(MarketTarballDto))]
[JsonSerializable(typeof(MarketSearchResultDto))]
[JsonSerializable(typeof(List<MarketSearchResultDto>))]
[JsonSerializable(typeof(PluginJobDto))]
[JsonSerializable(typeof(PluginHostLoadedDto))]
[JsonSerializable(typeof(PluginHostMessageDto))]
[JsonSerializable(typeof(PluginHostStatusDto))]
public partial class ContractsJsonContext
{
}
