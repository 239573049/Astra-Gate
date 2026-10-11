using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Astra.Plugins.Runtime;

namespace Astra.Plugins.Tests;

/// <summary>The embedded host.js / pi.js are exactly what Host/VENDORED.md records — a re-vendor is deliberate.</summary>
public partial class HostAssetsTests
{
    [Theory]
    [InlineData("host.js")]
    [InlineData("pi.js")]
    public void Embedded_files_match_the_vendored_hashes(string name)
    {
        var bytes = name == "host.js" ? HostAssets.HostJs() : HostAssets.PiJs();
        var row = Row().Matches(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Astra.Plugins", "Host", "VENDORED.md")))
            .Single(m => m.Groups["file"].Value == name);
        Assert.Equal(row.Groups["sha"].Value, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [Fact]
    public void Ensure_writes_both_files_into_a_folder_named_by_their_hash()
    {
        var home = Directory.CreateTempSubdirectory("astra-host-assets-").FullName;
        var assets = new HostAssets(new Astra.Core.AstraPaths(home));

        var (hostJs, piJs) = assets.Ensure();

        Assert.Equal(Path.Combine(home, "plugins", "host", assets.Hash), Path.GetDirectoryName(hostJs));
        Assert.Equal(HostAssets.HostJs(), File.ReadAllBytes(hostJs));
        Assert.Equal(HostAssets.PiJs(), File.ReadAllBytes(piJs));
        Assert.Equal((hostJs, piJs), assets.Ensure());
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Astra.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }

    [GeneratedRegex(@"^\| (?<file>[a-z]+\.js) \| [0-9a-f]{40} \| (?<sha>[0-9a-f]{64}) \|$", RegexOptions.Multiline)]
    private static partial Regex Row();
}
