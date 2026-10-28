using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Routing;
using Astra.Gateway.Routing;

namespace Astra.Server.Api;

/// <summary>
/// /api/smart-routing (plan §5.7): read and change the smart-routing settings, try the judge on a sample text, and list
/// the models a grade can be given. Changes are checked as a whole and saved atomically; a refusal is a 400 (409 for a
/// virtual-model clash) whose <c>details.problems</c> hold every reason with a stable code the console turns into its own words.
/// </summary>
public static class SmartRoutingEndpoints
{

















    public static void MapSmartRoutingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/smart-routing").AddEndpointFilter<AdminApiErrorFilter>();

        group.MapGet("", (SmartRoutingService service, SmartRouter router, ISecretProtector secrets) =>
            Results.Ok(ToDto(service.Current, router, secrets)));

        group.MapPut("", async (SmartRoutingUpdate body, SmartRoutingService service, SmartRouter router, ISecretProtector secrets, CancellationToken ct) =>
        {
            try
            {
                var saved = await service.UpdateAsync(s => Apply(s, body, secrets), ct);
                return Results.Ok(ToDto(saved, router, secrets));
            }
            catch (RoutingSettingsException e)
            {
                throw Refuse(e.Problems);
            }
        });

        // Asks the judge about a sample text with the settings as they are being edited. Nothing is saved and no request
        // is logged; the call itself is the judge service's and may cost its (small) per-token price.
        group.MapPost("/test", async (SmartRoutingTestRequest body, SmartRoutingService service, RoutingJudgeRegistry judges, ISecretProtector secrets, CancellationToken ct) =>
        {
            var draft = service.Current.Clone();
            try
            {
                Apply(draft, new SmartRoutingUpdate(Judge: body.Judge, Grades: body.Grades), secrets);
            }
            catch (RoutingSettingsException e)
            {
                throw Refuse(e.Problems);
            }
            SmartRoutingService.Normalize(draft);
            var problems = SmartRoutingValidator.Check(draft);
            problems.AddRange(SmartRoutingValidator.CheckComplete(draft).Where(p => p.Code is SmartRoutingValidator.Codes.JudgeKeyMissing or SmartRoutingValidator.Codes.JudgeModelMissing));
            if (problems.Count > 0) throw Refuse(problems);

            var text = body.Text?.Trim();
            if (string.IsNullOrEmpty(text)) throw new AdminApiException(400, "text is required");
            var chat = new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = text }) };
            var ask = AskExtractor.Extract(ApiProtocol.OpenAIChat, chat, 0, draft.Rules.MaxChars)
                      ?? throw new AdminApiException(400, "text has nothing to judge");
            string key;
            try
            {
                key = secrets.Unprotect(draft.Judge.ApiKeyProtected!);
            }
            catch (CryptographicException)
            {
                throw Refuse([new RoutingProblem(SmartRoutingValidator.Codes.JudgeKeyMissing, "the saved API key can no longer be read; enter it again", "judge.apiKey")]);
            }

            // A first call is often slower than the deadline requests run under; the test is the user's own, so it waits longer
            // — and says how long a request would have waited (LimitMs), so a slow answer is not mistaken for a working one.
            var connecting = false;
            var endpoint = new JudgeEndpoint(draft.Judge.EffectiveBaseUrl, key, draft.Judge.Model, TimeSpan.FromMilliseconds(Math.Max(draft.Judge.TimeoutMs, 5000)))
            {
                OnNewConnection = () => connecting = true,
            };
            var question = SmartRouter.QuestionFor(draft, ask);
            var started = Stopwatch.GetTimestamp();
            long Elapsed() => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long Limit() => draft.Judge.TimeoutMs + (connecting ? (long)JudgeEndpoint.ConnectAllowance.TotalMilliseconds : 0);
            try
            {
                var verdict = await judges.Require(draft.Judge.Protocol).JudgeAsync(endpoint, question, ct);
                var grade = draft.Grades.First(g => g.Id == verdict.Choice);
                return Results.Ok(new SmartRoutingTestDto(
                    true, grade.Id, grade.Name.Length > 0 ? grade.Name : grade.Id, grade.Model.Length > 0 ? grade.Model : null, verdict.Confidence,
                    verdict.Confidence >= draft.Rules.MinConfidence, verdict.Probabilities.ToDictionary(p => p.Key, p => p.Value), verdict.JudgeModel,
                    Elapsed(), verdict.InputTokens, verdict.OutputTokens, null, null, Limit(), connecting));
            }
            catch (RoutingJudgeException e)
            {
                return Results.Ok(new SmartRoutingTestDto(
                    false, null, null, null, null, null, null, null, Elapsed(), 0, 0, SmartRouter.FailureName(e.Kind), e.Message, Limit(), connecting));
            }
        });

        // Every model each usable provider serves: what a grade can be given. The same id on two providers is listed for
        // both, because a grade names the provider its requests go to.
        group.MapGet("/models", async (SmartRoutingService service, CancellationToken ct) =>
            Results.Ok((await service.RoutingOptionsAsync(ct))
                .Select(m => new RoutingModelDto(m.Id, m.Id == m.Model.ModelId ? m.Effective.DisplayName : m.Id, m.Target.Provider.Id, m.Target.Provider.Name))
                .ToList()));
    }

    /// <summary>Applies an update to <paramref name="s"/> (a copy of the saved settings); it is checked afterwards, as a whole.</summary>
    internal static void Apply(SmartRoutingSettings s, SmartRoutingUpdate body, ISecretProtector secrets)
    {
        var originBefore = OriginOf(s.Judge);
        if (body.Enabled is { } enabled) s.Enabled = enabled;
        if (body.VirtualModel is not null) s.VirtualModel = body.VirtualModel;
        if (body.Judge is { } judge)
        {
            if (judge.Protocol is not null && judge.Protocol != s.Judge.Protocol)
            {
                // Another dialect starts from its own address and model.
                s.Judge.Protocol = judge.Protocol;
                s.Judge.BaseUrl = null;
                s.Judge.Model = JudgeProtocols.DefaultModel(judge.Protocol);
            }
            if (judge.BaseUrl is not null) s.Judge.BaseUrl = judge.BaseUrl; // blank: back to the dialect's own address
            if (judge.Model is not null) s.Judge.Model = judge.Model;
            if (judge.TimeoutMs is { } timeout) s.Judge.TimeoutMs = timeout;
            if (judge.Instructions is not null) s.Judge.Instructions = judge.Instructions; // blank: back to the built-in question
            if (judge.ApiKey is { } apiKey)
            {
                if (apiKey.Length == 0) s.Judge.ApiKeyProtected = null;
                else
                {
                    var trimmed = apiKey.Trim();
                    if (!SmartRoutingValidator.IsValidApiKey(trimmed))
                        throw new RoutingSettingsException([new RoutingProblem(SmartRoutingValidator.Codes.JudgeKeyInvalid,
                            "the API key may only contain visible ASCII characters (no spaces or line breaks)", "judge.apiKey")]);
                    s.Judge.ApiKeyProtected = secrets.Protect(trimmed);
                }
            }
        }
        if (body.Grades is { } grades)
            s.Grades = grades.Select(g => new RoutingGrade
            {
                Id = g.Id ?? "", Name = g.Name ?? "", Description = g.Description ?? "", Model = g.Model ?? "", ProviderId = g.ProviderId,
            }).ToList();
        if (body.Rules is { } rules)
        {
            if (rules.Mode is not null) s.Rules.Mode = rules.Mode;
            if (rules.SessionTtlMinutes is { } ttl) s.Rules.SessionTtlMinutes = ttl;
            if (rules.FallbackGrade is not null) s.Rules.FallbackGrade = rules.FallbackGrade;
            if (rules.MinConfidence is { } minConfidence) s.Rules.MinConfidence = minConfidence;
            if (rules.ContextTurns is { } contextTurns) s.Rules.ContextTurns = contextTurns;
            if (rules.MaxChars is { } maxChars) s.Rules.MaxChars = maxChars;
        }
        // A saved key belongs to the service that issued it. Pointing the judge at another address (or another dialect)
        // without giving that service's own key must not send the old key there: it is dropped, and has to be entered again.
        if (body.Judge?.ApiKey is null && OriginOf(s.Judge) != originBefore) s.Judge.ApiKeyProtected = null;
    }

    /// <summary>Scheme, host and port of the judge's address: what decides who receives its key.</summary>
    private static string OriginOf(RoutingJudgeSettings judge)
    {
        var url = string.IsNullOrWhiteSpace(judge.BaseUrl) ? JudgeProtocols.DefaultBaseUrl(judge.Protocol) : judge.BaseUrl.Trim();
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant() : url;
    }

    private static AdminApiException Refuse(IReadOnlyList<RoutingProblem> problems) =>
        new(problems.Any(p => p.Code == SmartRoutingValidator.Codes.VirtualModelConflict) ? 409 : 400, problems[0].Message,
            new RoutingProblemsDetails(problems.Select(p => new RoutingProblemDto(p.Code, p.Message, p.Field)).ToList()));

    private static SmartRoutingDto ToDto(SmartRoutingSettings s, SmartRouter router, ISecretProtector secrets)
    {
        string? mask = null;
        var hasKey = !string.IsNullOrEmpty(s.Judge.ApiKeyProtected);
        if (hasKey)
        {
            try
            {
                var key = secrets.Unprotect(s.Judge.ApiKeyProtected!);
                mask = key.Length >= 12 ? key[..3] + "…" + key[^4..] : "••••";
            }
            catch (CryptographicException)
            {
                mask = "••••";
            }
        }
        return new SmartRoutingDto(
            s.Enabled, s.VirtualModel,
            new SmartRoutingJudgeDto(s.Judge.Protocol, s.Judge.BaseUrl, s.Judge.EffectiveBaseUrl, hasKey, mask, s.Judge.Model, s.Judge.TimeoutMs, s.Judge.Instructions),
            s.Grades.Select(g => new SmartRoutingGradeDto(g.Id, g.Name, g.Description, g.Model, g.ProviderId)).ToList(),
            new SmartRoutingRulesDto(s.Rules.Mode, s.Rules.SessionTtlMinutes, s.Rules.FallbackGrade, s.Rules.MinConfidence, s.Rules.ContextTurns, s.Rules.MaxChars),
            StatusOf(router),
            SmartRoutingSettings.DefaultInstructions,
            JudgeProtocols.All.Select(p => new JudgeProtocolDto(p, JudgeProtocols.DefaultBaseUrl(p), JudgeProtocols.DefaultModel(p))).ToList());
    }

    private static SmartRoutingStatusDto StatusOf(SmartRouter router)
    {
        var activity = router.Activity;
        var decisions = activity.Decisions;
        // A judge that answered, however sure it was: its time is the judge's speed. A timeout's time is only the deadline.
        var answered = decisions.Where(d => d.Outcome is RoutingSources.Judge or "low_confidence").Select(d => d.JudgeMs).Order().ToList();
        var lastFallback = decisions.LastOrDefault(d => d.Outcome != RoutingSources.Judge);
        var recent = new RecentDecisionsDto(
            decisions.Count,
            decisions.GroupBy(d => d.Outcome).ToDictionary(g => g.Key, g => g.Count()),
            answered.Count == 0 ? null : answered[answered.Count / 2],
            lastFallback?.Outcome, lastFallback?.At);
        return new SmartRoutingStatusDto(router.BreakerOpen, router.PinCount, recent,
            activity.Unreachable.Select(u => new UnreachableGradeDto(u.Caller, u.Grade, u.Model, u.UsedGrade, u.Count, u.LastAt, u.Reason, u.ProviderName)).ToList());
    }
}
