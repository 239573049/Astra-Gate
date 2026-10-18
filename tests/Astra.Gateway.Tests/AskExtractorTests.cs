using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Gateway.Routing;

namespace Astra.Gateway.Tests;

/// <summary>What the judge reads of a request (plan §5.5): the user's own words, in each client protocol.</summary>
public class AskExtractorTests
{
    private static JsonObject Body(string json) => JsonNode.Parse(json)!.AsObject();

    // A Claude Code style conversation: reminders injected into user messages, tool results as user messages.
    private const string ClaudeCode = """
        {"model":"astra-auto","tools":[{"name":"Bash"},{"name":"Read"}],"messages":[
          {"role":"user","content":[{"type":"text","text":"<system-reminder>\nAs you answer the user's questions, you can use these tools.\n</system-reminder>"},{"type":"text","text":"Add a login test"}]},
          {"role":"assistant","content":[{"type":"text","text":"Sure."},{"type":"tool_use","id":"t1","name":"Read","input":{}}]},
          {"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"file contents"}]},
          {"role":"assistant","content":"Done reading."},
          {"role":"user","content":[{"type":"text","text":"<system-reminder>x</system-reminder>"},{"type":"text","text":"Fix the flaky login test"}]},
          {"role":"assistant","content":[{"type":"tool_use","id":"t2","name":"Bash","input":{}}]},
          {"role":"user","content":[{"type":"tool_result","tool_use_id":"t2","content":"ok"}]}
        ]}
        """;

    // ---------------------------------------------------------------- Anthropic

    [Fact]
    public void Anthropic_Takes_The_Last_Real_User_Message_Not_The_Trailing_Tool_Result()
    {
        var ask = AskExtractor.Extract(ApiProtocol.Anthropic, Body(ClaudeCode), contextTurns: 2, maxChars: 6000)!;

        Assert.Equal("Fix the flaky login test", ask.Request);
        Assert.Equal(["Add a login test"], ask.Earlier);
        Assert.Equal(7, ask.Shape.Messages);
        Assert.Equal(2, ask.Shape.Tools);
        Assert.False(ask.Shape.HasImages);
    }

    [Fact]
    public void System_Reminder_Blocks_Are_Not_The_Users_Words()
    {
        var onlyReminder = Body("""{"messages":[{"role":"user","content":"real ask"},{"role":"user","content":[{"type":"text","text":"<system-reminder>noise</system-reminder>"}]}]}""");
        Assert.Equal("real ask", AskExtractor.Extract(ApiProtocol.Anthropic, onlyReminder, 0, 6000)!.Request);

        var inline = Body("""{"messages":[{"role":"user","content":"before <system-reminder>noise</system-reminder> after"}]}""");
        Assert.Equal("before  after", AskExtractor.Extract(ApiProtocol.Anthropic, inline, 0, 6000)!.Request);

        // An unterminated reminder is kept as written: it is more likely the user's text than ours to drop.
        var open = Body("""{"messages":[{"role":"user","content":"look at <system-reminder> tags"}]}""");
        Assert.Equal("look at <system-reminder> tags", AskExtractor.Extract(ApiProtocol.Anthropic, open, 0, 6000)!.Request);
    }

    [Fact]
    public void Context_Turns_Bounds_The_Earlier_Asks_And_Zero_Means_None()
    {
        var body = Body("""{"messages":[{"role":"user","content":"one"},{"role":"user","content":"two"},{"role":"user","content":"three"},{"role":"user","content":"four"}]}""");

        Assert.Empty(AskExtractor.Extract(ApiProtocol.Anthropic, body, 0, 6000)!.Earlier);
        Assert.Equal(["three"], AskExtractor.Extract(ApiProtocol.Anthropic, body, 1, 6000)!.Earlier);
        Assert.Equal(["two", "three"], AskExtractor.Extract(ApiProtocol.Anthropic, body, 2, 6000)!.Earlier); // oldest first
        Assert.Equal(["one", "two", "three"], AskExtractor.Extract(ApiProtocol.Anthropic, body, 5, 6000)!.Earlier); // fewer than asked for
    }

    [Fact]
    public void Images_Set_The_Flag_On_The_Current_Ask_Only()
    {
        var current = Body("""{"messages":[{"role":"user","content":"old"},{"role":"user","content":[{"type":"image","source":{}},{"type":"text","text":"what is this"}]}]}""");
        Assert.True(AskExtractor.Extract(ApiProtocol.Anthropic, current, 1, 6000)!.Shape.HasImages);

        var earlier = Body("""{"messages":[{"role":"user","content":[{"type":"image","source":{}},{"type":"text","text":"old"}]},{"role":"user","content":"now"}]}""");
        Assert.False(AskExtractor.Extract(ApiProtocol.Anthropic, earlier, 1, 6000)!.Shape.HasImages);
    }

    [Fact]
    public void Assistant_And_Tool_Content_Never_Reaches_The_Judge()
    {
        var ask = AskExtractor.Extract(ApiProtocol.Anthropic, Body(ClaudeCode), 5, 6000)!;
        var all = string.Join("\n", ask.Earlier.Append(ask.Request));
        Assert.DoesNotContain("Sure.", all);
        Assert.DoesNotContain("file contents", all);
        Assert.DoesNotContain("Done reading", all);
        Assert.DoesNotContain("system-reminder", all);
    }

    // ---------------------------------------------------------------- OpenAI Chat

    [Fact]
    public void Chat_Reads_String_And_Part_Content_And_Skips_System_Assistant_And_Tool_Messages()
    {
        var body = Body("""
            {"model":"astra-auto","tools":[{}],"messages":[
              {"role":"system","content":"You are helpful."},
              {"role":"user","content":"First question"},
              {"role":"assistant","content":"First answer"},
              {"role":"user","content":[{"type":"text","text":"Second"},{"type":"image_url","image_url":{"url":"data:image/png;base64,AAAA"}}]},
              {"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"f","arguments":"{}"}}]},
              {"role":"tool","tool_call_id":"c1","content":"result"}
            ]}
            """);
        var ask = AskExtractor.Extract(ApiProtocol.OpenAIChat, body, 2, 6000)!;

        Assert.Equal("Second", ask.Request);
        Assert.Equal(["First question"], ask.Earlier);
        Assert.True(ask.Shape.HasImages);
        Assert.Equal(6, ask.Shape.Messages);
        Assert.Equal(1, ask.Shape.Tools);
    }

    // ---------------------------------------------------------------- OpenAI Responses

    [Fact]
    public void Responses_Accepts_A_Plain_String_Input()
    {
        var ask = AskExtractor.Extract(ApiProtocol.OpenAIResponses, Body("""{"model":"astra-auto","input":"hello there"}"""), 2, 6000)!;
        Assert.Equal("hello there", ask.Request);
        Assert.Equal(1, ask.Shape.Messages);
    }

    [Fact]
    public void Responses_Reads_Input_Items_And_Skips_Function_Calls_And_Their_Outputs()
    {
        var body = Body("""
            {"input":[
              {"type":"message","role":"user","content":[{"type":"input_text","text":"Plan the migration"}]},
              {"type":"function_call","call_id":"c1","name":"x","arguments":"{}"},
              {"type":"function_call_output","call_id":"c1","output":"done"},
              {"type":"reasoning","summary":[]},
              {"type":"message","role":"assistant","content":[{"type":"output_text","text":"Okay"}]}
            ]}
            """);
        var ask = AskExtractor.Extract(ApiProtocol.OpenAIResponses, body, 2, 6000)!;
        Assert.Equal("Plan the migration", ask.Request);
        Assert.Empty(ask.Earlier);
        Assert.Equal(5, ask.Shape.Messages);
    }

    [Fact]
    public void Responses_Accepts_The_Short_Message_Form_Without_A_Type()
    {
        var ask = AskExtractor.Extract(ApiProtocol.OpenAIResponses, Body("""{"input":[{"role":"user","content":"short form"}]}"""), 0, 6000)!;
        Assert.Equal("short form", ask.Request);
    }

    // ---------------------------------------------------------------- Gemini

    [Fact]
    public void Gemini_Reads_Text_Parts_And_Skips_Model_Turns_And_Function_Responses()
    {
        var body = Body("""
            {"tools":[{"functionDeclarations":[{"name":"f"}]}],"contents":[
              {"role":"user","parts":[{"text":"Summarise this"},{"inlineData":{"mimeType":"image/png","data":"AAAA"}}]},
              {"role":"model","parts":[{"text":"Sure"}]},
              {"role":"user","parts":[{"functionResponse":{"name":"f","response":{}}}]}
            ]}
            """);
        var ask = AskExtractor.Extract(ApiProtocol.Gemini, body, 2, 6000)!;
        Assert.Equal("Summarise this", ask.Request);
        Assert.True(ask.Shape.HasImages);
        Assert.Equal(3, ask.Shape.Messages);
        Assert.Equal(1, ask.Shape.Tools);
    }

    [Fact]
    public void Gemini_Treats_A_Role_Less_Content_As_The_Users()
    {
        var ask = AskExtractor.Extract(ApiProtocol.Gemini, Body("""{"contents":[{"parts":[{"text":"no role here"}]}]}"""), 0, 6000)!;
        Assert.Equal("no role here", ask.Request);
    }

    // ---------------------------------------------------------------- nothing to read

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"messages":"nope"}""")]
    [InlineData("""{"messages":[1,2,3]}""")]
    [InlineData("""{"messages":[{"role":"assistant","content":"only me"}]}""")]
    [InlineData("""{"messages":[{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":"x"}]}]}""")]
    [InlineData("""{"messages":[{"role":"user","content":"   "}]}""")]
    [InlineData("""{"messages":[{"role":"user","content":{"unexpected":"object"}}]}""")]
    public void A_Body_Without_User_Text_Gives_Nothing_And_Never_Throws(string json)
    {
        Assert.Null(AskExtractor.Extract(ApiProtocol.Anthropic, Body(json), 2, 6000));
        Assert.Null(AskExtractor.Extract(ApiProtocol.OpenAIChat, Body(json), 2, 6000));
    }

    [Fact]
    public void Nodes_Of_The_Wrong_Kind_Are_Tolerated()
    {
        Assert.Null(AskExtractor.Extract(ApiProtocol.OpenAIResponses, Body("""{"input":42}"""), 2, 6000));
        Assert.Null(AskExtractor.Extract(ApiProtocol.Gemini, Body("""{"contents":[{"role":"user","parts":"text"}]}"""), 2, 6000));
        Assert.Null(AskExtractor.Extract(ApiProtocol.Anthropic, Body("""{"messages":[{"role":7,"content":"x"}]}"""), 2, 6000));
    }

    // ---------------------------------------------------------------- budget

    [Fact]
    public void A_Long_Ask_Keeps_Its_Start_And_Its_End()
    {
        var text = "BEGIN" + new string('m', 10_000) + "END";
        var ask = AskExtractor.Extract(ApiProtocol.Anthropic, Body($$"""{"messages":[{"role":"user","content":"{{text}}"}]}"""), 0, 1000)!;

        Assert.Equal(1000, ask.Request.Length);
        Assert.StartsWith("BEGIN", ask.Request);
        Assert.EndsWith("END", ask.Request);
        Assert.Contains("…", ask.Request);
    }

    [Fact]
    public void The_Budget_Is_Shared_And_The_Current_Ask_Keeps_At_Least_Two_Thirds_Of_It()
    {
        var long1 = new string('a', 5000);
        var long2 = new string('b', 5000);
        var current = new string('c', 5000);
        var ask = AskExtractor.Extract(ApiProtocol.Anthropic,
            Body($$"""{"messages":[{"role":"user","content":"{{long1}}"},{"role":"user","content":"{{long2}}"},{"role":"user","content":"{{current}}"}]}"""), 2, 1200)!;

        Assert.Equal(2, ask.Earlier.Count);
        Assert.All(ask.Earlier, e => Assert.Equal(200, e.Length)); // 1200 / 3 / 2
        Assert.Equal(800, ask.Request.Length);
        Assert.Equal(1200, ask.Earlier.Sum(e => e.Length) + ask.Request.Length);
    }

    [Fact]
    public void A_Cut_Never_Splits_A_Surrogate_Pair()
    {
        var text = string.Concat(Enumerable.Repeat("😀", 2000));
        var ask = AskExtractor.Extract(ApiProtocol.Anthropic, Body($$"""{"messages":[{"role":"user","content":"{{text}}"}]}"""), 0, 999)!;

        for (var i = 0; i < ask.Request.Length; i++)
        {
            var c = ask.Request[i];
            if (char.IsHighSurrogate(c)) Assert.True(i + 1 < ask.Request.Length && char.IsLowSurrogate(ask.Request[i + 1]), $"lone high surrogate at {i}");
            if (char.IsLowSurrogate(c)) Assert.True(i > 0 && char.IsHighSurrogate(ask.Request[i - 1]), $"lone low surrogate at {i}");
        }
    }

    [Fact]
    public void A_Short_Ask_Is_Not_Touched()
    {
        var ask = AskExtractor.Extract(ApiProtocol.Anthropic, Body("""{"messages":[{"role":"user","content":"  修复登录测试 🚀  "}]}"""), 0, 1000)!;
        Assert.Equal("修复登录测试 🚀", ask.Request);
    }
}
