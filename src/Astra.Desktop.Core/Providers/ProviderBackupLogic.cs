using System.Text.Json.Nodes;
using Astra.Desktop.Core.I18n;

namespace Astra.Desktop.Core.Providers;

/// <summary>One provider of a previewed backup file (web <c>BackupPreviewItem</c>): never carries a key, only a mask.</summary>
public sealed record BackupItem(
    int Index, string Name, string Status, int ModelCount, IReadOnlyList<string> Addresses, bool HasKey, string? KeyMasked,
    string? ExistingName, string? Error, IReadOnlyList<string> Ignored);

/// <summary>The preview of a backup file: how its keys are stored, and what each provider would do.</summary>
public sealed record BackupPreview(string Secrets, IReadOnlyList<BackupItem> Items);

/// <summary>The outcome of an import: how many providers were created and how many entries were skipped.</summary>
public sealed record BackupResult(int Added, int Skipped);

/// <summary>The saved WebDAV copy settings (web <c>WebDavStatus</c>); the password is never sent back, only whether one is stored.</summary>
public sealed record WebDavStatus(bool Configured, string Url, string Username, bool HasPassword, string Folder);

/// <summary>
/// Rules of the provider backup sheet (web <c>ProviderBackupSheet</c>, <c>.plan/provider-backup.md</c>): the three ways to store keys, which
/// entries can be ticked (only new ones start ticked), and how the server's JSON is read. The file itself stays a <see cref="JsonObject"/>: the
/// sheet reads its <c>secrets</c> and provider count and hands everything else back to the server untouched.
/// </summary>
public static class ProviderBackupLogic
{
    /// <summary>No key in the file (the default).</summary>
    public const string None = "none";

    /// <summary>Keys sealed with a passphrase.</summary>
    public const string Passphrase = "passphrase";

    /// <summary>Keys written as they are.</summary>
    public const string Plaintext = "plaintext";

    /// <summary>The shortest passphrase the server accepts.</summary>
    public const int MinPassphraseLength = 8;

    /// <summary>The key policies in the order the picker lists them.</summary>
    public static readonly IReadOnlyList<string> Modes = [None, Passphrase, Plaintext];

    /// <summary>The key policies the WebDAV copy offers: the server never uploads plaintext keys.</summary>
    public static readonly IReadOnlyList<string> WebDavModes = [None, Passphrase];

    /// <summary>The folder the server uses when none is given.</summary>
    public const string DefaultWebDavFolder = "astra";

    /// <summary>Picker label of a key policy.</summary>
    public static string ModeLabel(string mode) => Loc.T("backup.secrets." + mode);

    /// <summary>Whether the export button is usable: a passphrase needs its minimum length.</summary>
    public static bool CanExport(string mode, string passphrase) => mode != Passphrase || passphrase.Length >= MinPassphraseLength;

    /// <summary>"astra-providers-2026-11-11.json".</summary>
    public static string FileName(DateTimeOffset now) => $"astra-providers-{now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}.json";

    /// <summary>Reads a chosen file; null when it is not an Astra provider backup (web <c>isBackupFile</c>).</summary>
    public static JsonObject? ParseFile(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is JsonObject file && (string?)file["format"] == "astra-providers" && file["providers"] is JsonArray) return file;
        }
        catch (System.Text.Json.JsonException)
        {
            // not JSON at all: the same answer as any other file that is not a backup
        }
        return null;
    }

    /// <summary>How the file's keys are stored: <see cref="None"/>, <see cref="Passphrase"/> or <see cref="Plaintext"/>.</summary>
    public static string SecretsOf(JsonObject file) => (string?)file["secrets"] ?? None;

    /// <summary>Whether previewing needs the passphrase first.</summary>
    public static bool NeedsPassphrase(JsonObject file) => SecretsOf(file) == Passphrase;

    /// <summary>The number of providers in a file.</summary>
    public static int Count(JsonObject file) => (file["providers"] as JsonArray)?.Count ?? 0;

    /// <summary>The body of a preview (indexes null) or an import. The passphrase only travels with a file that needs one.</summary>
    public static JsonObject Request(JsonObject file, string? passphrase, IEnumerable<int>? indexes)
    {
        var body = new JsonObject { ["file"] = file.DeepClone() };
        if (NeedsPassphrase(file) && passphrase is not null) body["passphrase"] = passphrase;
        if (indexes is not null) body["indexes"] = new JsonArray(indexes.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());
        return body;
    }

    /// <summary>Decodes the server's preview.</summary>
    public static BackupPreview ParsePreview(JsonObject? json)
    {
        var items = new List<BackupItem>();
        foreach (var node in json?["items"] as JsonArray ?? [])
        {
            if (node is not JsonObject o) continue;
            var addresses = (o["endpoints"] as JsonArray ?? []).Select(e => (string?)e?["baseUrl"]).Where(a => !string.IsNullOrEmpty(a)).Select(a => a!).ToList();
            items.Add(new BackupItem(
                (int?)o["index"] ?? items.Count, (string?)o["name"] ?? "", (string?)o["status"] ?? "invalid", (int?)o["modelCount"] ?? 0, addresses,
                (bool?)o["hasKey"] ?? false, (string?)o["keyMasked"], (string?)o["existing"]?["name"], (string?)o["error"],
                (o["ignored"] as JsonArray ?? []).Select(i => (string?)i ?? "").Where(i => i.Length > 0).ToList()));
        }
        return new BackupPreview((string?)json?["secrets"] ?? None, items);
    }

    /// <summary>Decodes <c>GET|PUT|DELETE /api/backup/webdav</c>.</summary>
    public static WebDavStatus ParseWebDavStatus(JsonObject? json)
        => new((bool?)json?["configured"] ?? false, (string?)json?["url"] ?? "", (string?)json?["username"] ?? "", (bool?)json?["hasPassword"] ?? false,
            (string?)json?["folder"] is { Length: > 0 } folder ? folder : DefaultWebDavFolder);

    /// <summary>The body of <c>PUT /api/backup/webdav</c>; an empty password is left out, which keeps the stored one.</summary>
    public static JsonObject WebDavSettingsRequest(string url, string username, string folder, string password)
    {
        var body = new JsonObject { ["url"] = url, ["username"] = username, ["folder"] = folder };
        if (password.Length > 0) body["password"] = password;
        return body;
    }

    /// <summary>The body of <c>POST /api/backup/webdav/upload</c>.</summary>
    public static JsonObject WebDavUploadRequest(string secrets, string passphrase)
    {
        var body = new JsonObject { ["secrets"] = secrets };
        if (secrets == Passphrase) body["passphrase"] = passphrase;
        return body;
    }

    /// <summary>Decodes the server's import answer.</summary>
    public static BackupResult ParseResult(JsonObject? json)
        => new((json?["added"] as JsonArray)?.Count ?? 0, (json?["skipped"] as JsonArray)?.Count ?? 0);

    /// <summary>Only new entries and ones whose address is already used (by another key) can be imported.</summary>
    public static bool Selectable(BackupItem item) => item.Status is "new" or "sameHost";

    /// <summary>The ticks after a preview: only brand-new entries.</summary>
    public static HashSet<int> DefaultSelection(BackupPreview preview) => preview.Items.Where(i => i.Status == "new").Select(i => i.Index).ToHashSet();

    /// <summary>The entries that will be imported: ticked and selectable.</summary>
    public static List<int> Chosen(BackupPreview preview, ISet<int> ticked) => preview.Items.Where(i => ticked.Contains(i.Index) && Selectable(i)).Select(i => i.Index).ToList();

    /// <summary>Badge tone of a status (the view maps it to its own tones).</summary>
    public static ImportTone ToneOf(string status) => status switch
    {
        "new" => ImportTone.Green,
        "sameHost" => ImportTone.Orange,
        "invalid" => ImportTone.Red,
        _ => ImportTone.Neutral,
    };

    /// <summary>The toast after an import.</summary>
    public static string DoneText(BackupResult r)
        => r.Skipped > 0 ? Loc.T("backup.import.doneSkipped", ("count", r.Added), ("skipped", r.Skipped)) : Loc.T("backup.import.done", ("count", r.Added));
}
