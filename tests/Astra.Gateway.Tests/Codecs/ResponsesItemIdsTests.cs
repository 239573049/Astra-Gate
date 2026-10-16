using System.Text.Json.Nodes;
using Astra.Gateway.Protocol;
using Astra.Gateway.Protocol.Responses;

namespace Astra.Gateway.Tests.Codecs;

/// <summary>GitHub Copilot's per-event item ids, pinned per output_index on a pass-through Responses stream.</summary>
public class ResponsesItemIdsTests
{
    private static IReadOnlyList<SseEvent> Fixture(string name) =>
        SseParser.ParseAll(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "responses", name)));

    /// <summary>The id each event naming output item <paramref name="outputIndex"/> carries, in order.</summary>
    private static List<string?> IdsOf(IEnumerable<JsonObject> events, int outputIndex) =>
        events.Where(e => e["output_index"]?.GetValue<int>() == outputIndex)
            .Select(e => e["item"] is JsonObject item ? item["id"]?.GetValue<string>() : e["item_id"]?.GetValue<string>())
            .ToList();

    [Fact]
    public void Every_Event_Of_An_Item_Carries_The_Id_Its_Added_Event_Had()
    {
        var pins = new ResponsesItemIds();
        var events = Fixture("stream-copilot-rotating-ids.sse").Select(e => JsonNode.Parse(pins.Pin(e.Data))!.AsObject()).ToList();

        Assert.Equal(Enumerable.Repeat<string?>("rs_enc_a1", 4), IdsOf(events, 0));
        Assert.Equal(Enumerable.Repeat<string?>("msg_enc_b1", 6), IdsOf(events, 1));
        Assert.Equal(Enumerable.Repeat<string?>("fc_enc_c1", 4), IdsOf(events, 2));
        // Only the ids change: text, call ids and sequence numbers stay as they came.
        Assert.Equal("这轮我先读实现。", events[7]["delta"]!.GetValue<string>());
        Assert.Equal("call_1", events[14]["item"]!["call_id"]!.GetValue<string>());
        Assert.Equal(Enumerable.Range(0, 16), events.Select(e => e["sequence_number"]!.GetValue<int>()));
    }

    [Fact]
    public void Response_Level_Events_Pass_As_They_Came()
    {
        var pins = new ResponsesItemIds();
        var events = Fixture("stream-copilot-rotating-ids.sse");
        var pinned = events.Select(e => pins.Pin(e.Data)).ToList();
        Assert.Same(events[0].Data, pinned[0]);   // response.created
        Assert.Same(events[^1].Data, pinned[^1]); // response.completed, its output ids untouched
        Assert.Same(events[1].Data, pinned[1]);   // an added event defines the id, so it is not rewritten
    }

    [Fact]
    public void A_Stream_With_Stable_Ids_Is_Returned_Unchanged()
    {
        var pins = new ResponsesItemIds();
        foreach (var sse in Fixture("stream-completed.sse"))
            Assert.Same(sse.Data, pins.Pin(sse.Data));
    }

    [Fact]
    public void An_Added_Item_Without_An_Id_Gets_One_That_Its_Later_Events_Share()
    {
        var pins = new ResponsesItemIds();
        var added = JsonNode.Parse(pins.Pin("""{"type":"response.output_item.added","output_index":3,"item":{"type":"message","role":"assistant","content":[]}}"""))!;
        var id = added["item"]!["id"]!.GetValue<string>();
        Assert.StartsWith("oi_3_", id);
        var delta = JsonNode.Parse(pins.Pin("""{"type":"response.output_text.delta","item_id":"x1","output_index":3,"content_index":0,"delta":"hi"}"""))!;
        var done = JsonNode.Parse(pins.Pin("""{"type":"response.output_item.done","output_index":3,"item":{"id":"x2","type":"message"}}"""))!;
        Assert.Equal(id, delta["item_id"]!.GetValue<string>());
        Assert.Equal(id, done["item"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void Only_The_Top_Level_Item_Id_Is_Replaced()
    {
        var pins = new ResponsesItemIds();
        pins.Pin("""{"type":"response.output_item.added","output_index":0,"item":{"id":"fc1","type":"function_call"}}""");
        var delta = JsonNode.Parse(pins.Pin("""{"type":"response.function_call_arguments.delta","output_index":0,"part":{"item_id":"nested"},"item_id":"x","delta":"a"}"""))!;
        Assert.Equal("fc1", delta["item_id"]!.GetValue<string>());
        Assert.Equal("nested", delta["part"]!["item_id"]!.GetValue<string>());
    }

    [Fact]
    public void Text_Before_The_Item_Id_Keeps_Its_Offsets()
    {
        var pins = new ResponsesItemIds();
        pins.Pin("""{"type":"response.output_item.added","output_index":2,"item":{"id":"msg_1","type":"message"}}""");
        // Non-ASCII and a surrogate pair (🙂) before the id: byte and string offsets differ here.
        var pinned = pins.Pin("""{"type":"response.output_text.delta","delta":"你好🙂","output_index":2,"item_id":"enc_9","content_index":0}""");
        var delta = JsonNode.Parse(pinned)!;
        Assert.Equal("msg_1", delta["item_id"]!.GetValue<string>());
        Assert.Equal("你好🙂", delta["delta"]!.GetValue<string>());
        Assert.Equal(0, delta["content_index"]!.GetValue<int>());
    }

    [Fact]
    public void An_Escaped_Item_Id_Takes_The_Tree_Path_And_Is_Replaced()
    {
        var pins = new ResponsesItemIds();
        pins.Pin("""{"type":"response.output_item.added","output_index":0,"item":{"id":"msg_1","type":"message"}}""");
        var delta = JsonNode.Parse(pins.Pin("""{"type":"response.output_text.delta","output_index":0,"item_id":"enc1","delta":"a"}"""))!;
        Assert.Equal("msg_1", delta["item_id"]!.GetValue<string>());
    }

    [Fact]
    public void A_Pinned_Id_That_Needs_Escaping_Is_Written_Escaped()
    {
        var pins = new ResponsesItemIds();
        pins.Pin("""{"type":"response.output_item.added","output_index":0,"item":{"id":"a\"b\\c","type":"message"}}""");
        var delta = JsonNode.Parse(pins.Pin("""{"type":"response.output_text.delta","output_index":0,"item_id":"enc","delta":"a"}"""))!;
        Assert.Equal("a\"b\\c", delta["item_id"]!.GetValue<string>());
    }

    [Fact]
    public void Events_For_An_Item_Never_Added_Or_Not_Json_Are_Left_Alone()
    {
        var pins = new ResponsesItemIds();
        const string orphan = """{"type":"response.output_text.delta","item_id":"x1","output_index":9,"delta":"hi"}""";
        const string notJson = "\"output_index\": not json";
        Assert.Same(orphan, pins.Pin(orphan));
        Assert.Same(notJson, pins.Pin(notJson));
    }
}
