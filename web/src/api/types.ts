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
  /** Balance / quota query settings (settings.quota is managed only through the quota endpoints). */
  quotaConfig: ProviderQuotaConfig;
  /** Last snapshot; on a failed query the previous plans stay and `error` / `errorCode` describe the failure. */
  quota?: ProviderQuotaSnapshot | null;
  quotaCheckedAtUtc?: string | null;
}

// ---------- provider import (from CC Switch / Alma / Claude Code / Codex / Magpie) ----------
export type ImportStatus = 'new' | 'same' | 'sameHost' | 'off' | 'skip';
export interface ImportCandidate {
  ref: string; source: string; name: string; fromApp?: string | null;
  endpoints: { protocol: ApiProtocol; baseUrl: string }[]; authScheme: AuthScheme;
  hasKey: boolean; keyMasked?: string | null; keyFingerprint?: string | null;
  models: string[]; headers: string[]; templateId?: string | null;
  status: ImportStatus; existing?: { id: string; name: string } | null;
  off: boolean; skipReason?: string | null; ignoredFields: string[];
}
export interface ImportSource {
  id: string; name: string; path?: string | null; found: boolean; error?: string | null; items: ImportCandidate[];
}
export interface ImportSelection { source: string; ref: string }
export interface ImportResult {
  added: { id: string; name: string }[]; skipped: { ref: string; reason: string }[];
}

// ---------- provider balance / quota query ----------
export type QuotaErrorCode =
  | "config" | "no_key" | "unauthorized" | "no_endpoint" | "rate_limited" | "upstream"
  | "timeout" | "network" | "not_json" | "too_large" | "empty";
/** One balance or usage window. Money plans carry remaining/total/used + unit; plan windows carry usedPercent. */
export interface ProviderQuotaPlan {
  name?: string | null; unit?: string | null; remaining?: number | null; total?: number | null; used?: number | null;
  usedPercent?: number | null; resetsAtUtc?: string | null; windowMinutes?: number | null; extra?: string | null;
}
export interface ProviderQuotaSnapshot {
  fetchedAtUtc?: string | null; template?: string | null; kind?: "balance" | "plan" | null;
  isValid?: boolean | null; invalidMessage?: string | null; planLabel?: string | null; plans?: ProviderQuotaPlan[] | null;
  error?: string | null; errorCode?: QuotaErrorCode | null; errorAtUtc?: string | null; failures?: number | null;
}
export interface ProviderQuotaRequest { method: "GET" | "POST"; url: string; auth: "provider" | "none"; headers?: Record<string, string> }
export interface ProviderQuotaConfig {
  enabled: boolean;
  /** null = the suggested template; "custom" = request + extract below. */
  template?: string | null; suggestedTemplate?: string | null; effectiveTemplate?: string | null;
  /** null = the global interval; 0 = never in the background. */
  intervalMinutes?: number | null; effectiveIntervalMinutes: number;
  timeoutSec: number; baseUrl?: string | null; params: Record<string, string>;
  /** Names of the stored secret parameters (values never leave the server). */
  secrets: string[];
  request?: ProviderQuotaRequest | null; extract?: Record<string, unknown> | null;
}
/** PUT body: omitted fields keep their value; secrets: "" removes one, an omitted name keeps it. */
export interface ProviderQuotaConfigInput {
  enabled?: boolean; template?: string | null; intervalMinutes?: number | null; timeoutSec?: number; baseUrl?: string | null;
  params?: Record<string, string>; secrets?: Record<string, string>;
  request?: ProviderQuotaRequest | null; extract?: Record<string, unknown> | null;
}
export interface ProviderQuotaState { config: ProviderQuotaConfig; snapshot?: ProviderQuotaSnapshot | null; checkedAtUtc?: string | null }
export interface ProviderQuotaTest {
  ok: boolean; errorCode?: QuotaErrorCode | null; error?: string | null; httpStatus?: number | null; url?: string | null;
  raw?: unknown; snapshot?: ProviderQuotaSnapshot | null; template?: string | null;
}
export interface QuotaTemplate {
  id: string; name: string; kind: "balance" | "plan"; appliesTo: string[]; hosts: string[];
  params: { name: string; secret: boolean; required: boolean }[];
  request: ProviderQuotaRequest; extract: Record<string, unknown>;
}
// GET /api/provider-quota/templates -> QuotaTemplate[]
// GET /api/providers/{id}/quota -> ProviderQuotaState
// POST /api/providers/{id}/quota -> ProviderQuotaState (400 {error, details:{errorCode}} for config / no_key)
// POST /api/providers/{id}/quota/test body ProviderQuotaConfigInput -> ProviderQuotaTest (nothing saved)
// PUT /api/providers/{id}/quota/config body ProviderQuotaConfigInput -> ProviderQuotaState
// GET /api/providers -> Provider[] ; GET /api/providers/{id} -> Provider
// POST /api/providers body ProviderCreate -> Provider
// PATCH /api/providers/{id} body Partial<ProviderUpdate> -> Provider  (apiKey: "" clears, omitted keeps)
// DELETE /api/providers/{id}  -> 409 {error, details:{boundClients}} when a client is bound
// POST /api/providers/{id}/duplicate -> Provider
// POST /api/providers/{id}/test body ProviderTestRequest -> text/event-stream of ProviderTestEvent
//   Validation failures (unknown provider, bad parameter) answer with plain JSON {error} instead.
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
/** Body of one provider connection test; every field is optional (the server fills in the defaults). */
export interface ProviderTestRequest {
  modelId?: string | null;
  /** Upstream protocol; defaults to the provider's preferred one. */
  protocol?: ApiProtocol | null;
  stream?: boolean;
  prompt?: string | null;
  maxOutputTokens?: number;
}
/** The "done" event of a test stream: whether it worked, what it cost in time, and what came back. */
export interface ProviderTestDone {
  ok: boolean; error?: string | null; httpStatus?: number | null;
  /** Milliseconds to the upstream response headers; null when the request never reached one. */
  httpMs?: number | null;
  /** Milliseconds to the first token of the answer; null when the upstream sent none. */
  ttftMs?: number | null;
  totalMs?: number | null;
  text: string; reasoning: string;
  /** The upstream body as received (credentials redacted, truncated past 256 KiB). */
  raw: string; rawTruncated: boolean; contentType?: string | null;
  /** The upstream response headers, credentials redacted. */
  headers?: Record<string, string> | null;
  responseModel?: string | null; requestId: string;
  usage?: { inputTokens: number; outputTokens: number; raw?: unknown } | null;
}
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
export type ClientKind =
  | "codex" | "claude-code" | "gemini-cli" | "opencode" | "claude-desktop" | "grok-build"
  | "pi" | "hermes-agent" | "minimax-code" | "copilot-cli" | "vscode-copilot"
  | "crush" | "qwen-code" | "droid" | "kimi-code" | "zed"
  | "vscode-insiders" | "vscodium" | "omp" | "mimo-code" | "deepseek-harness" | "workbuddy";
export interface ClientInfo {
  kind: ClientKind; name: string; protocol: ApiProtocol;
  availability: "available" | "coming_soon"; availabilityReason?: string | null; mode: "switch" | "coexist";
  detection: { installed: boolean; configExists: boolean; version?: string | null; configPaths: string[] };
  status: "disabled" | "enabled" | "drifted"; enabled: boolean;
  providerId?: string | null; accountId?: string | null; selectedModel?: string | null; extras: Record<string, unknown>;
  appliedAt?: string | null; warnings: string[]; requiresRestart: boolean;
  /** Enabled, not drifted, but Astra would now write different values (e.g. the gateway port changed). */
  configOutdated?: boolean;
  /** Token written into this client's config (as "<token>.<kind>"); "default" when never chosen. */
  tokenId?: string | null;
  install?: ClientInstall | null;
}
/**
 * How the client is installed and kept up to date. latestVersion comes from the npm registry (only after
 * POST /api/clients/check-updates). blocker: why install/update is unavailable — "npm-missing",
 * "vscode-missing" or "external-install" (installed another way, no updater of its own).
 */
export interface ClientInstall {
  method: "npm" | "vscode-extension" | "manual";
  package?: string | null; homepageUrl: string;
  installed: boolean; executable?: string | null; version?: string | null;
  latestVersion?: string | null; latestCheckedAt?: string | null; latestError?: string | null; updateAvailable: boolean;
  installCommand?: string | null; updateCommand?: string | null; updateVia?: "npm" | "self" | null;
  blocker?: "npm-missing" | "vscode-missing" | "external-install" | "shadowed" | null; busy: boolean;
  /** npm's global directories are not writable for this user (root-owned): the npm command needs admin rights. */
  needsAdmin?: boolean;
  /** Astra can show the system's admin prompt (macOS osascript / Linux pkexec) and run npm elevated. */
  adminAvailable?: boolean;
  /** One-time terminal fix (`sudo chown -R "$(whoami)" …`) after which installs no longer need admin rights. */
  adminFixCommand?: string | null;
  /** Other copies of the command behind the one that runs (terminal PATH order); a newer one is shadowed. */
  otherCopies?: { path: string; version: string; newer: boolean }[] | null;
  /** The running copy is outside the user's terminal PATH (e.g. only in ~/.npm-global/bin). */
  notOnPath?: boolean;
}
/**
 * hint — failed runs: "permission-denied" (npm -g cannot write its global directories), "admin-cancelled" (the admin
 * prompt was dismissed), "timeout"; succeeded runs that did not take effect: "shadowed" (the new version sits behind
 * an older copy on PATH), "unchanged", "not-on-path". elevated: ran with admin rights (output may only arrive at the
 * end; cannot be stopped).
 */
export interface ClientInstallJob {
  kind: ClientKind; action: "install" | "update"; state: "running" | "succeeded" | "failed" | "cancelled";
  command: string; startedAt: string; finishedAt?: string | null; exitCode?: number | null; log: string[];
  hint?: "permission-denied" | "admin-cancelled" | "timeout" | "shadowed" | "unchanged" | "not-on-path" | null; elevated?: boolean;
}
// POST /api/clients/check-updates body {force?} -> ClientInfo[]
// POST /api/clients/{kind}/install body {action: "install"|"update", elevated?} -> ClientInstallJob ; GET same path -> current/last job (404 none)
// POST /api/clients/{kind}/install/cancel -> ClientInstallJob
/** tokenId: omitted keeps the client's current token (default token when it has none). */
export interface EnableRequest { providerId?: string | null; model?: string | null; extras?: Record<string, unknown> | null; tokenId?: string | null }
export interface ConfigChange { file: string; format: "toml" | "json" | "env" | "yaml"; keyPath: string; before?: string | null; after?: string | null }
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
// GET /api/clients/{kind}/models -> string[]   (enabled models of the bound provider, for model pickers)

// ---------- tokens ----------
/** Usage of one token: `today` from the request log (server-local day), `total` from lifetime counters. */
export interface TokenStats {
  costUsd: number; requests: number; inputTokens: number; outputTokens: number; totalTokens: number;
  cacheReadTokens: number; cacheWriteTokens: number;
  /** cacheReadTokens / inputTokens; null without input. */
  cacheHitRate?: number | null;
  /** Weighted output speed: output tokens / generation seconds of successful requests; null without data. */
  tps?: number | null;
}
export interface Token {
  id: string; name: string; isDefault: boolean; enabled: boolean;
  /** e.g. "sk-astra-AbC123…" — the plaintext only comes from /reveal. */
  keyMasked: string;
  /** Provider (and pinned subscription account) for direct calls with the bare token; null = direct calls rejected. */
  providerId?: string | null; accountId?: string | null;
  createdAt: string; updatedAt: string; lastUsedAt?: string | null;
  /** Enabled clients whose config uses this token. */
  clients: ClientKind[];
  today: TokenStats; total: TokenStats;
}
export interface TokenMutationResult { token?: Token | null; rewritten: ClientKind[]; skipped: ClientKind[] }
// GET /api/tokens -> Token[]
// POST /api/tokens body {name, providerId?, accountId?} -> Token
// PATCH /api/tokens/{id} body {name?, enabled?, providerId?, accountId?} -> Token   (never touches client configs)
// POST /api/tokens/{id}/reset -> TokenMutationResult   (new key; rewrites the configs of its enabled clients)
// POST /api/tokens/{id}/reveal -> {token}
// DELETE /api/tokens/{id} -> TokenMutationResult       (default token: 409; clients move to the default token)

// ---------- requests & stats ----------
/** "pending" only ever comes from the live feed: the gateway is still handling the request (never stored). */
export type RequestStatus = "success" | "upstream_error" | "gateway_error" | "client_cancelled" | "blocked" | "pending";
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
  /** Token that authenticated the request (current name, or the snapshot once the token was deleted). */
  tokenId?: string | null; tokenName?: string | null;
  /** Subscription account that served the request (after any failover); name is null once the account is deleted. */
  accountId?: string | null; accountName?: string | null;
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
  bodies?: { clientRequest?: string | null; upstreamRequest?: string | null; upstreamRequestHeaders?: string | null;
    upstreamResponse?: string | null; clientResponse?: string | null } | null;
  /** Privacy guard outcome (plan §6.7); present when the guard produced a report for this request. */
  privacy?: PrivacyReport | null;
}
// GET /api/requests?from=&to=&client=&token=&provider=&model=&status=&page=1&pageSize=50 -> Page<RequestSummary>
// GET /api/requests/{id} -> RequestDetail (served from the live feed while the request is in flight)
/** One frame of GET /api/requests/live (SSE): the in-flight snapshot first, then every change. */
export interface RequestLiveEvent {
  /** started / updated: the row as it stands now; persisted: the row is in the database and list queries return it. */
  type: "started" | "updated" | "persisted";
  request: RequestSummary;
}
export interface StatsSummary {
  range: Range; costUsd: number; requests: number; successRate: number;
  inputTokens: number; outputTokens: number;
  cacheReadTokens: number; cacheWriteTokens: number; reasoningTokens: number;
  avgTtftMs?: number | null;
}
export interface TimeseriesPoint { bucket: string; key: string; costUsd: number; requests: number; tokens: number }
/** inputTokens already includes cacheReadTokens; hit rate = cacheReadTokens / inputTokens. */
export interface TopModel { model: string; costUsd: number; requests: number; tokens: number; inputTokens: number; cacheReadTokens: number }
/** One local day of the activity heatmap; days without requests are absent. */
export interface DailyActivity { day: string; requests: number }
// GET /api/stats/summary?range=&client=&token= -> StatsSummary
// GET /api/stats/timeseries?range=&groupBy=day|hour&by=model|provider|client|token&client=&token=&tzOffset= -> TimeseriesPoint[]
//    (tzOffset = viewer's UTC offset in minutes; buckets are local "yyyy-MM-dd" / "yyyy-MM-ddTHH:00")
// GET /api/stats/top-models?range=&limit=10&client=&token= -> TopModel[]
// GET /api/stats/activity-heatmap?days=365&client=&token=&tzOffset= -> DailyActivity[]
//    (a fixed window ending today, 30-730 days; it ignores the range selector's 今天/7d/30d/90d)
// `client` (a ClientKind) / `token` (a token id) narrow any stats call to those requests.

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
  credits?: { usedPercent?: number; resetsAtUtc?: string | null; monthlyLimit?: number | null; prepaidBalance?: number | null } | null; // Grok / Copilot premium requests
  planLabel?: string | null;
  /** GitHub Copilot: premium-request quota (Copilot bills premium requests, not tokens). */
  entitlement?: number | null;
  remaining?: number | null;
  overageUsed?: number | null;
  overagePermitted?: boolean | null;
  /** Copilot login reported by /copilot_internal/user. */
  account?: string | null;
}
export interface ProviderAccount {
  id: string; providerId: string; displayName: string; accountEmail?: string | null; plan?: string | null;
  status: AccountStatus; expiresAtUtc?: string | null; lastRefreshAtUtc?: string | null; createdAt: string;
  quota?: AccountQuota | null;
  /** codex 的额度重置卡快照（列表接口实时刷新）。 */
  credits?: ResetCreditList | null;
  /** User switch: a disabled account is never used for requests. */
  enabled: boolean;
  /** The account requests go to right now (unless a client / token pins another one). */
  isCurrent: boolean;
  /** Failover order (ascending); the list comes back sorted by it. */
  sortOrder: number;
  /** Rate-limited until then (automatic failover skips it meanwhile). */
  cooldownUntilUtc?: string | null;
  lastError?: string | null;
}
/** Who may use a subscription provider and how its accounts switch. */
export interface SubscriptionPolicy {
  clientPolicy: 'claude-code-only' | 'any';
  switchMode: 'manual' | 'failover';
  /** Claude Pro/Max subscription: the client policy is shown (and defaults to Claude Code only). */
  claudeSubscription: boolean;
  /** Non-Claude-Code callers present a Claude Code identity (see the gateway's ClaudeCodeMimicry). */
  mimicClaudeCode: boolean;
}
// GET|PUT /api/providers/{id}/subscription-policy  (PUT body: Partial<{clientPolicy, switchMode, mimicClaudeCode}>) -> SubscriptionPolicy
// POST /api/provider-accounts/{id}/activate -> ProviderAccount[]   (409 {error, status} when the login is dead)
// PATCH /api/provider-accounts/{id} body {enabled?, displayName?} -> ProviderAccount[]
// PUT /api/providers/{id}/accounts/order body {ids} -> ProviderAccount[]
/** One rate-limit reset card (codex); `status` is available | redeemed | expired. */
export interface ResetCredit {
  id: string; reset_type?: string | null; is_supported_by_plan?: boolean | null; status: string;
  granted_at?: string | null; expires_at?: string | null; redeemed_at?: string | null;
  title?: string | null; description?: string | null;
}
export interface ResetCreditList {
  credits?: ResetCredit[] | null; available_count?: number | null; total_earned_count?: number | null;
  history_enabled?: boolean | null;
}
// GET /api/provider-accounts/{id}/reset-credits -> ResetCreditList
export interface ResetCreditResult { account: ProviderAccount; quota?: AccountQuota | null }
// POST /api/provider-accounts/{id}/reset-credits/{creditId}/consume -> ResetCreditResult
// GET /api/providers/{id}/accounts -> ProviderAccount[]   (tokens never leave the server)
// POST /api/provider-accounts/{id}/refresh -> ProviderAccount (409 {error, status} when refresh failed)
// POST /api/provider-accounts/{id}/quota -> ProviderAccount (501 {error} when the family has no probe)
// DELETE /api/provider-accounts/{id}
export type SubscriptionLoginStart =
  | { mode: "pkce"; state: string; authorizeUrl: string }
  | { mode: "device"; state: string; userCode: string; verificationUrl?: string | null; interval: number }
  // Server-mediated flow (ZAI CLI): open the authorize URL, then the server polls until done.
  | { mode: "cli"; state: string; authorizeUrl: string; interval: number }
  // Manual paste (Claude): the client's registered redirect_uri is not a loopback address, so the
  // authorization page shows the code and the user pastes it back to finish the login.
  | { mode: "paste"; state: string; authorizeUrl: string };
// POST /api/providers/{id}/accounts/login body {accountId?} -> SubscriptionLoginStart
//   (400 {error, needsVerification: true} while the provider's OAuth flow is unverified)
// POST /api/providers/{id}/accounts/login/{state}/complete body {code} -> {status:"done", account}
//   (400 {status:"error", error} when the pasted code could not be exchanged)
export type SubscriptionPollResult =
  | { status: "pending" | "slow_down" }
  | { status: "done"; account: ProviderAccount }
  | { status: "expired" | "denied" | "error"; error?: string | null };
// POST /api/providers/{id}/accounts/login/{state}/poll -> SubscriptionPollResult
/** A `codex login` already present on this machine (~/.codex/auth.json); tokens never cross the wire. */
export interface LocalCodexLogin {
  available: boolean; accountEmail?: string | null; plan?: string | null; detail?: string | null;
}
// GET /api/subscription/codex/local-login -> LocalCodexLogin
export interface ImportedCodexAccount {
  account: ProviderAccount; quota?: AccountQuota | null;
  /** Set when the account was imported but its first verification failed. */
  warning?: string | null;
}
// POST /api/providers/{id}/accounts/import-codex -> ImportedCodexAccount
/**
 * A GitHub authorization already present on this machine (the VS Code GitHub session, GH_TOKEN, or the
 * Copilot plugin config) that could back a Copilot account; the token itself never crosses the wire.
 */
export interface LocalCopilotLogin {
  available: boolean; source?: string | null; detail?: string | null;
}
// GET /api/subscription/copilot/local-login -> LocalCopilotLogin
export interface ImportCopilotRequest {
  /** A GitHub token the user pasted; omitted/empty means "probe this machine". */
  token?: string | null;
  /** Adopts the token into an existing account row instead of creating a new one. */
  accountId?: string | null;
}
// POST /api/providers/{id}/accounts/import-copilot body ImportCopilotRequest -> ImportedCodexAccount

// ---------- settings ----------
export interface Settings {
  locale: "zh" | "en"; debugBodies: boolean; bodyRetentionDays: number; requestRetentionDays: number | null;
  effortBudgets: { low: number; medium: number; high: number }; streamIdleTimeoutSec: number;
  updateChannel: "stable" | "beta"; updateAutoCheck: boolean;
  /** Background balance / quota refresh of API-key providers, minutes (0 = off; providers may override). */
  quotaAutoIntervalMinutes: number;
  /** Outbound proxy: system = env vars, then the OS setting; custom = proxyUrl; direct = never. Providers' own proxy wins. */
  proxyMode: ProxyMode; proxyUrl: string | null; proxyUsername: string | null; hasProxyPassword: boolean;
  /** NO_PROXY-style hosts, comma separated, that stay direct in custom mode. */
  proxyBypass: string | null;
  /** Read-only here (changed via CLI / config.json): */
  port: number; host: string; dataDir: string; gatewayBaseUrl: string;
}
export type ProxyMode = "system" | "custom" | "direct";
/** proxyPassword is write-only (null / "" clears it); reads only report hasProxyPassword. */
export type SettingsPatch = Partial<Settings> & { proxyPassword?: string | null };
// GET /api/settings -> Settings ; PATCH /api/settings body SettingsPatch -> Settings

// ---------- update feed ----------
// GET /api/update/status ; POST /api/update/check
export interface UpdateStatus {
  current: string; available?: string | null; lastCheckAt?: string | null; notes?: string | null;
  error?: string | null; channel: string; autoCheck: boolean; feedConfigured: boolean;
}

