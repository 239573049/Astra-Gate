namespace Astra.Api.Contracts;

/// <summary>
/// One member of a routing group as it is stored: a <c>provider/model</c> target (<paramref name="Provider"/> is a provider id) or
/// another group (<paramref name="Group"/> is its slug) — exactly one of the two.
/// </summary>
/// <param name="Weight">Share of the requests under the <c>weight</c> mode, 1–1000.</param>
public sealed record RoutingGroupMemberDto(string? Provider, string? Model, string? Group, int Weight = 1);

/// <summary>Admin API payload: a routing group, the virtual model <c>group/&lt;slug&gt;</c> (<paramref name="ModelId"/>).</summary>
/// <param name="Mode"><c>smart</c>, <c>order</c>, <c>rotate</c>, <c>usage</c>, <c>pace</c> or <c>weight</c>.</param>
/// <param name="Stays"><c>auto</c>, <c>session</c>, <c>turn</c>, <c>off</c> or <c>strict</c>: how long a conversation stays with the member that answered it.</param>
/// <param name="Listed">Whether the group is offered in model lists.</param>
public sealed record RoutingGroupDto(
    string Id, string Slug, string ModelId, string Name, string Mode, string Stays, bool Enabled, bool Listed,
    List<RoutingGroupMemberDto> Members, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// Create (<c>POST</c>, <paramref name="Name"/> required, <paramref name="Slug"/> defaults to the name's slug) or change
/// (<c>PATCH</c>; the slug never changes) a routing group. Omitted fields are left as they are; <paramref name="Members"/> replaces the
/// whole list. A member's provider may be named by id, alias or name — it is stored as the id.
/// </summary>
public sealed record RoutingGroupWrite(
    string? Name = null, string? Slug = null, string? Mode = null, string? Stays = null, bool? Enabled = null, bool? Listed = null,
    List<RoutingGroupMemberDto>? Members = null);

/// <summary>The live state of one member of a group (the console's member rows).</summary>
/// <param name="Key"><c>provider/model</c> or <c>group/slug</c> as the member is keyed inside its group.</param>
/// <param name="Health"><c>healthy</c>, <c>cooling</c>, <c>open</c> (circuit open), <c>half_open</c>, <c>no_account</c> (no usable login or key), <c>off</c> (provider switched off), <c>missing</c> (provider deleted) or <c>group</c>.</param>
/// <param name="UsedPercent">The fullest quota window's used share; null when no quota figure is known (or it is stale).</param>
/// <param name="Window">The window that decides when the allowance renews (<c>session</c>, <c>weekly</c>, <c>credits</c>, <c>plan</c>).</param>
/// <param name="Pace">Share of that window left per hour until it renews.</param>
/// <param name="LastUsed">When a request last went to this member through the group (in memory; empty after a restart).</param>
/// <param name="Recent">Requests it took in the last 30 minutes.</param>
/// <param name="Problem">Why the member cannot take requests: <c>model_missing</c> (the provider does not serve the model), <c>provider_missing</c>, <c>provider_off</c> or <c>group_missing</c>.</param>
public sealed record RoutingGroupMemberStatusDto(
    string Key, string? ProviderId, string? ProviderName, string? Model, string? Group, int Weight, string Health,
    DateTimeOffset? CoolingUntil, double? UsedPercent, string? Window, DateTimeOffset? ResetsAt, double? Pace,
    DateTimeOffset? LastUsed, int Recent, string? Problem);

/// <summary>A group's live status: its members and its last decision (when, and why its first member stood first — a <c>RankReasons</c> code).</summary>
public sealed record RoutingGroupStatusDto(
    string Id, string Slug, DateTimeOffset? LastDecisionAt, string? LastReason, List<RoutingGroupMemberStatusDto> Members);
