using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Privacy;
using Astra.Server.Alerts;
using Astra.Server.Api;
using Astra.Server.Hosting;

namespace Astra.Server;

/// <summary>
/// Source-generated JSON metadata for everything the server serializes: the admin API DTOs and the
/// server-owned documents (<c>config.json</c>, <c>runtime.json</c>, settings blobs, the update feed).
/// Registered into <see cref="Astra.Core.JsonContexts"/> on module load so both
/// <see cref="Astra.Core.Json"/> and the ASP.NET HTTP JSON options resolve them without reflection
/// (Native AOT / trimming).
///
/// When you add a DTO that leaves the process — a new endpoint payload, a new persisted blob, a new
/// settings value — add it here; an unregistered type silently falls back to reflection, which works
/// today and fails under Native AOT.
/// </summary>
// plugins
[JsonSerializable(typeof(PluginDto))]
[JsonSerializable(typeof(PluginAgentDto))]
[JsonSerializable(typeof(PluginAuthMethodDto))]
[JsonSerializable(typeof(List<PluginAuthMethodDto>))]
[JsonSerializable(typeof(PluginSignInDto))]
[JsonSerializable(typeof(PluginPromptInput))]
[JsonSerializable(typeof(PluginValidateInput))]
[JsonSerializable(typeof(PluginSignInInput))]
[JsonSerializable(typeof(PluginApiKeyInput))]
[JsonSerializable(typeof(PluginCodeInput))]
[JsonSerializable(typeof(PluginCheckinDto))]
[JsonSerializable(typeof(PluginCheckinStateDto))]
[JsonSerializable(typeof(PluginCheckinSwitch))]
[JsonSerializable(typeof(MagpiePluginDto))]
[JsonSerializable(typeof(MagpieImportPreviewDto))]
[JsonSerializable(typeof(MagpieImportInput))]
[JsonSerializable(typeof(PluginsDto))]
[JsonSerializable(typeof(PluginAddInput))]
[JsonSerializable(typeof(PluginUpdateInput))]
[JsonSerializable(typeof(PluginOrderInput))]
[JsonSerializable(typeof(PluginPreferInput))]
// envelopes shared by several endpoints
[JsonSerializable(typeof(ErrorBody))]
[JsonSerializable(typeof(UpstreamStatusDetailsDto))]
[JsonSerializable(typeof(BoundClientsDetailsDto))]
[JsonSerializable(typeof(ErrorOnlyDto))]
[JsonSerializable(typeof(ErrorStatusDto))]
[JsonSerializable(typeof(ErrorVerificationDto))]
[JsonSerializable(typeof(RemovedDto))]
[JsonSerializable(typeof(StatusDto))]
[JsonSerializable(typeof(SignedInDto))]
[JsonSerializable(typeof(AuthStatusDto))]
[JsonSerializable(typeof(VersionDto))]
// models
[JsonSerializable(typeof(ModelDto))]
[JsonSerializable(typeof(ModelDetailDto))]
[JsonSerializable(typeof(ModelPriceDto))]
[JsonSerializable(typeof(ProviderOverrideRefDto))]
[JsonSerializable(typeof(ModelInput))]
[JsonSerializable(typeof(ModelPriceInput))]
[JsonSerializable(typeof(ResetFieldInput))]
[JsonSerializable(typeof(PriceKeyInfo))]
// billing
[JsonSerializable(typeof(BillingSimulateRequest))]
[JsonSerializable(typeof(SimulateUsage))]
[JsonSerializable(typeof(BillingItemDto))]
[JsonSerializable(typeof(BillingResultDto))]
// providers
[JsonSerializable(typeof(ProviderDto))]
[JsonSerializable(typeof(ModelOverridesDto))]
[JsonSerializable(typeof(EffectiveModelDto))]
[JsonSerializable(typeof(ProviderModelDto))]
// provider balance / quota query
[JsonSerializable(typeof(ProviderQuotaConfigDto))]
[JsonSerializable(typeof(ProviderQuotaDto))]
[JsonSerializable(typeof(ProviderQuotaTestDto))]
[JsonSerializable(typeof(ProviderQuotaErrorDto))]
// provider import
[JsonSerializable(typeof(ImportSourceDto))]
[JsonSerializable(typeof(ImportCandidateDto))]
[JsonSerializable(typeof(ImportEndpointDto))]
[JsonSerializable(typeof(ImportExistingDto))]
[JsonSerializable(typeof(ImportSelectionDto))]
[JsonSerializable(typeof(ImportResultDto))]
[JsonSerializable(typeof(ImportedProviderDto))]
[JsonSerializable(typeof(ImportSkippedDto))]
// provider templates
[JsonSerializable(typeof(TemplateUpdateDto))]
[JsonSerializable(typeof(RemoteModelDto))]
[JsonSerializable(typeof(ProviderTestOptions))]
[JsonSerializable(typeof(ProviderTestEvent))]
// clients
[JsonSerializable(typeof(ClientDetectionDto))]
[JsonSerializable(typeof(ClientInfoDto))]
[JsonSerializable(typeof(ClientPreviewDto))]
[JsonSerializable(typeof(ConfigChangeDto))]
[JsonSerializable(typeof(ClientDisableDto))]
[JsonSerializable(typeof(ClientBackupDto))]
[JsonSerializable(typeof(ConfigRepairDto))]
[JsonSerializable(typeof(TokenRewriteResult))]
[JsonSerializable(typeof(ReappliedDto))]
[JsonSerializable(typeof(ClientEndpoints.BindingInput))]
[JsonSerializable(typeof(ClientEndpoints.BindingsInput))]
[JsonSerializable(typeof(ClientBindingDto))]
[JsonSerializable(typeof(ClientOptionDescriptor))]
[JsonSerializable(typeof(List<ClientOptionDescriptor>))]
[JsonSerializable(typeof(ClientInstallDto))]
[JsonSerializable(typeof(ClientCopyDto))]
[JsonSerializable(typeof(ClientInstallJobDto))]
[JsonSerializable(typeof(ClientInstallRequest))]
[JsonSerializable(typeof(ClientUpdateCheckRequest))]
// Direct Claude Code mode with an Astra subscription account, and its opt-in client-side statistics.
[JsonSerializable(typeof(ClaudeDirectAccountDto))]
[JsonSerializable(typeof(ClaudeDirectStateDto))]
[JsonSerializable(typeof(ClaudeDirectSelectInput))]
[JsonSerializable(typeof(ClaudeDirectPrepareInput))]
[JsonSerializable(typeof(ClaudeDirectLaunchDto))]
[JsonSerializable(typeof(List<Astra.Data.Repositories.ClaudeDirectRequest>))]
// tokens
[JsonSerializable(typeof(TokenDto))]
[JsonSerializable(typeof(TokenStatsDto))]
[JsonSerializable(typeof(TokenLimitsDto))]
[JsonSerializable(typeof(WebhookDto))]
[JsonSerializable(typeof(WebhookSecretDto))]
[JsonSerializable(typeof(WebhookDeliveryDto))]
[JsonSerializable(typeof(AlertEventDto))]
[JsonSerializable(typeof(AlertParamDto))]
[JsonSerializable(typeof(AlertRuleDto))]
[JsonSerializable(typeof(AlertStateDto))]
[JsonSerializable(typeof(List<WebhookDto>))]
[JsonSerializable(typeof(List<WebhookDeliveryDto>))]
[JsonSerializable(typeof(List<AlertEventDto>))]
[JsonSerializable(typeof(List<AlertRuleDto>))]
[JsonSerializable(typeof(List<AlertStateDto>))]
[JsonSerializable(typeof(TokenBudgetDto))]
[JsonSerializable(typeof(TokenBudgetUsageDto))]
[JsonSerializable(typeof(TokenSecretDto))]
[JsonSerializable(typeof(TokenMutationResultDto))]
[JsonSerializable(typeof(TokenPoolDto))]
[JsonSerializable(typeof(TokenPoolMemberDto))]
[JsonSerializable(typeof(PoolMemberHealthDto))]
[JsonSerializable(typeof(TokenPoolInputDto))]
[JsonSerializable(typeof(TokenPoolMemberInputDto))]
// requests and stats
[JsonSerializable(typeof(RequestEndpoints.RequestSummaryDto))]
[JsonSerializable(typeof(RequestEndpoints.RequestDetailDto))]
[JsonSerializable(typeof(RequestEndpoints.RequestLiveEventDto))]
[JsonSerializable(typeof(RequestEndpoints.UsageItemDto))]
[JsonSerializable(typeof(RequestEndpoints.BodiesDto))]
[JsonSerializable(typeof(RequestEndpoints.BodySizesDto))]
[JsonSerializable(typeof(RequestEndpoints.RequestPageDto))]
[JsonSerializable(typeof(RequestEndpoints.PageDto<PrivacyEndpoints.PrivacyEventDto>))]
[JsonSerializable(typeof(RequestEndpoints.StatsSummaryDto))]
[JsonSerializable(typeof(RequestEndpoints.TimeseriesPointDto))]
[JsonSerializable(typeof(RequestEndpoints.TopModelDto))]
[JsonSerializable(typeof(RequestEndpoints.DailyActivityDto))]
[JsonSerializable(typeof(RequestEndpoints.ProviderStatDto))]
[JsonSerializable(typeof(RequestEndpoints.RateStatsDto))]
[JsonSerializable(typeof(RequestEndpoints.SettingsDto))]
// traces (.plan/observability.md §4)
[JsonSerializable(typeof(TraceEndpoints.TraceRequestDto))]
[JsonSerializable(typeof(TraceEndpoints.TraceSessionDetailDto))]
[JsonSerializable(typeof(TraceEndpoints.TraceTopologyDto))]
[JsonSerializable(typeof(List<TraceEndpoints.TraceSessionDto>))]
// privacy
[JsonSerializable(typeof(PrivacyEndpoints.DryRunRequest))]
[JsonSerializable(typeof(PrivacyEndpoints.PrivacyEventDto))]
[JsonSerializable(typeof(PrivacyEndpoints.PrivacyCategoryStatDto))]
[JsonSerializable(typeof(PrivacyEndpoints.PrivacyEventStatsDto))]
[JsonSerializable(typeof(PrivacyEndpoints.PrivacyRuleDto))]
[JsonSerializable(typeof(PrivacyEndpoints.PrivacyPolicyDto))]
[JsonSerializable(typeof(PrivacyEndpoints.PrivacyDryRunResultDto))]
// model sync
[JsonSerializable(typeof(SyncChangeDto))]
[JsonSerializable(typeof(SyncPreviewDto))]
[JsonSerializable(typeof(SyncApplyRequest))]
[JsonSerializable(typeof(SyncApplyResult))]
// subscriptions and OAuth login
[JsonSerializable(typeof(SubscriptionEndpoints.LoginOptions))]
[JsonSerializable(typeof(SubscriptionEndpoints.ProviderAccountDto))]
[JsonSerializable(typeof(List<SubscriptionEndpoints.ProviderAccountDto>))]
[JsonSerializable(typeof(SubscriptionEndpoints.AccountPatch))]
[JsonSerializable(typeof(SubscriptionEndpoints.AccountOrder))]
[JsonSerializable(typeof(SubscriptionEndpoints.SubscriptionPolicyDto))]
[JsonSerializable(typeof(SubscriptionEndpoints.SubscriptionPolicyPatch))]
[JsonSerializable(typeof(SubscriptionEndpoints.LoginModeDto))]
[JsonSerializable(typeof(SubscriptionEndpoints.LoginDeviceDto))]
[JsonSerializable(typeof(SubscriptionEndpoints.LoginPasteDto))]
[JsonSerializable(typeof(SubscriptionEndpoints.CompleteLoginRequest))]
[JsonSerializable(typeof(SubscriptionEndpoints.LoginCliDto))]
[JsonSerializable(typeof(SubscriptionEndpoints.LoginPollDto))]
[JsonSerializable(typeof(SubscriptionEndpoints.LoginDoneDto))]
[JsonSerializable(typeof(SubscriptionEndpoints.PollFailureDto))]
[JsonSerializable(typeof(SubscriptionEndpoints.ResetCreditResultDto))]
[JsonSerializable(typeof(SubscriptionEndpoints.LocalCodexLoginDto))]
[JsonSerializable(typeof(SubscriptionEndpoints.LocalCopilotLoginDto))]
[JsonSerializable(typeof(SubscriptionEndpoints.LocalCopilotImport))]
[JsonSerializable(typeof(SubscriptionEndpoints.ImportedAccountDto))]
[JsonSerializable(typeof(SubscriptionEndpoints.BatchCopilotImport))]
[JsonSerializable(typeof(SubscriptionEndpoints.BatchImportResultDto))]
[JsonSerializable(typeof(SubscriptionEndpoints.AccountImportRequest))]
[JsonSerializable(typeof(SubscriptionEndpoints.AccountImportInfoDto))]
[JsonSerializable(typeof(SubscriptionEndpoints.BatchImportItemDto))]
[JsonSerializable(typeof(List<SubscriptionEndpoints.BatchImportItemDto>))]
// auth
[JsonSerializable(typeof(SystemEndpoints.LoginRequest))]
[JsonSerializable(typeof(SystemEndpoints.SetPasswordRequest))]
[JsonSerializable(typeof(SystemEndpoints.ListenDto))]
[JsonSerializable(typeof(SystemEndpoints.ListenRequest))]
[JsonSerializable(typeof(AutostartEndpoints.AutostartDto))]
[JsonSerializable(typeof(AutostartEndpoints.AutostartRequest))]
[JsonSerializable(typeof(TunnelEndpoints.TunnelStatusDto))]
[JsonSerializable(typeof(TunnelEndpoints.TunnelConfigRequest))]
[JsonSerializable(typeof(TunnelEndpoints.DetectedCloudflaredDto))]
// server-owned documents
[JsonSerializable(typeof(ServerOptions))]
[JsonSerializable(typeof(RuntimeInfo))]
[JsonSerializable(typeof(UpdateManifest))]
[JsonSerializable(typeof(UpdateCheckState))]
[JsonSerializable(typeof(UpdateEndpoints.UpdateStatusDto))]
// collections used as roots
[JsonSerializable(typeof(List<SyncChangeDto>))]
[JsonSerializable(typeof(List<ClientInfoDto>))]
[JsonSerializable(typeof(List<ClientBackupDto>))]
[JsonSerializable(typeof(List<ProviderDto>))]
[JsonSerializable(typeof(List<ImportSourceDto>))]
[JsonSerializable(typeof(List<ImportSelectionDto>))]
[JsonSerializable(typeof(List<TokenDto>))]
[JsonSerializable(typeof(List<ModelDto>))]
[JsonSerializable(typeof(List<RequestEndpoints.DailyActivityDto>))]
[JsonSerializable(typeof(List<RequestEndpoints.TimeseriesPointDto>))]
[JsonSerializable(typeof(List<RequestEndpoints.TopModelDto>))]
[JsonSerializable(typeof(List<RequestEndpoints.ProviderStatDto>))]
[JsonSerializable(typeof(List<PriceKeyInfo>))]
// The provider model lists (configured and upstream) are materialised as Lists before they leave the endpoint
// (an interface-typed root makes the serializer probe the whole interface graph), so the Lists are root types.
[JsonSerializable(typeof(List<ProviderModelDto>))]
[JsonSerializable(typeof(List<RemoteModelDto>))]
// Endpoints that hand back an interface-typed collection: the declared type is the root the
// serializer resolves, so the interface itself needs metadata.
[JsonSerializable(typeof(IReadOnlyList<ClientInfoDto>))]
[JsonSerializable(typeof(IReadOnlyList<TokenDto>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonNode))]
internal partial class ServerJsonContext : JsonSerializerContext
{
    // CA2255 warns about module initializers in libraries. Registration here is deliberate and is the
    // point of the type: Core cannot reference this assembly, so the context announces itself instead.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage", "CA2255:The ModuleInitializer attribute should not be used in libraries",
        Justification = "Self-registration into JsonContexts is the intended contract; Core must not reference this assembly.")]
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register() => Astra.Core.JsonContexts.Add(Default);
}
