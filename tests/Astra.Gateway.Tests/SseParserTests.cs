using System.Text;
using Astra.Gateway.Protocol;

namespace Astra.Gateway.Tests;

public class SseParserTests
{
    // CRLF / CR / LF endings, a comment, a field without its space, multi-line data, multi-byte UTF-8,
    // and a last event with no blank line after it.
    private const string Document =
        "event: a\r\ndata: 你好\r\ndata: 🙂\r\n\r\n" +
        ": ping\n\n" +
        "data:x\rdata: y\r\r" +
        "id: 7\nretry: 10\ndata: {\"k\":1}\n\n" +
        "event: tail\ndata: last";

    private static readonly SseEvent[] Expected =
    [
        new("a", "你好\n🙂"),
        new(null, "x\ny"),
        new(null, "{\"k\":1}"),
        new("tail", "last"),
    ];

    [Fact]
    public void Parses_A_Whole_Document()
    {
        Assert.Equal(Expected, SseParser.ParseAll(Document));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public void Chunk_Boundaries_Anywhere_Give_The_Same_Events(int chunk)
    {
        // Splits fall inside CRLF pairs, multi-byte characters and field names.
        var bytes = Encoding.UTF8.GetBytes(Document);
        var parser = new SseParser();
        var events = new List<SseEvent>();
        for (var i = 0; i < bytes.Length; i += chunk)
            events.AddRange(parser.Feed(bytes.AsSpan(i, Math.Min(chunk, bytes.Length - i))));
        events.AddRange(parser.Flush());
        Assert.Equal(Expected, events);
    }

    [Fact]
    public void A_Line_Longer_Than_The_Buffers_Survives()
    {
        var data = new string('é', 50_000);
        var bytes = Encoding.UTF8.GetBytes($"data: {data}\n\n");
        var parser = new SseParser();
        var events = new List<SseEvent>();
        for (var i = 0; i < bytes.Length; i += 4096)
            events.AddRange(parser.Feed(bytes.AsSpan(i, Math.Min(4096, bytes.Length - i))));
        Assert.Equal(data, Assert.Single(events).Data);
    }

    [Fact]
    public void Writer_Round_Trips_Multi_Line_Data()
    {
        var frame = new SseEvent("e", "one\n\ntwo\n");
        Assert.Equal("event: e\ndata: one\ndata: \ndata: two\ndata: \n\n", SseWriter.Format(frame));
        Assert.Equal([frame], SseParser.ParseAll(SseWriter.Format(frame)));
    }
}
