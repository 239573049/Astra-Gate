using Astra.Plugins.Diagnostics;

namespace Astra.Plugins.Tests;

/// <summary>The checks astra-server plugin-selftest runs on each AOT binary, here under the JIT.</summary>
public class PluginSelfTestTests
{
    [Fact]
    public void Every_check_passes()
    {
        var failed = PluginSelfTest.Run().Where(r => !r.Ok).Select(r => $"{r.Name}: {r.Detail}").ToList();
        Assert.Empty(failed);
    }
}
