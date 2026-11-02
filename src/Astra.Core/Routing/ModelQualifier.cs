using Astra.Core.Models;

namespace Astra.Core.Routing;

/// <summary>
/// Provider-qualified model ids (issue #21): <c>&lt;model&gt;&lt;separator&gt;&lt;provider alias&gt;</c>. Two providers serving the same model
/// id are two ids a client can pick between. The id is only ever a name the client uses — the gateway splits it while
/// routing and never writes it into the request body, so the upstream model, billing and the log's requested model stay as
/// they were. Null (<see cref="From"/>) = the feature is off.
/// </summary>
public sealed record ModelQualifier(string Separator)
{
    public const string DefaultSeparator = "@";
    public const int MaxSeparatorLength = 8;

    /// <summary>The qualifier the settings ask for, or null while the feature is off.</summary>
    public static ModelQualifier? From(AppSettings settings) =>
        settings.ProviderQualifiedModels ? new ModelQualifier(Clean(settings.ModelProviderSeparator)) : null;

    /// <summary>A separator the model id can be split on: 1–8 characters without whitespace, else the default.</summary>
    public static string Clean(string? separator) =>
        separator is { Length: > 0 and <= MaxSeparatorLength } && !separator.Any(char.IsWhiteSpace) ? separator : DefaultSeparator;

    /// <summary>Whether <paramref name="separator"/> is acceptable as is (what the settings endpoint validates).</summary>
    public static bool IsValidSeparator(string? separator) => separator is not null && Clean(separator) == separator;

    /// <summary>The id a client sees for <paramref name="modelId"/> served by <paramref name="provider"/>.</summary>
    public string Qualify(string modelId, Provider provider) => modelId + Separator + provider.Alias();

    /// <summary>
    /// Splits <paramref name="requested"/> at its last separator whose right-hand side is the alias of one of
    /// <paramref name="providers"/> (alias compared ignoring case; the first provider in order wins a shared alias). Only a known
    /// alias splits, so a model id that itself contains the separator is left alone. False when it does not end in one.
    /// </summary>
    public bool TrySplit(string requested, IEnumerable<Provider> providers, out string modelId, out Provider provider)
    {
        modelId = requested;
        provider = null!;
        var at = requested.LastIndexOf(Separator, StringComparison.Ordinal);
        // Every separator position is tried from the right: an alias may itself contain the separator.
        while (at > 0)
        {
            var alias = requested[(at + Separator.Length)..];
            if (alias.Length > 0)
                foreach (var candidate in providers)
                    if (string.Equals(candidate.Alias(), alias, StringComparison.OrdinalIgnoreCase))
                    {
                        modelId = requested[..at];
                        provider = candidate;
                        return true;
                    }
            at = requested.LastIndexOf(Separator, at - 1, StringComparison.Ordinal);
        }
        return false;
    }
}
