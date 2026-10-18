using System.Text;
using System.Text.Json.Nodes;
using Astra.Core.Routing;
using Astra.Gateway.Pipeline;

namespace Astra.Gateway.Routing;

/// <summary>
/// OpenAI Decisions: <c>POST {base}/v1/decisions</c> with <c>{ model, input, questions: [...] }</c>, answered with
/// <c>{ model, answers: [...], usage }</c>. The grades go in as the <c>choices</c> of one <c>choice</c> question.
/// <para>
/// DRAFT: OpenAI's own documentation could not be read while this was written (developers.openai.com refused the fetch),
/// so the field names follow LiteLLM's description of OpenAI's format (docs.litellm.ai/docs/decisions; plan §2.2).
/// Check them against OpenAI's "Create a decision" reference before relying on this dialect. The answer parser is
/// tolerant on purpose (probabilities as a list or a map, confidence optional) so a small difference does not break it.
/// </para>
/// </summary>
public sealed class DecisionsJudge(GatewayHttpClients clients) : IRoutingJudge
{
    /// <summary>The <c>name</c> of the one question, to find its answer again.</summary>
    internal const string QuestionName = "grade";

    public string Protocol => JudgeProtocols.OpenAI;

    public async Task<JudgeVerdict> JudgeAsync(JudgeEndpoint endpoint, JudgeQuestion question, CancellationToken ct)
    {
        var text = await JudgeHttp.PostAsync(clients, JudgeUrls.For(endpoint.BaseUrl, "decisions"), endpoint, BuildRequest(endpoint.Model, question), ct);
        return ParseResponse(text, question);
    }

    internal static JsonObject BuildRequest(string model, JudgeQuestion question)
    {
        var choices = new JsonArray(question.Choices
            .Select(c => (JsonNode)new JsonObject { ["value"] = c.Id, ["description"] = c.Description }).ToArray());
        return new JsonObject
        {
            ["model"] = model,
            ["input"] = RenderInput(question.State),
            ["questions"] = new JsonArray(new JsonObject
            {
                ["type"] = "choice", ["name"] = QuestionName, ["instructions"] = question.Instructions, ["choices"] = choices,
            }),
        };
    }

    /// <summary>OpenAI's input is text (or messages), not an object: the same state, as labelled sections.</summary>
    internal static string RenderInput(JudgeState state)
    {
        var text = new StringBuilder("Current request:\n").Append(state.Request);
        if (state.Earlier.Count > 0)
        {
            text.Append("\n\nEarlier requests (context only, oldest first):");
            for (var i = 0; i < state.Earlier.Count; i++) text.Append('\n').Append(i + 1).Append(". ").Append(state.Earlier[i]);
        }
        text.Append($"\n\nConversation: {state.Shape.Messages} messages, {state.Shape.Tools} tools available, images in the current request: {(state.Shape.HasImages ? "yes" : "no")}");
        return text.ToString();
    }

    internal static JudgeVerdict ParseResponse(string text, JudgeQuestion question)
    {
        var root = JudgeParsing.Parse(text) as JsonObject
                   ?? throw new RoutingJudgeException(JudgeFailure.BadResponse, "the answer is not a JSON object");
        var answers = (root["answers"] as JsonArray)?.OfType<JsonObject>().ToList()
                      ?? throw new RoutingJudgeException(JudgeFailure.BadResponse, "the answer has no answers");
        var answer = answers.FirstOrDefault(a => JudgeParsing.Text(a["name"]) == QuestionName)
                     ?? answers.FirstOrDefault(a => JudgeParsing.Text(a["type"]) == "choice")
                     ?? throw new RoutingJudgeException(JudgeFailure.BadResponse, "the answer has no answer to the question");
        var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
        switch (answer["probabilities"])
        {
            case JsonArray list:
                foreach (var item in list.OfType<JsonObject>())
                    if (JudgeParsing.Text(item["value"]) is { } grade && JudgeParsing.Number(item["probability"]) is { } probability)
                        probabilities[grade] = probability;
                break;
            case JsonObject map:
                foreach (var (grade, value) in map)
                    if (JudgeParsing.Number(value) is { } probability) probabilities[grade] = probability;
                break;
        }
        var usage = root["usage"] as JsonObject;
        return JudgeParsing.Verdict(question, JudgeParsing.Text(answer["choice"]), probabilities, JudgeParsing.Number(answer["confidence"]),
            JudgeParsing.Text(root["model"]), JudgeParsing.Count(usage?["input_tokens"]), JudgeParsing.Count(usage?["output_tokens"]));
    }
}
