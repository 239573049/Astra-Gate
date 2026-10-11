namespace Astra.Plugins.Middleware;

/// <summary>
/// Splits a server-sent event stream into events the way magpie's gateway middleware does
/// (internal/middleware/run.go eventEnd / splitEvent), so a middleware sees exactly the events it sees
/// in magpie.
/// </summary>
public static class SseEventFramer
{
    /// <summary>Where the first complete event in <paramref name="b"/> ends (the blank line's start), and
    /// that blank line's length; (-1, 0) when no event is complete yet.</summary>
    public static (int Index, int Length) EventEnd(ReadOnlySpan<byte> b)
    {
        var i = b.IndexOf("\n\n"u8);
        var j = b.IndexOf("\r\n\r\n"u8);
        if (i < 0 && j < 0) return (-1, 0);
        return j >= 0 && (i < 0 || j < i) ? (j, 4) : (i, 2);
    }

    /// <summary>
    /// The one data line of an event that carries a JSON object: the event's name (from <c>event:</c>,
    /// "" when it has none), and where the JSON starts and ends in <paramref name="ev"/>. False when the
    /// event has no data line, more than one, or data that isn't an object (<c>data: [DONE]</c>) — such
    /// an event passes as it is.
    /// </summary>
    public static bool TrySplit(ReadOnlySpan<byte> ev, out string name, out int dataStart, out int dataEnd)
    {
        name = "";
        dataStart = dataEnd = 0;
        var lineStart = -1;
        var lineLength = 0;
        var off = 0;
        var rest = ev;
        while (rest.Length > 0)
        {
            var k = rest.IndexOf((byte)'\n');
            var l = k >= 0 ? rest[..(k + 1)] : rest;
            rest = rest[l.Length..];
            var t = l.TrimEnd("\r\n"u8);
            if (t.StartsWith("event:"u8))
            {
                name = System.Text.Encoding.UTF8.GetString(t[6..]).Trim();
            }
            else if (t.StartsWith("data:"u8))
            {
                if (lineStart >= 0) return false; // data over more than one line
                lineStart = off;
                lineLength = t.Length;
            }
            off += l.Length;
        }
        if (lineStart < 0) return false;
        var line = ev.Slice(lineStart, lineLength);
        var start = line.Length > 5 && line[5] == (byte)' ' ? 6 : 5;
        // an event's JSON is an object in every API; data: [DONE] isn't JSON
        if (line.Length <= start || line[start] != (byte)'{') return false;
        dataStart = lineStart + start;
        dataEnd = lineStart + lineLength;
        return true;
    }
}
