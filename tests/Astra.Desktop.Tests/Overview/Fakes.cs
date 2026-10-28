using System.Net;
using System.Text;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Tests.Overview;

/// <summary>Test doubles of the overview and token tests.</summary>
internal static class Fakes
{
    /// <summary>A translator that prints the key and its arguments, so a test sees exactly which text a function asked for.</summary>
    public static readonly Translate T = (key, args) => args.Length == 0 ? key : key + "(" + string.Join(",", args.Select(a => a.Name + "=" + a.Value)) + ")";

    /// <summary>2026-10-26 (a Monday) 12:00 UTC.</summary>
    public static readonly DateTimeOffset Noon = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    public static LocalTime Utc => LocalTime.At(Noon, TimeSpan.Zero);
}

/// <summary>Answers requests from a function and keeps every request it saw.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
{
    public List<(string Method, string Url, string Body, string? Admin)> Seen { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        request.Headers.TryGetValues("X-Astra-Admin", out var admin);
        lock (Seen) Seen.Add((request.Method.Method, request.RequestUri!.PathAndQuery, body, admin?.FirstOrDefault()));
        var (status, text) = answer(request);
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}

/// <summary>Records toasts and answers confirmations.</summary>
internal sealed class RecordingFeedback : IFeedback
{
    public List<(string Kind, string Title, string? Description)> Toasts { get; } = [];
    public List<ConfirmRequest> Confirms { get; } = [];
    public bool Answer { get; set; } = true;

    public void Success(string title, string? description = null) => Toasts.Add(("success", title, description));
    public void Error(string title, string? description = null) => Toasts.Add(("error", title, description));
    public void Info(string title, string? description = null) => Toasts.Add(("info", title, description));

    public Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct = default)
    {
        Confirms.Add(request);
        return Task.FromResult(Answer);
    }
}

/// <summary>Contract DTOs with sensible defaults.</summary>
internal static class Dtos
{
    public static Astra.Api.Contracts.ProviderDto Provider(string id, string name = "P", bool enabled = true, string auth = "bearer", int models = 1)
        => new(id, null, null, false, name, null, "cat", [], [], auth, false, null, new Dictionary<string, string>(), null, 1m, null, null, new System.Text.Json.Nodes.JsonObject(), enabled, 0,
            null, null, models, [], Fakes.Noon, Fakes.Noon,
            new Astra.Api.Contracts.ProviderQuotaConfigDto(false, null, null, null, null, 0, 0, null, [], [], null, null), null, null);

    public static Astra.Api.Contracts.TokenStatsDto Stats(decimal cost = 0, long requests = 0, long input = 0, long output = 0, double? hit = null, double? tps = null)
        => new(cost, requests, input, output, input + output, 0, 0, hit, tps);

    public static Astra.Api.Contracts.TokenDto Token(string id = "tok", string name = "name", bool isDefault = false, bool enabled = true,
        string? providerId = null, string? accountId = null, Astra.Api.Contracts.TokenPoolDto? pool = null, Astra.Api.Contracts.TokenLimitsDto? limits = null,
        List<string>? allowed = null, List<Astra.Api.Contracts.TokenBudgetUsageDto>? usage = null, IReadOnlyList<string>? clients = null, int recent = 0)
        => new(id, name, isDefault, enabled, "sk-…", providerId, accountId, Fakes.Noon, Fakes.Noon, null, clients ?? [], Stats(), Stats(), limits, allowed,
            usage ?? [], recent, pool);
}
