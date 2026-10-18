using System.Text.Json.Nodes;
using Astra.Core.Routing;
using Astra.Gateway.Pipeline;

namespace Astra.Gateway.Routing;

/// <summary>
/// TypeSafe System One (Jev): <c>POST {base}/v1/systemone</c> with <c>{ model, state, questions }</c>, answered with
/// <c>{ model, answers, usage }</c> (docs.typesafe.ai; plan §2.1). OpenRouter, Vercel AI Gateway and LiteLLM host the
/// same protocol under their own base URL and model name, so they need no dialect of their own. The grades go in as the
/// <c>criteria</c> of one <c>choice</c> question; the answer's probabilities and confidence are TypeSafe's.
/// </summary>
public sealed class SystemOneJudge(GatewayHttpClients clients) : IRoutingJudge
{
    /// <summary>The key of the one question and of its answer.</summary>
    internal const string QuestionId = "grade";

    public string Protocol => JudgeProtocols.TypeSafe;

    public async Task<JudgeVerdict> JudgeAsync(JudgeEndpoint endpoint, JudgeQuestion question, CancellationToken ct)
    {
        var text = await JudgeHttp.PostAsync(clients, JudgeUrls.For(endpoint.BaseUrl, "systemone"), endpoint, BuildRequest(endpoint.Model, question), ct);
        return ParseResponse(text, question);
    }

    /// <summary>
    /// The state is an object (TypeSafe's advice: name each part) holding the current request, the earlier ones when
    /// there are any, and the conversation's shape.
    /// </summary>
    internal static JsonObject BuildRequest(string model, JudgeQuestion question)
    {
        var state = new JsonObject { ["request"] = question.State.Request };
        if (question.State.Earlier.Count > 0)
            state["earlier_requests"] = new JsonArray(question.State.Earlier.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray());
        state["conversation"] = new JsonObject
        {
            ["messages"] = question.State.Shape.Messages,
            ["tools"] = question.State.Shape.Tools,
            ["has_images"] = question.State.Shape.HasImages,
        };
        var criteria = new JsonObject();
        foreach (var choice in question.Choices) criteria[choice.Id] = choice.Description;
        return new JsonObject
        {
            ["model"] = model,
            ["state"] = state,
            ["questions"] = new JsonObject
            {
                [QuestionId] = new JsonObject { ["type"] = "choice", ["instructions"] = question.Instructions, ["criteria"] = criteria },
            },
        };
    }

    internal static JudgeVerdict ParseResponse(string text, JudgeQuestion question)
    {
        var root = JudgeParsing.Parse(text) as JsonObject
                   ?? throw new RoutingJudgeException(JudgeFailure.BadResponse, "the answer is not a JSON object");
        var answer = (root["answers"] as JsonObject)?[QuestionId] as JsonObject
                     ?? throw new RoutingJudgeException(JudgeFailure.BadResponse, "the answer has no answer to the question");
        var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
        if (answer["probabilities"] is JsonObject map)
            foreach (var (grade, value) in map)
                if (JudgeParsing.Number(value) is { } probability) probabilities[grade] = probability;
        var usage = root["usage"] as JsonObject;
        return JudgeParsing.Verdict(question, JudgeParsing.Text(answer["choice"]), probabilities, JudgeParsing.Number(answer["confidence"]),
            JudgeParsing.Text(root["model"]), JudgeParsing.Count(usage?["input_tokens"]), JudgeParsing.Count(usage?["output_tokens"]));
    }
}
