using System.Text.Json;
using Astra.Api.Contracts;
using Astra.Core;

namespace Astra.Desktop.Tests.Requests;

/// <summary>Request rows and live frames as the server writes them (camelCase, <c>Json.Api</c>).</summary>
internal static class RequestTestData
{
    public static RequestSummaryDto Row(
        string id, string status = "success", string startedAt = "2026-10-26T11:57:00Z", string? client = null, string? token = "tok_a", string? provider = "prov_a",
        string? model = "gpt-4.1-mini", string? upstream = null, string? response = null, bool remote = false)
        => new(id, DateTimeOffset.Parse(startedAt, System.Globalization.CultureInfo.InvariantCulture), client, provider, "OpenAI", "openai-chat", "openai-chat", true, model, upstream ?? model,
            response ?? model, model, true, status, 200, 120, 900, 50.0, 100, 20, 10, 0, 0, 12_000_000, "reported", null, null, null, token, "ci-bot", null, null, remote, null);

    public static string Frame(string type, RequestSummaryDto row)
        => "data: " + JsonSerializer.Serialize(new RequestLiveEventDto(type, row), JsonContexts.Info<RequestLiveEventDto>(Json.Api)) + "\n\n";
}
