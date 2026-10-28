using Astra.Api.Contracts;
using Astra.Desktop.Core.I18n;

namespace Astra.Desktop.Core.Providers;

/// <summary>Candidate status -> badge tone (web <c>STATUS_TONE</c>); the view maps these to its own tones.</summary>
public enum ImportTone
{
    /// <summary>Muted.</summary>
    Neutral,

    /// <summary>Green: brand new.</summary>
    Green,

    /// <summary>Orange: same host as an existing provider.</summary>
    Orange,

    /// <summary>Red: cannot be imported.</summary>
    Red,
}

/// <summary>
/// Selection and wording rules of the import sheet (ImportProvidersSheet.tsx): only brand-new entries start ticked, "same" / "skip" ones can
/// never be chosen, the search matches name or address, and a 409 on import shows the server's message and rescans.
/// </summary>
public static class ImportLogic
{
    /// <summary>The selection key of a candidate (source and ref).</summary>
    public static string Key(ImportCandidateDto c) => c.Source + "\u0000" + c.Ref;

    /// <summary>Whether the candidate's checkbox is usable.</summary>
    public static bool Selectable(ImportCandidateDto c) => c.Status is not ("skip" or "same");

    /// <summary>The ticks after every (re)scan: only new, enabled entries.</summary>
    public static HashSet<string> DefaultSelection(IEnumerable<ImportSourceDto>? sources)
        => (sources ?? []).SelectMany(s => s.Items).Where(c => c.Status == "new").Select(Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>Search match on name or any endpoint address (empty query matches all).</summary>
    public static bool Matches(ImportCandidateDto c, string query)
    {
        var q = query.Trim().ToLowerInvariant();
        return q.Length == 0 || c.Name.ToLowerInvariant().Contains(q, StringComparison.Ordinal)
            || c.Endpoints.Any(e => e.BaseUrl.ToLowerInvariant().Contains(q, StringComparison.Ordinal));
    }

    /// <summary>The candidates that will be imported: ticked and selectable.</summary>
    public static List<ImportCandidateDto> Chosen(IEnumerable<ImportSourceDto>? sources, ISet<string> selected)
        => (sources ?? []).SelectMany(s => s.Items).Where(c => selected.Contains(Key(c)) && Selectable(c)).ToList();

    /// <summary>Badge tone of a status.</summary>
    public static ImportTone ToneOf(string status) => status switch
    {
        "new" => ImportTone.Green,
        "sameHost" => ImportTone.Orange,
        "skip" => ImportTone.Red,
        _ => ImportTone.Neutral,
    };

    /// <summary>The explanatory line under a row, or null.</summary>
    public static string? Hint(ImportCandidateDto c)
    {
        if (c.Status == "skip" && !string.IsNullOrEmpty(c.SkipReason)) return Loc.T("import.skip." + c.SkipReason);
        if (c.Status == "same" && c.Existing is { } e) return Loc.T("import.existing", ("name", e.Name));
        if (c.Status == "sameHost" && c.Existing is { } h) return Loc.T("import.sameHostHint", ("name", h.Name));
        return null;
    }

    /// <summary>"Chat · api.openai.com / Anthropic · api.anthropic.com".</summary>
    public static string ProtocolsLine(ImportCandidateDto c)
        => string.Join(" / ", c.Endpoints.Select(e => $"{ProviderProtocols.Short(Astra.Core.ApiProtocols.ToId(e.Protocol))} · {ProviderFormat.HostOf(e.BaseUrl)}"));

    /// <summary>The toast after an import: "imported n" or "imported n, skipped m".</summary>
    public static string DoneText(ImportResultDto result)
        => result.Skipped.Count > 0
            ? Loc.T("import.doneSkipped", ("count", result.Added.Count), ("skipped", result.Skipped.Count))
            : Loc.T("import.done", ("count", result.Added.Count));
}
