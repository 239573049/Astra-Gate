// Astra admin API contract (server: src/Astra.Server/Api, JSON camelCase).
// Exception: `PricingSchedule` objects are passed verbatim in their storage format (snake_case keys,
// token types as keys) because users can edit them as raw JSON.
// Every mutating request (POST/PUT/PATCH/DELETE under /api) must send header `X-Astra-Admin: 1`.
// Errors: non-2xx with body `{ error: string, details?: unknown }`.

export const API_VERSION = "1.0";

// ---------- shared ----------
export type ApiProtocol = "openai-chat" | "openai-responses" | "anthropic" | "gemini";
export type TokenType =
  | "input" | "cache_read" | "cache_write_5m" | "cache_write_1h" | "input_audio" | "input_image"
  | "output" | "reasoning" | "output_audio" | "output_image" | (string & {});
export type Range = "today" | "7d" | "30d" | "90d" | "all";

export interface Page<T> { items: T[]; total: number; page: number; pageSize: number }

// GET /api/health -> { status: "ok" } ; GET /api/version -> VersionInfo
export interface VersionInfo { version: string; apiVersion: string }
// GET /api/auth/status ; POST /api/auth/login {password} ; POST /api/auth/logout
export interface AuthStatus { required: boolean; signedIn: boolean }

// ---------- pricing (storage format, snake_case) ----------
export type PriceSet = Partial<Record<TokenType, number | null>> & { per_call?: Record<string, number> };
export interface ServiceTierRule { multiplier?: number | null; prices?: PriceSet | null }
export interface ContextTier {
  name: string; threshold_input_tokens: number;
  mode: "whole" | "progressive"; output_policy: "highest" | "base"; prices: PriceSet;
}
export interface TimeWindowRule {
  name: string; timezone: string; start: string; end: string; // "HH:mm", [start, end), end<=start crosses midnight
  days?: number[] | null;          // ISO weekdays 1=Mon..7=Sun (day the window starts)
  exclude_dates?: string[] | null; // "yyyy-MM-dd" in the window timezone (holidays)
  multiplier?: number | null; prices?: PriceSet | null;
}
export interface PricingSchedule {
  currency: "USD"; unit: "per_1m_tokens"; base: PriceSet;
  service_tiers?: Record<string, ServiceTierRule> | null;
  context_tiers?: ContextTier[] | null;
  time_windows?: TimeWindowRule[] | null;
  per_request?: number | null; notes?: string | null;
}

// ---------- models (system catalog; pricing differs per AI provider) ----------
export interface ModelCapabilities {
  vision?: boolean | null; tools?: boolean | null; reasoning?: boolean | null; promptCache?: boolean | null;
  audio?: boolean | null; pdf?: boolean | null; structuredOutput?: boolean | null;
}
export type ModelField = "displayName" | "family" | "aliases" | "contextWindow" | "maxOutputTokens" | "capabilities" | "pricing";
export interface Model {
  id: string; displayName: string; vendor: string; family?: string | null; aliases: string[];
  contextWindow?: number | null; maxOutputTokens?: number | null; capabilities: ModelCapabilities;
  /** Official (vendor) default pricing. */
  pricing?: PricingSchedule | null;
  source: "seed" | "sync" | "user"; userModifiedFields: ModelField[]; enabled: boolean;
  /** Price keys that have a provider-specific price for this model. */
  providerPriceKeys: string[];
  updatedAt: string;
}
/** System price of a model at one AI provider type (price key = provider template / models.dev provider id). */
export interface ModelPrice {
  priceKey: string; upstreamModelId?: string | null; pricing: PricingSchedule;
  source: "seed" | "sync" | "user"; userModified: boolean; updatedAt: string;
}
export interface ProviderOverrideRef { providerId: string; providerName: string; providerModelId: number; modelId: string; pricing: PricingSchedule }
export interface ModelDetail extends Model { providerPrices: ModelPrice[]; providerOverrides: ProviderOverrideRef[] }
// GET /api/models?search=&vendor= -> Model[]
// GET /api/models/{id} -> ModelDetail
// POST /api/models  body ModelInput -> Model           (source=user)
// PUT /api/models/{id} body ModelInput -> Model        (changed fields are added to userModifiedFields)
// DELETE /api/models/{id}
// POST /api/models/{id}/reset-field body {field: ModelField} -> Model   (restore seed value)
// PUT /api/models/{id}/prices/{priceKey} body {upstreamModelId?, pricing} -> ModelPrice
// DELETE /api/models/{id}/prices/{priceKey}
// GET /api/price-keys -> PriceKeyInfo[]   (all known price keys: from templates + existing prices)
export interface ModelInput {
  id?: string; displayName: string; vendor: string; family?: string | null; aliases?: string[];
  contextWindow?: number | null; maxOutputTokens?: number | null; capabilities?: ModelCapabilities;
  pricing?: PricingSchedule | null; enabled?: boolean;
}
export interface PriceKeyInfo { priceKey: string; label: string; templateIds: string[] }

// POST /api/models/sync/preview -> SyncPreview ; POST /api/models/sync/apply body {changeIds: string[]} -> {applied: number}
export interface SyncChange {
  id: string; modelId: string; priceKey?: string | null; kind: "new_model" | "field" | "price";
  field?: string | null; before?: unknown; after?: unknown; skippedBecauseUserModified: boolean;
}
export interface SyncPreview { source: string; fetchedAt: string; changes: SyncChange[] }

// POST /api/billing/simulate body BillingSimulateRequest -> BillingResult
export interface BillingSimulateRequest {
  pricing?: PricingSchedule | null;
  /** Alternatively price an existing model at a provider (uses the same resolver as the gateway). */
  modelId?: string; providerId?: string;
  usage: { tokens: Partial<Record<TokenType, number>>; calls?: Record<string, number>; serviceTier?: string | null };
  requestTimeUtc?: string; providerMultiplier?: number; locale?: "zh" | "en";
}
export interface BillingItem {
  tokenType: string; tokens: number; isPerCall: boolean; baseUnitPrice: number; unitPrice: number;
  multiplier: number; multiplierSources: string[]; tier: string; pricedAs: string; costNanos: number; note?: string | null;
}
export interface BillingTraceStep { code: string; args: Record<string, string> }
export interface BillingResult {
  items: BillingItem[]; totalNanos: number; totalUsd: number; priced: boolean;
  trace: BillingTraceStep[]; description: string;
  pricingSource: "system_default" | "provider_price" | "provider_override" | "none"; priceKey?: string | null;
}

// ---------- provider templates & providers ----------
export interface TemplateEndpoint { protocol: ApiProtocol; baseUrl: string; fullUrl?: boolean }
export interface ProviderTemplate {
  id: string; version: number; name: string; category: "official" | "subscription" | "cn" | "aggregator" | "custom" | "local";
  icon?: string | null; website?: string | null; apiKeyUrl?: string | null; docsUrl?: string | null;
  endpoints: TemplateEndpoint[]; preferredUpstreamProtocols: ApiProtocol[];
  auth: { scheme: AuthScheme }; authScheme: AuthScheme; requiresApiKey: boolean; models: string[];
  priceKey?: string | null; defaultPriceMultiplier?: number | null;
  variants?: { id: string; name: string; priceKey?: string | null; endpointsOverride?: TemplateEndpoint[] | null }[] | null;
}
export type AuthScheme = "bearer" | "x-api-key" | "x-goog-api-key" | "query-key" | "none" | "oauth-subscription";
// GET /api/provider-templates -> ProviderTemplate[]

export interface Provider {
  id: string; templateId?: string | null; templateVersion?: number | null; templateUpdateAvailable: boolean;
  name: string; icon?: string | null; category: string;
  endpoints: TemplateEndpoint[]; preferredUpstreamProtocols: ApiProtocol[]; authScheme: AuthScheme;
  hasApiKey: boolean; apiKeyMasked?: string | null; // e.g. "sk-…a1b2"
  extraHeaders: Record<string, string>; httpProxy?: string | null;
  priceMultiplier: number; priceKey?: string | null; adapterId?: string | null; settings: Record<string, unknown>;
  enabled: boolean; sortOrder: number; notes?: string | null; website?: string | null;
  modelCount: number; boundClients: string[]; createdAt: string; updatedAt: string;
}
// GET /api/providers -> Provider[] ; GET /api/providers/{id} -> Provider
// POST /api/providers body ProviderCreate -> Provider
// PATCH /api/providers/{id} body Partial<ProviderUpdate> -> Provider  (apiKey: "" clears, omitted keeps)
// DELETE /api/providers/{id}  -> 409 {error, details:{boundClients}} when a client is bound
// POST /api/providers/{id}/duplicate -> Provider
// POST /api/providers/{id}/test body {modelId?} -> ProviderTestResult
// GET /api/providers/{id}/remote-models -> RemoteModel[]
// GET /api/providers/{id}/template-update -> {currentVersion, latestVersion, changes: string[]} ; POST same path -> Provider
// PUT /api/providers/order body {ids: string[]}
// priceKey: "" selects only official pricing; null/omitted inherits the template default.
export interface ProviderCreate {
  templateId?: string | null; variantId?: string | null; name?: string | null; apiKey?: string | null;
  endpoints?: TemplateEndpoint[] | null; authScheme?: AuthScheme | null; priceKey?: string | null;
  /** Model ids to add initially; defaults to the template's model list. */
  models?: string[] | null;
}
export interface ProviderUpdate {
  name: string; apiKey: string; endpoints: TemplateEndpoint[]; preferredUpstreamProtocols: ApiProtocol[];
  authScheme: AuthScheme; extraHeaders: Record<string, string>; httpProxy: string | null; priceMultiplier: number;
  priceKey: string | null; settings: Record<string, unknown>; enabled: boolean; notes: string | null;
}
export interface ProviderTestResult { ok: boolean; latencyMs: number; httpStatus?: number | null; protocol: ApiProtocol; model?: string | null; error?: string | null }
export interface RemoteModel { id: string; displayName?: string | null; linkedSystemModelId?: string | null; alreadyAdded: boolean }

export type FieldOrigin = "inherited" | "overridden" | "unset";
export interface ModelOverrides {
  displayName?: string | null; contextWindow?: number | null; maxOutputTokens?: number | null;
  capabilities?: ModelCapabilities | null; pricing?: PricingSchedule | null;
}
/** A provider's model: only modelId is required, everything else is inherited from the linked system model. */
export interface ProviderModel {
  id: number; providerId: string; modelId: string; systemModelId?: string | null; enabled: boolean; sortOrder: number;
  overrides: ModelOverrides;
  effective: { displayName: string; contextWindow?: number | null; maxOutputTokens?: number | null; capabilities: ModelCapabilities };
  origins: Record<"displayName" | "contextWindow" | "maxOutputTokens" | "capabilities" | "pricing", FieldOrigin>;
  pricingSource: BillingResult["pricingSource"]; priceKey?: string | null; multiplier: number;
  effectivePricing?: PricingSchedule | null;
}
// GET /api/providers/{id}/models -> ProviderModel[]
// POST /api/providers/{id}/models body {modelIds: string[]} -> ProviderModel[]   (auto-links system models)
// PATCH /api/providers/{id}/models/{pmId} body {systemModelId?, overrides?, enabled?} -> ProviderModel
//    (send overrides with a field set to null to "reset to inherited")
// DELETE /api/providers/{id}/models/{pmId}

// ---------- clients ----------
export type ClientKind = "codex" | "claude-code" | "gemini-cli" | "opencode" | "claude-desktop" | "grok-build";
export interface ClientInfo {
  kind: ClientKind; name: string; protocol: ApiProtocol;
  availability: "available" | "coming_soon"; availabilityReason?: string | null; mode: "switch" | "coexist";
  detection: { installed: boolean; configExists: boolean; version?: string | null; configPaths: string[] };
  status: "disabled" | "enabled" | "drifted"; enabled: boolean;
  providerId?: string | null; accountId?: string | null; selectedModel?: string | null; extras: Record<string, unknown>;
  appliedAt?: string | null; warnings: string[]; requiresRestart: boolean;
  /** Enabled, not drifted, but Astra would now write different values (e.g. the gateway port changed). */
  configOutdated?: boolean;
}
export interface EnableRequest { providerId?: string | null; model?: string | null; extras?: Record<string, unknown> | null }
export interface ConfigChange { file: string; format: "toml" | "json" | "env"; keyPath: string; before?: string | null; after?: string | null }
export interface ConfigPreview { changes: ConfigChange[]; diffs: { file: string; unifiedDiff: string }[]; warnings: string[] }
export interface DisableResult { restored: string[]; drifted: string[]; client: ClientInfo }
export interface BackupInfo { id: string; createdAt: string; files: string[]; firstWrite: boolean }
// GET /api/clients -> ClientInfo[]
// PUT /api/clients/{kind}/binding body {providerId} -> ClientInfo      (takes effect immediately, no config rewrite)
// POST /api/clients/{kind}/preview-enable body EnableRequest -> ConfigPreview
// POST /api/clients/{kind}/enable body EnableRequest -> ClientInfo
// POST /api/clients/{kind}/disable -> DisableResult
// POST /api/clients/{kind}/force-restore -> DisableResult
// GET /api/clients/{kind}/backups -> BackupInfo[] ; POST /api/clients/{kind}/backups/{id}/restore -> ClientInfo
// POST /api/clients/{kind}/rotate-key -> ClientInfo   (rewrites the key in the client config when enabled)
// GET /api/clients/{kind}/models -> string[]   (enabled models of the bound provider, for model pickers)

// ---------- requests & stats ----------
export type RequestStatus = "success" | "upstream_error" | "gateway_error" | "client_cancelled" | "blocked";
export interface RequestSummary {
  id: string; startedAtUtc: string; clientKind?: string | null; providerId?: string | null; providerName?: string | null;
  inboundProtocol: ApiProtocol; upstreamProtocol?: ApiProtocol | null; passthrough: boolean;
  requestedModel?: string | null; upstreamModel?: string | null; systemModelId?: string | null; stream: boolean;
  /** Model id declared by the raw upstream response; absent for old logs or unreported models. */
  responseModel?: string | null;
  status: RequestStatus; httpStatus?: number | null; ttftMs?: number | null; totalMs?: number | null; outputTps?: number | null;
  /** Reasoning effort the client explicitly set (verbatim, e.g. "high" / "xhigh" / "none"); null = unset or an older log. Never inferred. */
  reasoningEffort?: string | null;
  /** Explicit mode: Anthropic thinking.type ("adaptive" | "enabled" | "disabled") or Gemini semantics ("auto" for -1, "disabled" for 0). */
  reasoningMode?: string | null;
  /** Raw thinking budget tokens the client set (Anthropic budget_tokens, Gemini thinkingBudget > 0); never mapped to a level. */
  reasoningBudgetTokens?: number | null;
  /** Input total includes cache reads and writes; output total includes reasoning. */
  totalInputTokens: number; totalOutputTokens: number;
  cacheReadTokens: number; cacheWriteTokens: number; reasoningTokens: number;
  costNanoUsd: number; usageSource: "reported" | "missing";
}
export interface RequestUsageItem {
  tokenType: string; tokens: number; isPerCall: boolean; unitPrice: string; baseUnitPrice?: string | null;
  tierApplied: string; pricedAs?: string | null; multipliers?: string[] | null; costNanoUsd: number; note?: string | null;
}
export interface RequestDetail extends RequestSummary {
  serviceTier?: string | null; errorType?: string | null; errorMessage?: string | null; upstreamRequestId?: string | null;
  ttfbMs?: number | null; generationMs?: number | null;
  usageRaw?: unknown; pricingSnapshot?: PricingSchedule | null; pricingSource?: string | null; priceKey?: string | null;
  billingTrace: BillingTraceStep[]; billingDescription?: string | null; usageItems: RequestUsageItem[];
  userAgent?: string | null;
  /** Present only when debug body capture was on for this request. */
  bodies?: { clientRequest?: string | null; upstreamRequest?: string | null; upstreamResponse?: string | null; clientResponse?: string | null } | null;
  /** Privacy guard outcome (plan §6.7); present when the guard produced a report for this request. */
  privacy?: PrivacyReport | null;
}
// GET /api/requests?from=&to=&client=&provider=&model=&status=&page=1&pageSize=50 -> Page<RequestSummary>
// GET /api/requests/{id} -> RequestDetail
export interface StatsSummary {
  range: Range; costUsd: number; requests: number; successRate: number;
  inputTokens: number; outputTokens: number;
  cacheReadTokens: number; cacheWriteTokens: number; reasoningTokens: number;
  avgTtftMs?: number | null;
}
export interface TimeseriesPoint { bucket: string; key: string; costUsd: number; requests: number; tokens: number }
/** inputTokens already includes cacheReadTokens; hit rate = cacheReadTokens / inputTokens. */
export interface TopModel { model: string; costUsd: number; requests: number; tokens: number; inputTokens: number; cacheReadTokens: number }
// GET /api/stats/summary?range=&client= -> StatsSummary
// GET /api/stats/timeseries?range=&groupBy=day|hour&by=model|provider|client&client=&tzOffset= -> TimeseriesPoint[]
//    (tzOffset = viewer's UTC offset in minutes; buckets are local "yyyy-MM-dd" / "yyyy-MM-ddTHH:00")
// GET /api/stats/top-models?range=&limit=10&client= -> TopModel[]
// `client` (a ClientKind) narrows any stats call to that client's requests.

// ---------- privacy guard (M11) ----------
export type PrivacyAction = "off" | "warn" | "block" | "redact";
export interface PrivacyCustomRule { id: string; name: string; pattern: string; enabled: boolean }
export interface PrivacySettingsDto {
  enabled: boolean; dryRun: boolean; restoreResponses: boolean; defaultAction: PrivacyAction;
  /** Store masked match samples in the per-request report (originals are never stored either way). */
  recordSamples: boolean;
  /** Category (or custom rule id) → action override. */
  categoryActions: Record<string, PrivacyAction>;
  /** Client kind → default action for that client's requests. */
  clientDefaults: Record<string, PrivacyAction>;
  customRules: PrivacyCustomRule[];
}
export interface PrivacyRuleInfo { id: string; category: string; description: string }
export interface PrivacyOverview { settings: PrivacySettingsDto; rules: PrivacyRuleInfo[] }
// GET /api/privacy -> PrivacyOverview ; PUT /api/privacy body PrivacySettingsDto -> PrivacySettingsDto
export interface PrivacyHit {
  ruleId: string; category: string; action: Exclude<PrivacyAction, "off">; count: number;
  /** Masked previews of the matched values (e.g. "john••••om"); empty when sample recording is off. */
  samples?: string[];
}
export interface PrivacyDryRunResult { blocked: boolean; hits: PrivacyHit[]; wouldRedact: number; categories: string[] }
/** Per-request guard outcome, stored in requests.privacy_json (originals of redacted values are never kept). */
export interface PrivacyReport { dryRun: boolean; blocked: boolean; redactions: number; hits: PrivacyHit[] }
// POST /api/privacy/dry-run body {text?, json?, clientKind?} -> PrivacyDryRunResult
// Every request also carries `privacy` in requests.privacy_json (hits/actions; originals are never stored).
/** One request that produced a guard outcome (privacy interception log). */
export interface PrivacyEvent {
  id: string; startedAtUtc: string; clientKind?: string | null; providerId?: string | null; providerName?: string | null;
  requestedModel?: string | null; status: RequestStatus; dryRun: boolean; blocked: boolean; redactions: number; hits: PrivacyHit[];
}
export interface PrivacyCategoryStat {
  category: string; requests: number; hits: number;
  warnHits: number; blockHits: number; redactHits: number;
}
// GET /api/privacy/events?from=&to=&client=&provider=&category=&action=&blocked=&page=1&pageSize=50 -> Page<PrivacyEvent>
export interface PrivacyEventStats { events: number; blocked: number; dryRun: number; redactions: number; categories: PrivacyCategoryStat[] }
// GET /api/privacy/events/stats?range=&client=&provider=&category=&action= -> PrivacyEventStats

// ---------- subscription accounts (M12) ----------
export type AccountStatus = "active" | "expired" | "revoked";
export interface QuotaWindow { usedPercent?: number; windowMinutes?: number; resetsAtUtc?: string | null }
export interface AccountQuota {
  fetchedAtUtc?: string | null;
  session?: QuotaWindow | null;   // Claude 5h / Codex primary window
  weekly?: QuotaWindow | null;    // Claude 7d / Codex secondary window
  credits?: { usedPercent?: number; monthlyLimit?: number | null; prepaidBalance?: number | null } | null; // Grok
  planLabel?: string | null;
}
export interface ProviderAccount {
  id: string; providerId: string; displayName: string; accountEmail?: string | null; plan?: string | null;
  status: AccountStatus; expiresAtUtc?: string | null; lastRefreshAtUtc?: string | null; createdAt: string;
  quota?: AccountQuota | null;
}
// GET /api/providers/{id}/accounts -> ProviderAccount[]   (tokens never leave the server)
// POST /api/provider-accounts/{id}/refresh -> ProviderAccount (409 {error, status} when refresh failed)
// POST /api/provider-accounts/{id}/quota -> ProviderAccount (501 {error} when the family has no probe)
// DELETE /api/provider-accounts/{id}
export type SubscriptionLoginStart =
  | { mode: "pkce"; state: string; authorizeUrl: string }
  | { mode: "device"; state: string; userCode: string; verificationUrl?: string | null; interval: number };
// POST /api/providers/{id}/accounts/login body {accountId?} -> SubscriptionLoginStart
//   (400 {error, needsVerification: true} while the provider's OAuth flow is unverified)
export type SubscriptionPollResult =
  | { status: "pending" | "slow_down" }
  | { status: "done"; account: ProviderAccount }
  | { status: "expired" | "denied" | "error"; error?: string | null };
// POST /api/providers/{id}/accounts/login/{state}/poll -> SubscriptionPollResult

// ---------- settings ----------
export interface Settings {
  locale: "zh" | "en"; debugBodies: boolean; bodyRetentionDays: number; requestRetentionDays: number | null;
  effortBudgets: { low: number; medium: number; high: number }; streamIdleTimeoutSec: number;
  updateChannel: "stable" | "beta"; updateAutoCheck: boolean;
  /** Read-only here (changed via CLI / config.json): */
  port: number; host: string; dataDir: string; gatewayBaseUrl: string;
}
// GET /api/settings -> Settings ; PATCH /api/settings body Partial<Settings> -> Settings

// ---------- update feed ----------
// GET /api/update/status ; POST /api/update/check
export interface UpdateStatus {
  current: string; available?: string | null; lastCheckAt?: string | null; notes?: string | null;
  error?: string | null; channel: string; autoCheck: boolean; feedConfigured: boolean;
}

