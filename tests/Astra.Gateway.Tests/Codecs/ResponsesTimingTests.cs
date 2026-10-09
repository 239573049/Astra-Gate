using System.Text.Json.Nodes;
using Astra.Gateway.Protocol;
using Astra.Gateway.Protocol.Responses;

namespace Astra.Gateway.Tests.Codecs;

public class ResponsesTimingTests
{
    private static IReadOnlyList<SseEvent> Frames() => SseParser.ParseAll(File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "responses", "stream-custom-tool.sse")));

    [Fact]
    public void Custom_Tool_Stream_Reports_Activity_Once_At_The_Item_Start()
    {
        var decoder = new ResponsesCodec().CreateResponseDecoder(new ResponseDecodeContext { Stream = true });
        var activityAt = new List<string?>();
        foreach (var frame in Frames())
        {
            var events = decoder.DecodeSse(frame).ToList();
            if (events.OfType<OutputActivityEvent>().Any()) activityAt.Add(frame.Event);
            // Free-form tool input must not be relabeled as portable function-call JSON or assistant text.
            Assert.Empty(events.OfType<ToolArgsDeltaEvent>());
            Assert.Empty(events.OfType<TextDeltaEvent>());
        }
        Assert.Equal(["response.output_item.added"], activityAt);
        Assert.Empty(decoder.Complete().OfType<OutputActivityEvent>());
    }

    [Theory]
    [InlineData("response.custom_tool_call_input.delta")]
    [InlineData("response.custom_tool_call_input.done")]
    [InlineData("response.output_item.done")]
    [InlineData("response.completed")]
    public void Custom_Tool_Activity_Is_Detected_When_Earlier_Events_Are_Absent(string eventName)
    {
        var decoder = new ResponsesCodec().CreateResponseDecoder(new ResponseDecodeContext { Stream = true });
        var frame = Frames().First(f => f.Event == eventName);
        Assert.Single(decoder.DecodeSse(frame).OfType<OutputActivityEvent>());
    }

    [Fact]
    public void Custom_Tool_Json_Reports_Activity_Without_Changing_The_Final_Response()
    {
        var response = JsonNode.Parse(Frames()[^1].Data)!["response"]!.AsObject();
        var decoder = new ResponsesCodec().CreateResponseDecoder(new ResponseDecodeContext { Stream = false });
        var events = decoder.DecodeJson(response).ToList();
        Assert.Single(events.OfType<OutputActivityEvent>());
        Assert.True(JsonNode.DeepEquals(response, decoder.FinalResponse));
    }

    [Fact]
    public void Lifecycle_Usage_And_Empty_Custom_Deltas_Do_Not_Claim_Output_Activity()
    {
        var decoder = new ResponsesCodec().CreateResponseDecoder(new ResponseDecodeContext { Stream = true });
        Assert.Empty(decoder.DecodeSse(Frames()[0]).OfType<OutputActivityEvent>());
        Assert.Empty(decoder.DecodeSse(new SseEvent("response.custom_tool_call_input.delta",
            """{"type":"response.custom_tool_call_input.delta","output_index":0,"item_id":"ctc_1","delta":""}""")).OfType<OutputActivityEvent>());
        var terminal = JsonNode.Parse(Frames()[^1].Data)!.AsObject();
        terminal["response"]!["output"] = new JsonArray();
        Assert.Empty(decoder.DecodeSse(new SseEvent("response.completed", terminal.ToJsonString())).OfType<OutputActivityEvent>());
    }
}
