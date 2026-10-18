using Astra.Core.Routing;
using Astra.Data;
using Astra.Gateway.Pipeline;

namespace Astra.Gateway.Routing;

/// <summary>
/// The smart-routing settings of one moment and how many changes led to them. A decision made under an older
/// <paramref name="Generation"/> is never served again (<see cref="DecisionStore"/>). The settings are shared with every
/// request: read them, never change them.
/// </summary>
public sealed record SmartRoutingSnapshot(SmartRoutingSettings Settings, int Generation);

/// <summary>Smart-routing settings that were refused, with every reason (the admin API answers 400 with them).</summary>
public sealed class RoutingSettingsException(IReadOnlyList<RoutingProblem> problems)
    : Exception(problems.Count > 0 ? problems[0].Message : "invalid smart-routing settings")
{
    public IReadOnlyList<RoutingProblem> Problems { get; } = problems;
}

/// <summary>
/// Owns the smart-routing settings (plan §5.7), the way <see cref="PrivacyGuardService"/> owns the privacy guard's: one
/// immutable snapshot that a request reads once and uses throughout, replaced as a whole when the settings change.
/// Stored under its own key in <c>settings</c> (<see cref="SmartRoutingSettings.StorageKey"/>); no migration.
/// </summary>
public sealed class SmartRoutingService(AstraDatabase db, ClientRouting routing)
{
    private readonly SemaphoreSlim _writes = new(1, 1);
    private volatile SmartRoutingSnapshot _snapshot = new(new SmartRoutingSettings(), 0);

    public SmartRoutingSnapshot Snapshot => _snapshot;

    /// <summary>The active settings; shared, so never changed by the caller.</summary>
    public SmartRoutingSettings Current => _snapshot.Settings;

    /// <summary>Whether <paramref name="model"/> is the virtual model — whether or not smart routing is switched on.</summary>
    public bool IsVirtual(string model) => string.Equals(model, Current.VirtualModel, StringComparison.Ordinal);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var loaded = (await db.Settings.GetAsync<SmartRoutingSettings>(SmartRoutingSettings.StorageKey, ct) ?? new SmartRoutingSettings()).Clone();
        FillProviders(loaded, await RoutingOptionsAsync(ct));
        _snapshot = new SmartRoutingSnapshot(loaded, 0);
    }

    /// <summary>
    /// Applies <paramref name="change"/> to a copy of the settings, checks the result and, only if it is valid, stores and
    /// activates it. Changes are applied one at a time, so two saves cannot overwrite each other.
    /// </summary>
    /// <exception cref="RoutingSettingsException">The result would not be valid; nothing is saved.</exception>
    public async Task<SmartRoutingSettings> UpdateAsync(Action<SmartRoutingSettings> change, CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct);
        try
        {
            var next = _snapshot.Settings.Clone();
            change(next);
            Normalize(next);
            var options = await RoutingOptionsAsync(ct);
            FillProviders(next, options);
            var problems = CheckAgainst(next, options);
            if (problems.Count > 0) throw new RoutingSettingsException(problems);
            await db.Settings.SetAsync(SmartRoutingSettings.StorageKey, next, ct);
            _snapshot = new SmartRoutingSnapshot(next, _snapshot.Generation + 1);
            return next.Clone();
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>
    /// Every model id some usable provider serves (an enabled model, or a mapped id), each once and listed for the first
    /// provider that serves it — what a grade's model can be chosen from, and what the gateway can route to.
    /// </summary>
    public async Task<IReadOnlyList<RoutableModel>> RoutableModelsAsync(CancellationToken ct = default)
    {
        var targets = new List<RouteTarget>();
        foreach (var provider in await db.Providers.ListAsync(ct))
            if (ClientRouting.IsUsable(provider)) targets.Add(new RouteTarget(provider, null));
        return await routing.ListModelsAsync(targets, ct);
    }

    /// <summary>
    /// Every model a grade can be given, as each usable provider serves it (an enabled model, or a mapped id): the same id
    /// on two providers is two options, because a grade names the provider its requests go to.
    /// </summary>
    public async Task<IReadOnlyList<RoutableModel>> RoutingOptionsAsync(CancellationToken ct = default)
    {
        var options = new List<RoutableModel>();
        foreach (var provider in await db.Providers.ListAsync(ct))
            if (ClientRouting.IsUsable(provider)) options.AddRange(await routing.ListModelsAsync([new RouteTarget(provider, null)], ct));
        return options;
    }

    /// <summary>
    /// Gives a grade saved before grades named a provider the first usable one serving its model — the provider the
    /// console showed next to the model when it was chosen. A grade whose model no provider serves keeps none.
    /// </summary>
    public static void FillProviders(SmartRoutingSettings s, IReadOnlyList<RoutableModel> options)
    {
        foreach (var grade in s.Grades)
            if (grade is { ProviderId: null, Model.Length: > 0 } && options.FirstOrDefault(o => o.Id == grade.Model) is { } option)
                grade.ProviderId = option.Target.Provider.Id;
    }

    /// <summary>The shape rules always; completeness and each grade's provider and model when the result is switched on.</summary>
    private static List<RoutingProblem> CheckAgainst(SmartRoutingSettings next, IReadOnlyList<RoutableModel> options)
    {
        var problems = SmartRoutingValidator.Check(next);
        // Clients ask for the virtual model by name: it must not also be a model a provider serves.
        if (SmartRoutingValidator.IsValidVirtualModel(next.VirtualModel) && options.Any(o => o.Id == next.VirtualModel))
            problems.Add(new(SmartRoutingValidator.Codes.VirtualModelConflict,
                $"'{next.VirtualModel}' is already a model an enabled provider serves; choose another virtual model id", "virtualModel"));
        if (!next.Enabled) return problems;

        problems.AddRange(SmartRoutingValidator.CheckComplete(next));
        for (var i = 0; i < next.Grades.Count; i++)
        {
            var grade = next.Grades[i];
            if (grade.Model.Length == 0 || options.Any(o => o.Id == grade.Model && o.Target.Provider.Id == grade.ProviderId)) continue;
            problems.Add(new(SmartRoutingValidator.Codes.GradeModelUnroutable,
                grade.ProviderId is null
                    ? $"no enabled provider serves '{grade.Model}' (grade '{grade.Id}')"
                    : $"provider '{grade.ProviderId}' is not usable or does not serve '{grade.Model}' (grade '{grade.Id}')",
                $"grades[{i}].model"));
        }
        return problems;
    }

    /// <summary>Trims what a person typed, so a stray space cannot make a model id or an address "different".</summary>
    public static void Normalize(SmartRoutingSettings s)
    {
        s.VirtualModel = (s.VirtualModel ?? "").Trim();
        s.Judge.Protocol = (s.Judge.Protocol ?? "").Trim();
        s.Judge.Model = (s.Judge.Model ?? "").Trim();
        s.Judge.BaseUrl = string.IsNullOrWhiteSpace(s.Judge.BaseUrl) ? null : s.Judge.BaseUrl.Trim().TrimEnd('/');
        s.Judge.Instructions = string.IsNullOrWhiteSpace(s.Judge.Instructions) ? null : s.Judge.Instructions.Trim();
        s.Rules.Mode = (s.Rules.Mode ?? "").Trim();
        s.Rules.FallbackGrade = (s.Rules.FallbackGrade ?? "").Trim();
        foreach (var grade in s.Grades)
        {
            grade.Id = (grade.Id ?? "").Trim();
            grade.Name = (grade.Name ?? "").Trim();
            grade.Description = (grade.Description ?? "").Trim();
            grade.Model = (grade.Model ?? "").Trim();
            grade.ProviderId = string.IsNullOrWhiteSpace(grade.ProviderId) ? null : grade.ProviderId.Trim();
        }
    }
}
