using System.Text.Json.Nodes;
using Astra.Core.Billing;
using Astra.Core.Models;
using Astra.Gateway.Protocol;

namespace Astra.Gateway.Pipeline;

/// <summary>What a forwarded call's answer is: a JSON document (read for usage), or audio / text the vendor made.</summary>
public enum ForwardedReply
{
    Json,

    /// <summary>JSON, text or an event stream, but audio as often: copied through as it arrives, never buffered.</summary>
    Binary,
}

/// <summary>
/// An OpenAI-shaped call beside chat that the gateway forwards to the provider its model routes to without translating it:
/// the Images API (issue #17), embeddings, rerank and audio. The pipeline treats them alike
/// (<c>GatewayPipeline.HandleForwardedAsync</c>): the token and its caps, routing, credentials, the privacy guard over the
/// request's text, the request log (the call's own <see cref="Protocol"/> id, not an <see cref="ApiProtocol"/>: no codec
/// translates it), the trace and the bill from the answer's usage.
/// </summary>
/// <param name="Protocol">The request log's protocol id (inbound and upstream).</param>
/// <param name="Path">The path beside the provider's OpenAI endpoint (<c>embeddings</c>, <c>audio/speech</c>, …).</param>
/// <param name="Operation">The trace's <c>gen_ai.operation.name</c>.</param>
/// <param name="Noun">What the call is named in an error (Chinese, like the gateway's other client-facing messages).</param>
/// <param name="ExampleModel">A model id for the "model is missing" hint.</param>
/// <param name="Form">Takes a multipart/form-data upload (streamed on after its <c>model</c> field).</param>
/// <param name="Reply">How the answer is relayed.</param>
public sealed record ForwardedCall(string Protocol, string Path, string Operation, string Noun, string ExampleModel, bool Form = false,
    ForwardedReply Reply = ForwardedReply.Json)
{
    public const string ImagesProtocol = "openai-images";
    public const string EmbeddingsProtocol = "openai-embeddings";
    public const string RerankProtocol = "rerank";
    public const string AudioProtocol = "openai-audio";

    public static ForwardedCall ImageGenerations { get; } = new(ImagesProtocol, "images/generations", "image_generation", "图像接口", "gpt-image-1");
    public static ForwardedCall ImageEdits { get; } = new(ImagesProtocol, "images/edits", "image_edit", "图像接口", "gpt-image-1", Form: true);
    public static ForwardedCall Embeddings { get; } = new(EmbeddingsProtocol, "embeddings", "embeddings", "embeddings 接口", "text-embedding-3-small");
    public static ForwardedCall Rerank { get; } = new(RerankProtocol, "rerank", "rerank", "rerank 接口", "rerank-3");
    public static ForwardedCall Transcriptions { get; } = new(AudioProtocol, "audio/transcriptions", "transcription", "语音转文字接口", "whisper-1", Form: true);
    public static ForwardedCall Translations { get; } = new(AudioProtocol, "audio/translations", "translation", "语音翻译接口", "whisper-1", Form: true);
    public static ForwardedCall Speech { get; } = new(AudioProtocol, "audio/speech", "speech", "文字转语音接口", "gpt-4o-mini-tts", Reply: ForwardedReply.Binary);

    public static ForwardedCall Of(ImageOperation operation) => operation == ImageOperation.Generations ? ImageGenerations : ImageEdits;

    public bool IsImages => Protocol == ImagesProtocol;

    /// <summary>Embeddings of a Gemini-only provider are translated (<see cref="GeminiEmbeddings"/>) rather than refused.</summary>
    public bool IsEmbeddings => Protocol == EmbeddingsProtocol;

    /// <summary>The usage of an answer's (or event's) <c>usage</c> / <c>meta</c> object.</summary>
    public NormalizedUsage Usage(JsonObject usage) => IsImages ? UsageNormalizer.FromOpenAIImages(usage) : UsageNormalizer.FromForwarded(usage);
}

/// <summary>
/// Embeddings for a provider that speaks only Gemini: OpenAI's body in, <c>embedContent</c> / <c>batchEmbedContents</c> out, and
/// the vectors back in OpenAI's shape (floats, or base64 float32 when the caller asked for it — the Python SDK does by
/// default). Gemini reports no token count for them, so such a call is logged and not billed.
/// </summary>
public static class GeminiEmbeddings
{
    /// <summary>The request for a Gemini model: its path beside the endpoint's base address, and the body.</summary>
    /// <exception cref="ProtocolException">An <c>input</c> that is not a string or a list of strings.</exception>
    public static (string Path, JsonObject Body) Encode(JsonObject openAi, string model)
    {
        var bare = model.StartsWith("models/", StringComparison.Ordinal) ? model[7..] : model;
        var name = "models/" + bare;
        var texts = new List<string>();
        var batch = false;
        switch (openAi["input"])
        {
            case JsonValue v when v.TryGetValue<string>(out var one):
                texts.Add(one);
                break;
            case JsonArray list:
                batch = true;
                foreach (var item in list)
                {
                    if (item is JsonValue entry && entry.TryGetValue<string>(out var text)) texts.Add(text);
                    else throw new ProtocolException("input 必须是字符串或字符串数组（Gemini 提供商不支持 token 数组）。");
                }
                break;
            default:
                throw new ProtocolException("请求缺少 input（字符串或字符串数组）。");
        }
        int? dimensions = openAi["dimensions"] is JsonValue d && d.TryGetValue<int>(out var n) ? n : null;
        JsonObject One(string text)
        {
            var item = new JsonObject
            {
                ["model"] = name,
                ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) },
            };
            if (dimensions is { } size) item["outputDimensionality"] = size;
            return item;
        }
        var escaped = Uri.EscapeDataString(bare);
        return batch
            ? ($"models/{escaped}:batchEmbedContents", new JsonObject { ["requests"] = new JsonArray(texts.Select(t => (JsonNode)One(t)).ToArray()) })
            : ($"models/{escaped}:embedContent", One(texts[0]));
    }

    /// <summary>Gemini's answer as OpenAI's embeddings list; null when it is not an embedding answer (an error body, say).</summary>
    public static JsonObject? Decode(JsonObject gemini, string model, bool base64)
    {
        var vectors = new List<JsonArray>();
        if (gemini["embedding"]?["values"] is JsonArray single) vectors.Add(single);
        else if (gemini["embeddings"] is JsonArray many)
        {
            foreach (var item in many)
                if (item?["values"] is JsonArray values) vectors.Add(values);
        }
        if (vectors.Count == 0) return null;
        var data = new JsonArray();
        for (var i = 0; i < vectors.Count; i++)
        {
            JsonNode embedding = base64 ? JsonValue.Create(ToBase64(vectors[i]))! : vectors[i].DeepClone();
            data.Add((JsonNode)new JsonObject { ["object"] = "embedding", ["index"] = i, ["embedding"] = embedding });
        }
        return new JsonObject { ["object"] = "list", ["data"] = data, ["model"] = model };
    }

    private static string ToBase64(JsonArray values)
    {
        var bytes = new byte[values.Count * 4];
        for (var i = 0; i < values.Count; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4, 4), values[i]?.GetValue<float>() ?? 0f);
        return Convert.ToBase64String(bytes);
    }
}
