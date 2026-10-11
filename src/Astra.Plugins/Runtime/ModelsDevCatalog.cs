using Astra.Core;

namespace Astra.Plugins.Runtime;

/// <summary>
/// The models.dev catalog the plugin host builds OpenCode's provider info from (host.js reads it as
/// <c>modelsDevPath</c>): an OpenCode plugin that names only its provider gets that provider's models from it, as in
/// OpenCode. Fetched before a host starts when there is none or it is a day old; a failed fetch keeps the copy there
/// is, and a host without any still runs (with the models its plugins list themselves).
/// </summary>
public sealed class ModelsDevCatalog(AstraPaths paths, Func<HttpClient> http, string source = "https://models.dev/api.json")
{
    private const long MaxBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan Fresh = TimeSpan.FromDays(1);
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>The catalog's path when there is one, else null.</summary>
    public string? Current => File.Exists(paths.PluginModelsDevFile) ? paths.PluginModelsDevFile : null;

    /// <summary>Fetches the catalog when it is missing or stale; returns its path, or null when there is none.</summary>
    public async Task<string?> EnsureAsync(Action<string, string> log, CancellationToken ct = default)
    {
        var file = paths.PluginModelsDevFile;
        if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < Fresh) return file;
        await _lock.WaitAsync(ct);
        try
        {
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < Fresh) return file;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var res = await http().GetAsync(source, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            res.EnsureSuccessStatusCode();
            if (res.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("the models.dev catalog is too large");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var part = file + ".part";
            await using (var body = await res.Content.ReadAsStreamAsync(timeout.Token))
            await using (var dst = File.Create(part))
            {
                var buffer = new byte[81920];
                long total = 0;
                int n;
                while ((n = await body.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    total += n;
                    if (total > MaxBytes) throw new InvalidDataException("the models.dev catalog is too large");
                    await dst.WriteAsync(buffer.AsMemory(0, n), timeout.Token);
                }
            }
            // only a JSON object replaces the copy there is
            await using (var check = File.OpenRead(part))
            using (var doc = await System.Text.Json.JsonDocument.ParseAsync(check, cancellationToken: timeout.Token))
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) throw new InvalidDataException("the models.dev catalog isn't an object");
            File.Move(part, file, overwrite: true);
            return file;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            log("warn", $"models.dev catalog not refreshed: {e.Message}");
            return Current;
        }
        finally
        {
            try
            {
                File.Delete(file + ".part");
            }
            catch (IOException)
            {
            }
            _lock.Release();
        }
    }
}
