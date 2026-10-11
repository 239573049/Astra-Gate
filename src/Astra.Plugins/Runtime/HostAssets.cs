using System.Reflection;
using System.Security.Cryptography;
using Astra.Core;

namespace Astra.Plugins.Runtime;

/// <summary>
/// magpie's plugin host (host.js) and its pi loader (pi.js), embedded in the server and written out
/// under <see cref="AstraPaths.PluginHostDir"/> in a folder named by their content hash: a new build
/// writes a new folder, and a running host keeps the files it started from.
/// </summary>
public sealed class HostAssets(AstraPaths paths)
{
    private readonly Lazy<(byte[] Host, byte[] Pi, string Hash)> _assets = new(Load);

    /// <summary>The content hash (first 12 hex digits of SHA-256 over both files) naming the folder.</summary>
    public string Hash => _assets.Value.Hash;

    /// <summary>Writes the files when this build's folder lacks them, and returns their paths.</summary>
    public (string HostJs, string PiJs) Ensure()
    {
        var (host, pi, hash) = _assets.Value;
        var dir = Path.Combine(paths.PluginHostDir, hash);
        var hostJs = Path.Combine(dir, "host.js");
        var piJs = Path.Combine(dir, "pi.js");
        Directory.CreateDirectory(dir);
        WriteIfChanged(hostJs, host);
        WriteIfChanged(piJs, pi);
        return (hostJs, piJs);
    }

    /// <summary>The embedded host.js, as shipped (for the vendoring check).</summary>
    public static byte[] HostJs() => Read("host.js");

    /// <summary>The embedded pi.js, as shipped.</summary>
    public static byte[] PiJs() => Read("pi.js");

    private static (byte[], byte[], string) Load()
    {
        var host = HostJs();
        var pi = PiJs();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(host);
        sha.AppendData(pi);
        return (host, pi, Convert.ToHexStringLower(sha.GetHashAndReset())[..12]);
    }

    private static byte[] Read(string name)
    {
        using var s = typeof(HostAssets).Assembly.GetManifestResourceStream("Astra.Plugins.Host." + name)
                      ?? throw new InvalidOperationException($"the plugin host's {name} is not embedded");
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    // Same content is left alone (a host may be reading it); anything else is replaced whole.
    private static void WriteIfChanged(string path, byte[] content)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length == content.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
                return;
        }
        catch (IOException)
        {
        }
        var part = path + ".part";
        File.WriteAllBytes(part, content);
        File.Move(part, path, overwrite: true);
    }
}
