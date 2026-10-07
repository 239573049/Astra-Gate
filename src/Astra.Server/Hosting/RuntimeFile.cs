using System.Security.Cryptography;
using System.Text.Json;
using Astra.Core;

namespace Astra.Server.Hosting;

/// <summary>runtime.json: lets the CLI and the desktop app discover a running server.</summary>
public sealed class RuntimeInfo
{
    public int Pid { get; set; }
    public int Port { get; set; }
    public string Host { get; set; } = "";
    public string Version { get; set; } = "";
    public string ApiVersion { get; set; } = "";
    public string StartedBy { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Secret required by /api/admin/shutdown; only readable by the owner (file mode 600).</summary>
    public string RuntimeToken { get; set; } = "";
}

public sealed class RuntimeFile(AstraPaths paths)
{
    private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public RuntimeInfo? Current { get; private set; }

    public RuntimeInfo Write(ServerOptions options)
    {
        var info = new RuntimeInfo
        {
            Pid = Environment.ProcessId,
            Port = options.Port,
            Host = options.Host,
            Version = ServerOptions.Version,
            ApiVersion = ServerOptions.ApiVersion,
            StartedBy = options.StartedBy,
            StartedAt = DateTimeOffset.UtcNow,
            RuntimeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
        };
        var tmp = paths.RuntimeFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(info, FileJson));
        AstraPaths.RestrictToOwner(tmp);
        File.Move(tmp, paths.RuntimeFile, overwrite: true);
        Current = info;
        return info;
    }

    /// <summary>Deletes runtime.json if it still belongs to this process.</summary>
    public void Delete()
    {
        try
        {
            if (!File.Exists(paths.RuntimeFile)) return;
            var existing = JsonSerializer.Deserialize<RuntimeInfo>(File.ReadAllText(paths.RuntimeFile), FileJson);
            if (existing?.Pid == Environment.ProcessId) File.Delete(paths.RuntimeFile);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Best effort on shutdown.
        }
    }
}
