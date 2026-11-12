using System.Text.Json.Nodes;
using Astra.Plugins.V2.Ui;

namespace Astra.Plugins.Tests.V2;

/// <summary>A plugin in a folder (dev) reloads on save and its open views carry on with their state (design/plugin-v2 06 §2).</summary>
public class PluginV2DevReloadTests
{
    private static string Source(string label) => $$"""
        import { ui } from 'astra';
        export const counter = ui.defineView({
          init() { return { n: 0 }; },
          render(s) { return ui.Stack({}, [ui.Text({ key: 't', value: '{{label}} ' + s.n }), ui.Button({ key: 'b', label: '+', onPress: 'inc' })]); },
          actions: { inc(s) { s.n += 1; } },
        });
        """;

    [Fact]
    public async Task Saving_the_view_patches_the_open_session_and_keeps_its_state()
    {
        await using var t = new V2TestHost();
        var manifest = new JsonObject
        {
            ["main"] = "main.js", ["permissions"] = new JsonArray("ui"),
            ["contributes"] = new JsonObject { ["ui.pages"] = new JsonArray(new JsonObject { ["id"] = "counter", ["title"] = "C" }) },
        };
        var dir = await t.AddAsync("acme.dev", manifest, [("main.js", Source("count"))]);
        var open = await t.Host.Ui.OpenAsync(new UiOpenRequest("acme.dev", "counter", null, V2TestHost.Client()));
        var stream = t.Host.Ui.Subscribe(open.SessionId, open.Rev);
        await t.Host.Ui.EventAsync(open.SessionId, "inc", null, null, seq: 1);
        await stream.ReadAsync(); // the action's patch: "count 1"

        await File.WriteAllTextAsync(Path.Combine(dir, "main.js"), Source("total"));
        File.SetLastWriteTimeUtc(Path.Combine(dir, "main.js"), DateTime.UtcNow.AddSeconds(5));

        var patch = await stream.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("patch", patch["type"]!.GetValue<string>());
        Assert.Contains("total 1", patch["ops"]!.ToJsonString());
        Assert.Equal(1, t.Host.Ui.Snapshot(open.SessionId).State!["n"]!.GetValue<int>());
        Assert.Contains(t.Services.Lines, l => l.Contains("reloaded"));
    }
}
