namespace Astra.Api.Contracts;

/// <summary>One client inside a saved profile (<c>GET /api/profiles</c>): what applying the profile would set for it.</summary>
/// <param name="Kind">The client kind.</param>
/// <param name="Name">The client's display name.</param>
/// <param name="Enabled">Whether Astra is enabled for the client in the profile.</param>
/// <param name="Bindings">Its providers in routing order.</param>
/// <param name="Model">The model the profile selects (the client's "default model"), if any.</param>
/// <param name="ModelShortlist">The client's model shortlist (empty = it sees every model).</param>
public sealed record ProfileClientDto(
    string Kind, string Name, bool Enabled, IReadOnlyList<ClientBindingDto> Bindings, string? Model, IReadOnlyList<string> ModelShortlist);

/// <summary>
/// A named snapshot of every configured client (a profile; magpie's <c>save</c> / <c>use</c>).
/// <paramref name="Active"/>: the profile applied (or saved) last; <paramref name="Drifted"/>: for the active one, the clients no
/// longer match it (a client was switched, rebound or reconfigured since).
/// </summary>
public sealed record ProfileDto(
    string Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<ProfileClientDto> Clients, bool Active, bool Drifted);

/// <summary>
/// Body of <c>POST /api/profiles</c> (save the current state as <c>name</c>) and <c>PUT /api/profiles/{id}</c>: <c>name</c>
/// renames; <c>overwrite</c> replaces the profile's snapshot with the current state of the clients.
/// </summary>
public sealed record ProfileInput(string? Name, bool? Overwrite = null);

/// <summary>
/// What a profile does to one client. <see cref="Action"/>: <c>enable</c>, <c>update</c> (enabled before and after),
/// <c>disable</c>, <c>store</c> (stays off; only Astra's stored choices change), <c>unchanged</c>, <c>skipped</c> (see
/// <see cref="Reason"/>) or <c>failed</c> (<see cref="Reason"/> is the error). <see cref="Reason"/> for a skipped client:
/// <c>not-installed</c>, <c>unavailable</c>, <c>unknown-client</c> or <c>no-provider</c> (every provider it named is gone).
/// <see cref="Changes"/> / <see cref="Diffs"/> are the config edits (a preview; empty once applied). <see cref="Notes"/> carries
/// what was left alone (keys the user changed) and warnings.
/// </summary>
public sealed record ProfileStepDto(
    string Kind, string Name, string Action, string? Reason, IReadOnlyList<ConfigChangeDto> Changes, IReadOnlyList<FileDiffDto> Diffs,
    IReadOnlyList<string> Notes);

/// <summary>
/// Something a profile named that no longer exists, dropped from the plan. <see cref="What"/>: <c>provider</c>, <c>account</c>
/// or <c>token</c>; <see cref="Reason"/>: <c>missing</c> or <c>disabled</c>.
/// </summary>
public sealed record ProfileDropDto(string Kind, string What, string Id, string Reason);

/// <summary>The preview (<c>GET /api/profiles/{id}/preview</c>) or the outcome (<c>POST …/apply</c>) of applying a profile.</summary>
public sealed record ProfilePlanDto(ProfileDto Profile, IReadOnlyList<ProfileStepDto> Steps, IReadOnlyList<ProfileDropDto> Dropped);

/// <summary>Body of <c>PUT /api/clients/{kind}/shortlist</c>: the model ids the client should see; empty clears the shortlist.</summary>
public sealed record ShortlistInput(List<string>? Models);
