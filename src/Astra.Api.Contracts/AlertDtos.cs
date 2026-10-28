using System.Text.Json.Nodes;

namespace Astra.Api.Contracts;



/// <summary>A webhook in the admin API: the secret only as <see cref="HasSecret"/>, custom headers by name only.</summary>
/// <param name="Format"><see cref="WebhookFormats"/>.</param>
public sealed record WebhookDto(
    string Id, string Name, string Url, string Format, bool Enabled, int TimeoutSec, bool UseProxy, bool HasSecret, List<string> HeaderNames,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Create / secret reset: the webhook plus its new (generated) secret, shown this once; null for bot formats.</summary>
public sealed record WebhookSecretDto(WebhookDto Webhook, string? Secret);

/// <summary>Admin API payload: webhook delivery.</summary>
public sealed record WebhookDeliveryDto(
    string Id, string WebhookId, string? RuleId, string Event, string Status, int Attempts, DateTimeOffset? NextAttemptAt,
    int? HttpStatus, string? Error, string? ResponseSnippet, JsonNode? Payload, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt);

/// <summary>Admin API payload: alert param.</summary>
public sealed record AlertParamDto(string Name, string Kind, List<string>? Choices, double? Default, bool Required);

/// <summary>Admin API payload: alert event.</summary>
public sealed record AlertEventDto(string Id, string SubjectType, List<AlertParamDto> Params);

/// <param name="Subjects">Subject ids; null = every subject of the event's type.</param>
/// <param name="Firing">How many subjects the rule is firing for right now.</param>
public sealed record AlertRuleDto(
    string Id, string Name, bool Enabled, string Event, List<string>? Subjects, JsonObject Condition, bool NotifyResolved,
    int? RepeatMinutes, int CooldownMinutes, List<string> WebhookIds, int Firing, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Admin API payload: alert state.</summary>
public sealed record AlertStateDto(
    string RuleId, string RuleName, string Event, string SubjectType, string SubjectId, string SubjectName, string State,
    JsonObject? Value, DateTimeOffset ChangedAt, DateTimeOffset? LastNotifiedAt);
