using System.Text;
using Astra.Core;
using Astra.Gateway.Pipeline;

namespace Astra.Gateway.Tests;

public sealed class BodyStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "astra-bodies-" + Guid.NewGuid().ToString("N"));
    private readonly BodyStore _store;

    public BodyStoreTests() => _store = new BodyStore(new AstraPaths(_root));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Text_Is_Written_As_Utf8_Up_To_MaxBytes()
    {
        var small = "héllo 汉字 🙂";
        await _store.WriteAsync("2026-01-01/a", BodyStore.ClientResponse, small);
        Assert.Equal(small, _store.Read("2026-01-01/a", BodyStore.ClientResponse));

        // Larger than MaxBytes once encoded (3 bytes per character), spread over builder chunks.
        var builder = new StringBuilder();
        for (var i = 0; i < 400; i++) builder.Append(new string('汉', 8_000));
        await _store.WriteAsync("2026-01-01/a", BodyStore.UpstreamResponse, builder);
        var file = _store.FileFor("2026-01-01/a", BodyStore.UpstreamResponse)!;
        Assert.Equal(BodyStore.MaxBytes, new FileInfo(file).Length);
    }

    [Fact]
    public async Task Builder_Chunks_Are_Written_In_Order()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < 5_000; i++) builder.Append(i).Append(',');
        await _store.WriteAsync("2026-01-01/b", BodyStore.ClientResponse, builder);
        Assert.Equal(builder.ToString(), _store.Read("2026-01-01/b", BodyStore.ClientResponse));
    }

    [Theory]
    [InlineData("a")]   // 1 byte
    [InlineData("é")]   // 2 bytes
    [InlineData("汉")]  // 3 bytes
    [InlineData("🙂")]  // 4 bytes
    public async Task Preview_Cuts_At_A_Character_Boundary(string unit)
    {
        var text = string.Concat(Enumerable.Repeat(unit, 1_000));
        await _store.WriteAsync("2026-01-01/c", BodyStore.ClientRequest, text);
        var size = Encoding.UTF8.GetByteCount(text);
        for (var limit = 95; limit <= 101; limit++)
        {
            var preview = _store.ReadPreview("2026-01-01/c", BodyStore.ClientRequest, limit)!.Value;
            Assert.Equal(size, preview.Size);
            Assert.StartsWith(preview.Text, text, StringComparison.Ordinal);
            Assert.InRange(Encoding.UTF8.GetByteCount(preview.Text), limit - 3, limit);
            Assert.DoesNotContain('�', preview.Text);
        }
        Assert.Equal(text, _store.ReadPreview("2026-01-01/c", BodyStore.ClientRequest, size)!.Value.Text);
        Assert.Null(_store.ReadPreview("2026-01-01/c", BodyStore.UpstreamRequest, 100));
    }
}
