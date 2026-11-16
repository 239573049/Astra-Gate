using Astra.Core;
using Astra.Core.Clients;
using Astra.Data;

namespace Astra.Server.Api;

/// <summary>
/// Saved profiles (<see cref="ClientProfile"/>): named snapshots of the client side that switch every client at once.
/// Stored as one document under the <c>settings</c> key <see cref="ClientProfiles.SettingsKey"/> — a handful of profiles,
/// always read and written whole together with the active marker, never queried per row, so a table and a migration would
/// add nothing. Applying goes through <see cref="ClientService"/> (the regular enable / disable path).
/// </summary>
public sealed class ProfileService(AstraDatabase db, ClientService clients)
{
    private const int MaxNameLength = 64;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<List<ProfileDto>> ListAsync(CancellationToken ct)
    {
        var doc = await LoadAsync(ct);
        var result = new List<ProfileDto>();
        foreach (var p in doc.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)) result.Add(await ToDtoAsync(p, doc, ct));
        return result;
    }

    public async Task<ProfileDto> GetAsync(string idOrName, CancellationToken ct)
    {
        var doc = await LoadAsync(ct);
        return await ToDtoAsync(Find(doc, idOrName), doc, ct);
    }

    /// <summary>Saves the clients' current state as a new profile, which becomes the active one.</summary>
    public async Task<ProfileDto> SaveAsync(string? name, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var doc = await LoadAsync(ct);
            name = ValidName(doc, name, null);
            var now = DateTimeOffset.UtcNow;
            var profile = new ClientProfile { Id = Ulid.NewUlid(), Name = name, CreatedAt = now, UpdatedAt = now, Clients = await clients.CaptureProfileAsync(ct) };
            doc.Profiles.Add(profile);
            doc.ActiveId = profile.Id;
            await db.Settings.SetAsync(ClientProfiles.SettingsKey, doc, ct);
            return await ToDtoAsync(profile, doc, ct);
        }
        finally { _lock.Release(); }
    }

    /// <summary>Renames a profile and / or replaces its snapshot with the clients' current state (which makes it the active one).</summary>
    public async Task<ProfileDto> UpdateAsync(string idOrName, ProfileInput input, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var doc = await LoadAsync(ct);
            var profile = Find(doc, idOrName);
            if (input.Name is not null) profile.Name = ValidName(doc, input.Name, profile);
            if (input.Overwrite == true)
            {
                profile.Clients = await clients.CaptureProfileAsync(ct);
                doc.ActiveId = profile.Id;
            }
            profile.UpdatedAt = DateTimeOffset.UtcNow;
            await db.Settings.SetAsync(ClientProfiles.SettingsKey, doc, ct);
            return await ToDtoAsync(profile, doc, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteAsync(string idOrName, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var doc = await LoadAsync(ct);
            var profile = Find(doc, idOrName);
            doc.Profiles.Remove(profile);
            if (doc.ActiveId == profile.Id) doc.ActiveId = null;
            await db.Settings.SetAsync(ClientProfiles.SettingsKey, doc, ct);
        }
        finally { _lock.Release(); }
    }

    /// <summary>What applying the profile would change, per client; nothing is written.</summary>
    public async Task<ProfilePlanDto> PreviewAsync(string idOrName, CancellationToken ct)
    {
        var doc = await LoadAsync(ct);
        var profile = Find(doc, idOrName);
        return await clients.PreviewProfileAsync(profile, await ToDtoAsync(profile, doc, ct), ct);
    }

    /// <summary>
    /// Switches every client the profile covers. The profile becomes the active one unless a client failed; clients that
    /// cannot be switched (not installed, a provider gone) are reported in the result and skipped.
    /// </summary>
    public async Task<ProfilePlanDto> ApplyAsync(string idOrName, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var doc = await LoadAsync(ct);
            var profile = Find(doc, idOrName);
            var result = await clients.ApplyProfileAsync(profile, await ToDtoAsync(profile, doc, ct), ct);
            if (result.Steps.All(s => s.Action != "failed"))
            {
                doc.ActiveId = profile.Id;
                await db.Settings.SetAsync(ClientProfiles.SettingsKey, doc, CancellationToken.None);
            }
            return result with { Profile = await ToDtoAsync(profile, doc, CancellationToken.None) };
        }
        finally { _lock.Release(); }
    }

    private async Task<ClientProfiles> LoadAsync(CancellationToken ct) =>
        await db.Settings.GetAsync<ClientProfiles>(ClientProfiles.SettingsKey, ct) ?? new ClientProfiles();

    private static ClientProfile Find(ClientProfiles doc, string idOrName) =>
        doc.Profiles.FirstOrDefault(p => p.Id == idOrName)
        ?? doc.Profiles.FirstOrDefault(p => string.Equals(p.Name, idOrName, StringComparison.OrdinalIgnoreCase))
        ?? throw new AdminApiException(404, "Profile not found");

    private static string ValidName(ClientProfiles doc, string? name, ClientProfile? self)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) throw new AdminApiException(400, "name is required");
        if (name.Length > MaxNameLength) throw new AdminApiException(400, $"name is at most {MaxNameLength} characters");
        if (doc.Profiles.Any(p => p != self && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new AdminApiException(409, "A profile with this name already exists");
        return name;
    }

    private async Task<ProfileDto> ToDtoAsync(ClientProfile p, ClientProfiles doc, CancellationToken ct)
    {
        var active = doc.ActiveId == p.Id;
        return new ProfileDto(p.Id, p.Name, p.CreatedAt, p.UpdatedAt,
            p.Clients.Select(e => new ProfileClientDto(e.Kind, clients.ClientNameOf(e.Kind), e.Enabled,
                e.Bindings.Select(b => new ClientBindingDto(b.ProviderId, b.AccountId)).ToList(), e.Model,
                ModelShortlist.Of(e.Extras) ?? [])).ToList(),
            active, active && await clients.ProfileDriftedAsync(p, ct));
    }
}
