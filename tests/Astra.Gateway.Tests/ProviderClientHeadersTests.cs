using System.Text.Json.Nodes;
using Astra.Core.Models;
using Astra.Gateway.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Astra.Gateway.Tests;

public class ProviderClientHeadersTests
{
    private static Provider WithClientHeaders(string? templateId, params string[] names) => new()
    {
        Id = "p",
        Name = "p",
        TemplateId = templateId,
        Settings = new JsonObject { [ProviderClientHeaders.SettingsKey] = new JsonArray(names.Select(n => (JsonNode)n).ToArray()) },
    };

    [Fact]
    public void Listed_client_headers_pass_through_and_are_never_invented()
    {
        using var request = new HttpRequestMessage();
        var client = new HeaderDictionary { ["X-Initiator"] = "agent", ["X-Other"] = "1" };
        ProviderClientHeaders.Forward(WithClientHeaders("openai", "X-Initiator", "X-Vision"), request, client);
        Assert.Equal("agent", request.Headers.GetValues("X-Initiator").Single());
        Assert.False(request.Headers.Contains("X-Vision"));
        Assert.False(request.Headers.Contains("X-Other"));
    }

    [Fact]
    public void A_client_value_replaces_a_configured_one()
    {
        using var request = new HttpRequestMessage();
        request.Headers.TryAddWithoutValidation("X-Initiator", "user");
        ProviderClientHeaders.Forward(WithClientHeaders("openai", "X-Initiator"), request, new HeaderDictionary { ["X-Initiator"] = "agent" });
        Assert.Equal("agent", request.Headers.GetValues("X-Initiator").Single());
    }

    [Fact]
    public void Without_the_setting_nothing_is_passed_through()
    {
        using var request = new HttpRequestMessage();
        ProviderClientHeaders.Apply(new Provider { Id = "p", Name = "p", TemplateId = "openai" }, request,
            new HeaderDictionary { ["X-Initiator"] = "agent" });
        Assert.Empty(request.Headers);
    }

    [Fact]
    public void Apply_adds_the_per_request_headers_of_the_providers_own_client_and_forward_does_not()
    {
        var provider = WithClientHeaders("github-copilot-subscription");
        using var full = new HttpRequestMessage();
        ProviderClientHeaders.Apply(provider, full, new HeaderDictionary());
        Assert.True(full.Headers.Contains("X-Request-Id"));
        using var listing = new HttpRequestMessage();
        ProviderClientHeaders.Forward(provider, listing, new HeaderDictionary());
        Assert.False(listing.Headers.Contains("X-Request-Id"));
    }
}
