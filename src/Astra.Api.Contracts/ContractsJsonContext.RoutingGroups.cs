using System.Text.Json.Serialization;

namespace Astra.Api.Contracts;

// Registrations for RoutingGroupDtos.cs (its own partial file, like the quota mirror, so no other line of ContractsJsonContext.cs changes).
[JsonSerializable(typeof(RoutingGroupDto))]
[JsonSerializable(typeof(List<RoutingGroupDto>))]
[JsonSerializable(typeof(RoutingGroupWrite))]
[JsonSerializable(typeof(RoutingGroupMemberDto))]
[JsonSerializable(typeof(RoutingGroupMemberStatusDto))]
[JsonSerializable(typeof(RoutingGroupStatusDto))]
[JsonSerializable(typeof(List<RoutingGroupStatusDto>))]
public partial class ContractsJsonContext
{
}
