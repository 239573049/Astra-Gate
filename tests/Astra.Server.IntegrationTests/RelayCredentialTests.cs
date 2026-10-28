using Astra.Gateway.Pipeline;
using Astra.Server.Relay;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Astra.Server.IntegrationTests;

/// <summary>Which credential a relay request carries, and what the relay may be started with.</summary>
public sealed class RelayCredentialTests
{
    [Theory]
    [InlineData("api-token", "Bearer other", "api-token")]
    [InlineData("api-token", "invalid authorization", "api-token")]
    [InlineData("", "Bearer bearer-token", "bearer-token")]
    [InlineData(null, "bearer bearer-token", "bearer-token")]
    [InlineData(" api-token ", null, "api-token")]
    [InlineData(null, "Bearer tid=test;exp=123;sig=abc", "tid=test;exp=123;sig=abc")]
    public void Credential_precedence_is_explicit(string? apiKey, string? authorization, string expected)
    {
        var headers = new HeaderDictionary { ["x-api-key"] = apiKey, ["Authorization"] = authorization };
        Assert.Equal(expected, RelayCaller.ReadCredential(headers));
    }

    [Fact]
    public void A_gemini_key_header_comes_after_x_api_key_and_before_authorization()
    {
        Assert.Equal("goog", RelayCaller.ReadCredential(new HeaderDictionary { ["x-goog-api-key"] = "goog", ["Authorization"] = "Bearer other" }));
        Assert.Equal("key", RelayCaller.ReadCredential(new HeaderDictionary { ["x-api-key"] = "key", ["x-goog-api-key"] = "goog" }));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", "Bearer valid")]
    [InlineData("has space", "Bearer valid")]
    [InlineData("one,two", null)]
    [InlineData(null, "Basic valid")]
    [InlineData(null, "Bearer")]
    [InlineData(null, "Bearer one,two")]
    [InlineData(null, "Bearer one two")]
    public void A_malformed_chosen_credential_is_refused(string? apiKey, string? authorization)
    {
        var error = Assert.Throws<GatewayException>(() =>
            RelayCaller.ReadCredential(new HeaderDictionary { ["x-api-key"] = apiKey, ["Authorization"] = authorization }));
        Assert.Equal(401, error.Status);
    }

    [Fact]
    public void Duplicate_and_non_ascii_credentials_are_refused()
    {
        foreach (var key in new[] { "x-api-key", "x-goog-api-key", "Authorization" })
        {
            var headers = new HeaderDictionary { [key] = new StringValues(["one", "two"]) };
            Assert.Throws<GatewayException>(() => RelayCaller.ReadCredential(headers));
        }
        foreach (var token in new[] { new string('a', 4097), "abcé", "abc\tdef", "abc\ndef" })
            Assert.Throws<GatewayException>(() => RelayCaller.ReadCredential(new HeaderDictionary { ["x-api-key"] = token }));
        var chosen = new HeaderDictionary { ["x-api-key"] = "valid", ["Authorization"] = new StringValues(["bad", "bad"]) };
        Assert.Equal("valid", RelayCaller.ReadCredential(chosen));
    }

    [Theory]
    [InlineData("https://proxy.example.invalid", true)]
    [InlineData("http://127.0.0.1:4142", true)]
    [InlineData("http://localhost:4142", true)]
    [InlineData("http://remote.example.invalid", false)]
    [InlineData("https://user:password@example.invalid", false)]
    [InlineData("https://example.invalid/path", false)]
    [InlineData("https://example.invalid?key=value", false)]
    [InlineData("https://example.invalid/#fragment", false)]
    [InlineData("file:///example", false)]
    public void A_base_url_is_an_origin_without_paths_or_credentials(string origin, bool valid)
    {
        string? Env(string name) => name switch
        {
            "ASTRA_RELAY_PROVIDER" => "openai",
            "ASTRA_RELAY_BASE_URL" => origin,
            _ => null,
        };
        if (valid) Assert.Equal(origin, RelayOptions.Load([], Env).BaseUrl);
        else Assert.Throws<ArgumentException>(() => RelayOptions.Load([], Env));
    }

    [Fact]
    public void Flags_win_over_the_environment_and_limits_come_from_it()
    {
        string? Env(string name) => name switch
        {
            "ASTRA_RELAY_PROVIDER" => "openai",
            "ASTRA_HOST" => "0.0.0.0",
            "ASTRA_PORT" => "4141",
            "ASTRA_RELAY_MAX_CONCURRENT_REQUESTS" => "8",
            "ASTRA_RELAY_MODEL_CACHE_SECONDS" => "60",
            _ => null,
        };
        var options = RelayOptions.Load(["--provider", "anthropic", "--port", "4242"], Env);
        Assert.Equal(("anthropic", "0.0.0.0", 4242, 8, 60),
            (options.Provider, options.Host, options.Port, options.MaxConcurrentRequests, options.ModelCacheSeconds));
    }

    [Theory]
    [InlineData("ASTRA_RELAY_MAX_CONCURRENT_REQUESTS", "0")]
    [InlineData("ASTRA_RELAY_MAX_REQUEST_BODY_BYTES", "1024")]
    [InlineData("ASTRA_RELAY_MAX_REQUEST_BODY_BYTES", "67108865")]
    [InlineData("ASTRA_RELAY_HEADERS_TIMEOUT_SECONDS", "0")]
    [InlineData("ASTRA_RELAY_MODEL_CACHE_ENTRIES", "5000")]
    [InlineData("ASTRA_PORT", "70000")]
    [InlineData("ASTRA_HOST", "not-a-host")]
    public void Out_of_range_settings_stop_the_relay_at_startup(string name, string value)
    {
        Assert.Throws<ArgumentException>(() => RelayOptions.Load([], n => n == name ? value : n == "ASTRA_RELAY_PROVIDER" ? "openai" : null));
    }

    [Theory]
    [InlineData("no-such-template", null)]
    [InlineData("ollama", null)]
    [InlineData("moonshot", "no-such-variant")]
    public void A_template_the_relay_cannot_serve_stops_it_at_startup(string provider, string? variant)
    {
        Assert.Throws<ArgumentException>(() => RelayApp.Create(new RelayOptions { Provider = provider, Variant = variant }));
    }
}
