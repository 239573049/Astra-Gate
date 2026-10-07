using Microsoft.Extensions.DependencyInjection;

namespace Astra.Gateway.Protocol;

/// <summary>Registers the protocol codecs (one IProtocolCodec singleton per wire protocol).</summary>
public static class GatewayCodecs
{
    public static IServiceCollection Register(IServiceCollection services)
    {
        services.AddSingleton<IProtocolCodec, Chat.ChatCodec>();
        services.AddSingleton<IProtocolCodec, Anthropic.AnthropicCodec>();
        services.AddSingleton<IProtocolCodec, Responses.ResponsesCodec>();
        services.AddSingleton<IProtocolCodec, Gemini.GeminiCodec>();
        return services;
    }
}
