using Astra.Core.Models;
using Astra.Gateway.Pipeline;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Astra.Gateway.Tests;

public class CopilotCliHeadersTests
{
    [Fact]
    public void Applies_only_to_the_copilot_subscription_template()
    {
        Assert.True(CopilotCliHeaders.Applies(new Provider { Id = "p", Name = "c", TemplateId = "github-copilot-subscription" }));
        Assert.False(CopilotCliHeaders.Applies(new Provider { Id = "p", Name = "o", TemplateId = "openai" }));
    }

    [Fact]
    public void Generates_one_request_id_that_seeds_the_interaction_and_task_ids()
    {
        using var request = new HttpRequestMessage();
        CopilotCliHeaders.Apply(request, new HeaderDictionary());
        var id = request.Headers.GetValues("X-Request-Id").Single();
        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal(id, request.Headers.GetValues("X-Interaction-Id").Single());
        Assert.Equal(id, request.Headers.GetValues("X-Agent-Task-Id").Single());
        Assert.Equal("conversation-agent", request.Headers.GetValues("OpenAI-Intent").Single());
        Assert.Equal("conversation-agent", request.Headers.GetValues("X-Interaction-Type").Single());
    }

    [Fact]
    public void A_client_value_wins_and_a_configured_one_beats_the_default()
    {
        using var request = new HttpRequestMessage();
        request.Headers.TryAddWithoutValidation("OpenAI-Intent", "conversation-edits");
        var client = new HeaderDictionary { ["X-Request-Id"] = "req-1", ["X-Interaction-Type"] = "conversation-subagent" };
        CopilotCliHeaders.Apply(request, client);
        Assert.Equal("req-1", request.Headers.GetValues("X-Interaction-Id").Single());
        Assert.Equal("conversation-subagent", request.Headers.GetValues("X-Interaction-Type").Single());
        Assert.Equal("conversation-edits", request.Headers.GetValues("OpenAI-Intent").Single());
    }
}
