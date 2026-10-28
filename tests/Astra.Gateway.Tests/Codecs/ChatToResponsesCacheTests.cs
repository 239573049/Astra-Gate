using System.Text.Json.Nodes;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Protocol;
using Astra.Gateway.Protocol.Chat;
using Astra.Gateway.Protocol.Responses;

namespace Astra.Gateway.Tests.Codecs;

/// <summary>
/// A Chat request translated to Responses (GitHub Copilot's GPT models are Responses-only) keeps what the upstream
/// prompt cache depends on: the client's <c>prompt_cache_key</c>, and instructions that stay the same from turn to turn.
/// </summary>
public class ChatToResponsesCacheTests
{
    private static readonly ChatCodec Chat = new();
    private static readonly ResponsesCodec Responses = new();

    private static UnifiedRequest Decode(string json) => Chat.DecodeRequest((JsonObject)JsonNode.Parse(json)!, new RequestDecodeContext());

    private static JsonObject ToResponses(string json) =>
        Responses.EncodeRequest(Decode(json), new RequestEncodeContext { UpstreamModel = "gpt-5" });

    private static string Turn(string reminder, string tail = "") =>
        $$"""
        {
          "model": "gpt-5",
          "messages": [
            { "role": "system", "content": "You are an agent." },
            { "role": "user", "content": "Fix the bug." },
            { "role": "assistant", "content": "Looking." }{{tail}},
            { "role": "system", "content": "{{reminder}}" },
            { "role": "user", "content": "Go on." }
          ]
        }
        """;

    [Fact]
    public void Prompt_cache_key_crosses_between_chat_and_responses()
    {
        var upstream = ToResponses("""{"model":"gpt-5","messages":[{"role":"user","content":"hi"}],"prompt_cache_key":"conv-1"}""");
        Assert.Equal("conv-1", upstream["prompt_cache_key"]!.GetValue<string>());

        var ir = Responses.DecodeRequest((JsonObject)JsonNode.Parse("""{"model":"gpt-5","input":"hi","prompt_cache_key":"conv-2"}""")!, new RequestDecodeContext());
        var chat = Chat.EncodeRequest(ir, new RequestEncodeContext { UpstreamModel = "gpt-5" });
        Assert.Equal("conv-2", chat["prompt_cache_key"]!.GetValue<string>());
    }

    [Fact]
    public void Late_system_message_stays_in_place_as_a_developer_item()
    {
        var upstream = ToResponses(Turn("Time is 10:00."));

        Assert.Equal("You are an agent.", upstream["instructions"]!.GetValue<string>());
        var input = upstream["input"]!.AsArray();
        Assert.Equal(["user", "assistant", "developer", "user"], input.Select(i => i!["role"]!.GetValue<string>()));
        Assert.Equal("Time is 10:00.", input[2]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Late_system_message_at_the_end_is_the_last_item()
    {
        var upstream = ToResponses("""
            {"model":"gpt-5","messages":[{"role":"user","content":"hi"},{"role":"developer","content":"Be brief."}]}
            """);

        Assert.Null(upstream["instructions"]);
        var input = upstream["input"]!.AsArray();
        Assert.Equal(["user", "developer"], input.Select(i => i!["role"]!.GetValue<string>()));
    }

    [Fact]
    public void Other_encoders_still_get_late_instructions_with_the_system_prompt()
    {
        var ir = Decode(Turn("Time is 10:00."));
        Assert.Equal(["You are an agent.", "Time is 10:00."], ir.System.OfType<TextPart>().Select(p => p.Text));
        var chat = Chat.EncodeRequest(ir, new RequestEncodeContext { UpstreamModel = "gpt-5" });
        Assert.Equal("You are an agent.\n\nTime is 10:00.", chat["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Derived_key_is_the_same_for_every_turn_of_one_conversation()
    {
        var first = PromptCacheKeys.Derive(Decode(Turn("Time is 10:00.")));
        var later = PromptCacheKeys.Derive(Decode(Turn("Time is 10:05.", """, { "role": "user", "content": "More." }""")));
        var other = PromptCacheKeys.Derive(Decode(Turn("Time is 10:00.").Replace("Fix the bug.", "Write docs.")));

        Assert.NotNull(first);
        Assert.StartsWith("astra-", first);
        Assert.Equal(first, later);
        Assert.NotEqual(first, other);
        Assert.Null(PromptCacheKeys.Derive(new UnifiedRequest { Model = "gpt-5" }));
    }
}
