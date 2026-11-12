using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Astra.Plugins.Tests.V2;

/// <summary>Streamed replies (http.fetch with stream: true, design/plugin-v2 2.1): chunks, whole lines, cancel, the limits.</summary>
public class PluginV2StreamTests
{
    private const string Main = """
        import { definePlugin, commands, http } from 'astra';
        export default definePlugin({
          activate() {
            commands.register('lines', async () => {
              const res = await http.fetch('https://api.acme.example/sse', { stream: true });
              const out = [];
              for await (const line of res.lines()) if (line) out.push(line);
              return { status: res.status, type: res.headers['content-type'], out };
            });
            commands.register('text', async () => (await http.fetch('https://api.acme.example/sse', { stream: true })).text());
            commands.register('cancel', async () => {
              const res = await http.fetch('https://api.acme.example/sse', { stream: true });
              await res.cancel();
              try { await res.stream()[Symbol.asyncIterator]().next(); return 'done after cancel'; } catch (e) { return e.code; }
            });
          },
        });
        """;

    // a body that arrives in pieces: the second line's "é" is split between two chunks
    private sealed class Chunked(byte[][] chunks) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            foreach (var c in chunks)
            {
                await stream.WriteAsync(c);
                await stream.FlushAsync();
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private static async Task<V2TestHost> HostAsync()
    {
        var t = new V2TestHost();
        var e = Encoding.UTF8.GetBytes("data: é\n");
        t.Services.Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new Chunked([Encoding.UTF8.GetBytes("data: a\r\n\r\n"), e[..7], e[7..], Encoding.UTF8.GetBytes("\ndata: [DONE]")]),
        };
        await t.AddAsync("acme.stream", new JsonObject
        {
            ["main"] = "main.js", ["permissions"] = new JsonArray("net:https://api.acme.example"),
            ["contributes"] = new JsonObject { ["commands"] = new JsonArray(new JsonObject { ["id"] = "lines", ["title"] = "L" }, new JsonObject { ["id"] = "text", ["title"] = "T" }, new JsonObject { ["id"] = "cancel", ["title"] = "C" }) },
        }, [("main.js", Main)]);
        return t;
    }

    [Fact]
    public async Task Lines_come_out_whole_across_chunks()
    {
        await using var t = await HostAsync();
        var r = (await t.Host.ExecuteCommandAsync("acme.stream:lines", null, null, CancellationToken.None))!;
        Assert.Equal(200, r["status"]!.GetValue<int>());
        Assert.Equal(["data: a", "data: é", "data: [DONE]"], r["out"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public async Task Text_drains_the_stream_and_cancel_closes_it()
    {
        await using var t = await HostAsync();
        Assert.Equal("data: a\r\n\r\ndata: é\n\ndata: [DONE]", (await t.Host.ExecuteCommandAsync("acme.stream:text", null, null, CancellationToken.None))!.GetValue<string>());
        Assert.Equal("done after cancel", (await t.Host.ExecuteCommandAsync("acme.stream:cancel", null, null, CancellationToken.None))!.GetValue<string>());
    }
}
