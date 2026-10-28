using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Api.Contracts;
using Astra.Desktop.Core.I18n;

namespace Astra.Desktop.Core.Accounts;

/// <summary>What the import sheet was opened for (web <c>ImportMode</c>): many entries of any format, one pasted credential, or keys only.</summary>
public enum ImportMode
{
    /// <summary>Many entries of any accepted format.</summary>
    Batch,

    /// <summary>One pasted credential.</summary>
    Token,

    /// <summary>API keys only.</summary>
    ApiKey,
}

/// <summary>The rules of the account import sheet (web AccountImportSheet.tsx): entry counting, accepted formats, titles and the result rows.</summary>
public static partial class AccountImport
{
    [GeneratedRegex(@"[,\s]+")]
    private static partial Regex Separators();

    private static string StripQuotes(string s) => s.Trim('"', '\'');

    /// <summary>Splits one line into tokens the way the server does (commas / whitespace / semicolons; direct Copilot tokens stay whole).</summary>
    public static int LineTokens(string line) => Separators().Split(line)
        .SelectMany(p =>
        {
            var cleaned = StripQuotes(p);
            // Direct Copilot tokens (tid=…;exp=…) contain semicolons themselves.
            return cleaned.StartsWith("tid=", StringComparison.Ordinal) || cleaned.StartsWith("exp=", StringComparison.Ordinal) ? [cleaned] : cleaned.Split(';');
        })
        .Count(p => StripQuotes(p).Length > 0);

    /// <summary>
    /// How many entries the pasted text holds; mirrors the server's <c>CredentialImport.SplitEntries</c>: one JSON document (an object, or an
    /// array of objects / strings) counts as is, otherwise one entry per line — a JSON object line is one entry, other lines split like
    /// tokens. Blank and <c>#</c> lines are ignored (web <c>countImportEntries</c>).
    /// </summary>
    public static int CountEntries(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return 0;
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                var doc = JsonNode.Parse(trimmed);
                if (doc is JsonArray array)
                    return array.Count(x => x is JsonObject or JsonArray
                        || (x is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>().Trim().Length > 0));
                if (doc is JsonObject) return 1;
            }
            catch (JsonException)
            {
                // not one JSON document: fall through to the line split
            }
        }
        var n = 0;
        foreach (var raw in text!.Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            n += t.StartsWith('{') ? 1 : LineTokens(t);
        }
        return n;
    }

    /// <summary>The sheet's title key (web <c>TITLE</c>).</summary>
    public static string TitleKey(ImportMode mode) => mode switch
    {
        ImportMode.Token => "providers.accounts.import.tokenTitle",
        ImportMode.ApiKey => "providers.accounts.import.apiKeyTitle",
        _ => "providers.accounts.import.batchTitle",
    };

    /// <summary>The formats the hint lists: every accepted one, or only the <c>-api-key</c> ones for a key import.</summary>
    public static IReadOnlyList<string> Formats(AccountImportInfoDto info, ImportMode mode)
        => mode == ImportMode.ApiKey ? info.Formats.Where(f => f.EndsWith("-api-key", StringComparison.Ordinal)).ToList() : info.Formats;

    /// <summary>The "accepts: …" hint, or null when no format is listed.</summary>
    public static string? FormatsHint(AccountImportInfoDto info, ImportMode mode)
    {
        var formats = Formats(info, mode);
        return formats.Count == 0 ? null : Loc.T("providers.accounts.import.formats", ("formats", string.Join(" · ", formats.Select(f => Loc.T("providers.accounts.format." + f)))));
    }

    /// <summary>A refresh-token / credential-JSON import rotates the token it uses: the sheet warns (web <c>takesRefreshTokens</c>).</summary>
    public static bool TakesRefreshTokens(AccountImportInfoDto info, ImportMode mode)
        => mode != ImportMode.ApiKey && info.Formats.Any(f => f.Contains("refresh-token", StringComparison.Ordinal) || f.EndsWith("-json", StringComparison.Ordinal));

    /// <summary>The label / placeholder of the text box.</summary>
    public static string Label(ImportMode mode) => Loc.T(mode == ImportMode.ApiKey ? "providers.accounts.import.apiKeyLabel" : "providers.accounts.import.label");

    /// <summary>Whether "import" may run: something to import, within the server's batch limit.</summary>
    public static bool CanRun(int count, AccountImportInfoDto info) => count > 0 && count <= info.MaxBatch;

    /// <summary>The request body (web <c>submit</c>): unset flags are left out, the proxy only when given.</summary>
    public static AccountImportRequest Request(string text, ImportMode mode, bool dryRun, string? proxy)
        => new(text, mode == ImportMode.ApiKey ? true : null, dryRun ? true : null, string.IsNullOrWhiteSpace(proxy) ? null : proxy.Trim());

    /// <summary>The result's summary line (web <c>previewSummary</c> / <c>summary</c>).</summary>
    public static string Summary(BatchImportResultDto r, bool dryRun)
        => Loc.T(dryRun ? "providers.accounts.import.previewSummary" : "providers.accounts.import.summary", ("ok", r.Imported), ("dup", r.Duplicates), ("fail", r.Failed));

    /// <summary>A status's label key (web <c>STATUS_LABEL</c>).</summary>
    public static string StatusKey(string status) => "providers.subscription.importBatchStatus." + status;

    /// <summary>A status's badge tone (web <c>STATUS_TONE</c>): green / accent / neutral / orange / red.</summary>
    public static string StatusTone(string status) => status switch
    {
        BatchImportStatus.Imported => "green",
        BatchImportStatus.Updated or BatchImportStatus.Ready => "accent",
        BatchImportStatus.InvalidFormat => "orange",
        BatchImportStatus.Failed => "red",
        _ => "neutral",
    };

    /// <summary>The text of a result row: the imported account's name (and any warning), else the message.</summary>
    public static string RowText(BatchImportItemDto item)
        => item.Account is { DisplayName.Length: > 0 } a && item.Status is BatchImportStatus.Imported or BatchImportStatus.Updated
            ? a.DisplayName + (string.IsNullOrEmpty(item.Message) ? "" : " · " + item.Message)
            : item.Message ?? "";

    /// <summary>What a credential kind reads as (<c>providers.accounts.credential.*</c>); null for none.</summary>
    public static string? KindText(string? kind) => string.IsNullOrEmpty(kind) ? null : Loc.T("providers.accounts.credential." + kind);
}
