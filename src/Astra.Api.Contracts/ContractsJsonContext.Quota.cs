using System.Text.Json.Serialization;

namespace Astra.Api.Contracts;

// Registrations for the quota template mirror in ProviderDtos.cs (its own partial file so the work package that added it touches no other
// line of ContractsJsonContext.cs).
[JsonSerializable(typeof(QuotaTemplateDto))]
[JsonSerializable(typeof(List<QuotaTemplateDto>))]
[JsonSerializable(typeof(QuotaParamSpecDto))]
[JsonSerializable(typeof(QuotaRequestSpecDto))]
public partial class ContractsJsonContext
{
}
