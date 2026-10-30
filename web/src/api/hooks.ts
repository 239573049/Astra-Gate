import { useMutation, useQuery, useQueryClient, type QueryKey } from '@tanstack/react-query';

import { api, apiBase, apiStream, apiText, enc, qs, type SseMessage } from './client';
import type {
  MarketListing,
  MarketSearchResult,
  PluginAuthMethod,
  PluginCheckinState,
  MagpieImportPreview,
  PluginHostStatus,
  PluginJob,
  PluginList,
  PluginPrompt,
  PluginSignIn,
  PluginUsage,
  AccountImportInfo,
  AccountImportRequest,
  AuthStatus,
  AutostartStatus,
  BackupInfo,
  BillingResult,
  BillingSimulateRequest,
  ClientBinding,
  ClientInfo,
  ClientInstallJob,
  ClientKind,
  ClaudeDirectRequest,
  ClaudeDirectSelect,
  ClaudeDirectState,
  ConfigPreview,
  DetectedCloudflared,
  DisableResult,
  DailyActivity,
  EnableRequest,
  ImportedCodexAccount,
  BatchImportResult,
  ImportResult,
  ImportSelection,
  ImportSource,
  ListenStatus,
  LocalCodexLogin,
  LocalCopilotLogin,
  Model,
  ModelDetail,
  ModelField,
  ModelInput,
  ModelPrice,
  Page,
  PriceKeyInfo,
  PricingSchedule,
  PrivacyDryRunResult,
  PrivacyEventStats,
  PrivacyOverview,
  PrivacySettingsDto,
  Provider,
  ProviderAccount,
  ProviderCreate,
  ProviderModel,
  ProviderQuotaConfigInput,
  ProviderQuotaState,
  ProviderQuotaTest,
  QuotaTemplate,
  ResetCreditList,
  ResetCreditResult,
  ProviderTemplate,
  ProviderTestRequest,
  ProviderUpdate,
  ProviderUsage,
  Range,
  RateStats,
  RemoteModel,
  RequestDetail,
  RequestPage,
  Settings,
  SettingsPatch,
  StatsSummary,
  SubscriptionLoginStart,
  SubscriptionPolicy,
  SubscriptionPollResult,
  SyncPreview,
  TimeseriesPoint,
  Token,
  TokenLimits,
  TokenMutationResult,
  TokenPoolInput,
  TopModel,
  ProviderStat,
  TunnelConfig,
  TunnelState,
  TunnelStatus,
  UpdateStatus,
  VersionInfo,
  ModelOverrides,
} from './types';
import type { PrivacyEvent } from './types';
import type { AlertEvent, AlertRule, AlertState, KeyInfo, KeyRequestPage, KeyStats, KeyStatsRange, Webhook, WebhookDelivery, WebhookFormat, WebhookSecret } from './types';
import type { TraceActivity, TraceRange, TraceRequest, TraceSession, TraceSessionDetail, TraceTopology } from './types';
import type { RoutingModel, SmartRouting, SmartRoutingTestRequest, SmartRoutingTestResult, SmartRoutingUpdate } from './types';

export const keys = {
  health: ['health'] as const,
  version: ['version'] as const,
  auth: ['auth'] as const,
  settings: ['settings'] as const,
  tunnel: ['tunnel'] as const,
  listen: ['listen'] as const,
  autostart: ['autostart'] as const,
  update: ['update'] as const,
  models: (search?: string, vendor?: string) => ['models', search ?? '', vendor ?? ''] as const,
  model: (id: string) => ['model', id] as const,
  priceKeys: ['price-keys'] as const,
  templates: ['provider-templates'] as const,
  quotaTemplates: ['provider-quota-templates'] as const,
  providers: ['providers'] as const,
  importSources: ['import-sources'] as const,
  provider: (id: string) => ['provider', id] as const,
  providerModels: (id: string) => ['provider-models', id] as const,
  remoteModels: (id: string) => ['remote-models', id] as const,
  templateUpdate: (id: string) => ['template-update', id] as const,
  clients: ['clients'] as const,
  claudeDirect: ['claude-direct'] as const,
  claudeDirectRequests: (id: string) => ['claude-direct-requests', id] as const,
  tokens: ['tokens'] as const,
  webhooks: ['webhooks'] as const,
  webhookDeliveries: (id: string) => ['webhook-deliveries', id] as const,
  alertEvents: ['alert-events'] as const,
  alertRules: ['alert-rules'] as const,
  alertStates: ['alert-states'] as const,
  clientModels: (kind: string) => ['client-models', kind] as const,
  backups: (kind: string) => ['backups', kind] as const,
  clientInstallJob: (kind: string) => ['client-install-job', kind] as const,
  privacy: ['privacy'] as const,
  smartRouting: ['smart-routing'] as const,
  routingModels: ['routing-models'] as const,
  privacyEvents: (q: PrivacyEventQuery) => ['privacy-events', q] as const,
  privacyEventStats: (...args: unknown[]) => ['privacy-event-stats', ...args] as const,
  providerAccounts: (id: string) => ['provider-accounts', id] as const,
  providerUsage: (id: string) => ['provider-usage', id] as const,
  subscriptionPolicy: (id: string) => ['subscription-policy', id] as const,
  localCodexLogin: ['local-codex-login'] as const,
  localCopilotLogin: ['local-copilot-login'] as const,
  plugins: ['plugins'] as const,
  pluginJob: (id: string) => ['plugin-job', id] as const,
  pluginHost: ['plugin-host'] as const,
  pluginMarket: ['plugin-market'] as const,
  pluginSearch: (q: string) => ['plugin-search', q] as const,
  pluginMethods: (provider: string) => ['plugin-methods', provider] as const,
  pluginSignIn: (provider: string, flow: string) => ['plugin-sign-in', provider, flow] as const,
  pluginUsage: (provider: string, account: string) => ['plugin-usage', provider, account] as const,
  pluginCheckin: (provider: string) => ['plugin-checkin', provider] as const,
  magpieImport: ['magpie-import'] as const,
  accountImportInfo: (id: string) => ['account-import-info', id] as const,
  resetCredits: (accountId: string) => ['reset-credits', accountId] as const,
  requests: (q: RequestQuery) => ['requests', q] as const,
  request: (id: string) => ['request', id] as const,
  stats: (kind: string, ...args: unknown[]) => ['stats', kind, ...args] as const,
  traceTopology: (range: TraceRange) => ['traces', 'topology', range] as const,
  traceSessions: (range: string, client?: string) => ['traces', 'sessions', range, client ?? ''] as const,
  traceSession: (key: string, turn?: string) => ['traces', 'session', key, turn ?? ''] as const,
  traceRequest: (id: string) => ['traces', 'request', id] as const,
  traceActivity: (limit: number) => ['traces', 'activity', limit] as const,
};

// ---------- system ----------

export const useHealth = () =>
  useQuery({
    queryKey: keys.health,
    queryFn: ({ signal }) => api<{ status: string }>('GET', '/api/health', undefined, signal),
    refetchInterval: 5000,
    retry: false,
  });

export const useVersion = () =>
  useQuery({ queryKey: keys.version, queryFn: () => api<VersionInfo>('GET', '/api/version'), staleTime: Infinity });

export const useAuthStatus = () =>
  useQuery({ queryKey: keys.auth, queryFn: () => api<AuthStatus>('GET', '/api/auth/status'), retry: false });

export function useLogin() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (password: string) => api('POST', '/api/auth/login', { password }),
    onSuccess: () => qc.invalidateQueries(),
  });
}

export function useLogout() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => api('POST', '/api/auth/logout'),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.auth }),
  });
}

export const useSettings = () =>
  useQuery({ queryKey: keys.settings, queryFn: () => api<Settings>('GET', '/api/settings') });

export function useUpdateSettings() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (patch: SettingsPatch) => api<Settings>('PATCH', '/api/settings', patch),
    onSuccess: (s) => qc.setQueryData(keys.settings, s),
  });
}

export function useSetAdminPassword() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (password: string) => api('POST', '/api/auth/password', { password }),
    onSuccess: () => Promise.all([qc.invalidateQueries({ queryKey: keys.tunnel }), qc.invalidateQueries({ queryKey: keys.listen })]),
  });
}

export const useListen = () =>
  useQuery({ queryKey: keys.listen, queryFn: () => api<ListenStatus>('GET', '/api/server/listen') });

/** Saves the listen address for the next start; the running server keeps its own until it restarts. */
export function useUpdateListen() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (host: string) => api<ListenStatus>('PUT', '/api/server/listen', { host }),
    onSuccess: (s) => qc.setQueryData(keys.listen, s),
  });
}

/** Whether the service starts at login (the entry `astra autostart` manages). */
export const useAutostart = () =>
  useQuery({ queryKey: keys.autostart, queryFn: () => api<AutostartStatus>('GET', '/api/server/autostart') });

/** Adds or removes the login entry; it applies at the next login and starts nothing now. */
export function useSetAutostart() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (enabled: boolean) => api<AutostartStatus>('PUT', '/api/server/autostart', { enabled }),
    onSuccess: (s) => qc.setQueryData(keys.autostart, s),
  });
}

// ---------- remote access ----------

const TUNNEL_BUSY: TunnelState[] = ['downloading', 'starting', 'reconnecting'];

/** Polls quickly while the tunnel is coming up, every 15 s while it runs, and only now and then while it is off. */
export const useTunnel = (enabled = true) =>
  useQuery({
    queryKey: keys.tunnel,
    queryFn: ({ signal }) => api<TunnelStatus>('GET', '/api/tunnel', undefined, signal),
    enabled,
    refetchInterval: (q) => {
      const s = q.state.data;
      if (s && TUNNEL_BUSY.includes(s.state)) return 1500;
      return s?.enabled ? 15_000 : 60_000;
    },
  });

export function useUpdateTunnel() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (config: TunnelConfig) => api<TunnelStatus>('PUT', '/api/tunnel', config),
    onSuccess: (s) => qc.setQueryData(keys.tunnel, s),
  });
}

/** Looks for a cloudflared on PATH; saves nothing. */
export function useDetectCloudflared() {
  return useMutation({ mutationFn: () => api<DetectedCloudflared>('POST', '/api/tunnel/cloudflared/detect') });
}

export function useToggleTunnel() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (on: boolean) => api<TunnelStatus>('POST', on ? '/api/tunnel/start' : '/api/tunnel/stop'),
    onSuccess: (s) => qc.setQueryData(keys.tunnel, s),
  });
}

export const useUpdateStatus = () =>
  useQuery({
    queryKey: keys.update,
    queryFn: ({ signal }) => api<UpdateStatus>('GET', '/api/update/status', undefined, signal),
    refetchInterval: 5 * 60_000,
  });

export function useCheckUpdate() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => api<UpdateStatus>('POST', '/api/update/check'),
    onSuccess: (s) => qc.setQueryData(keys.update, s),
  });
}

// ---------- models ----------

export const useModels = (search?: string, vendor?: string) =>
  useQuery({
    queryKey: keys.models(search, vendor),
    queryFn: () => api<Model[]>('GET', `/api/models${qs({ search, vendor })}`),
    placeholderData: (prev) => prev,
  });

export const useModel = (id: string | undefined) =>
  useQuery({
    queryKey: keys.model(id ?? ''),
    queryFn: () => api<ModelDetail>('GET', `/api/models/${enc(id!)}`),
    enabled: Boolean(id),
  });

export const usePriceKeys = () =>
  useQuery({ queryKey: keys.priceKeys, queryFn: () => api<PriceKeyInfo[]>('GET', '/api/price-keys') });

function invalidateModels(qc: ReturnType<typeof useQueryClient>, id?: string) {
  void qc.invalidateQueries({ queryKey: ['models'] });
  void qc.invalidateQueries({ queryKey: id ? keys.model(id) : ['model'] });
  void qc.invalidateQueries({ queryKey: keys.priceKeys });
  void qc.invalidateQueries({ queryKey: ['provider-models'] });
  void qc.invalidateQueries({ queryKey: ['client-models'] });
  void qc.invalidateQueries({ queryKey: ['remote-models'] });
}

export function useCreateModel() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: ModelInput) => api<Model>('POST', '/api/models', input),
    onSuccess: (m) => invalidateModels(qc, m.id),
  });
}

export function useUpdateModel(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: ModelInput) => api<Model>('PUT', `/api/models/${enc(id)}`, input),
    onSuccess: () => invalidateModels(qc, id),
  });
}

export function useDeleteModel() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api<void>('DELETE', `/api/models/${enc(id)}`),
    onSuccess: () => invalidateModels(qc),
  });
}

export function useResetModelField(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (field: ModelField) => api<Model>('POST', `/api/models/${enc(id)}/reset-field`, { field }),
    onSuccess: () => invalidateModels(qc, id),
  });
}

export function useUpsertModelPrice(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { priceKey: string; upstreamModelId?: string | null; pricing: PricingSchedule }) =>
      api<ModelPrice>('PUT', `/api/models/${enc(id)}/prices/${enc(v.priceKey)}`, {
        upstreamModelId: v.upstreamModelId ?? null,
        pricing: v.pricing,
      }),
    onSuccess: () => invalidateModels(qc, id),
  });
}

export function useDeleteModelPrice(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (priceKey: string) => api<void>('DELETE', `/api/models/${enc(id)}/prices/${enc(priceKey)}`),
    onSuccess: () => invalidateModels(qc, id),
  });
}

export const useSyncPreview = () =>
  useMutation({ mutationFn: () => api<SyncPreview>('POST', '/api/models/sync/preview') });

export function useSyncApply() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (changeIds: string[]) => api<{ applied: number }>('POST', '/api/models/sync/apply', { changeIds }),
    onSuccess: () => invalidateModels(qc),
  });
}

export const useSimulate = () =>
  useMutation({ mutationFn: (req: BillingSimulateRequest) => api<BillingResult>('POST', '/api/billing/simulate', req) });

// ---------- providers ----------

export const useTemplates = () =>
  useQuery({
    queryKey: keys.templates,
    queryFn: () => api<ProviderTemplate[]>('GET', '/api/provider-templates'),
    staleTime: 5 * 60_000,
  });

export const useProviders = () =>
  useQuery({ queryKey: keys.providers, queryFn: () => api<Provider[]>('GET', '/api/providers') });

export const useProvider = (id: string | undefined) =>
  useQuery({
    queryKey: keys.provider(id ?? ''),
    queryFn: () => api<Provider>('GET', `/api/providers/${enc(id!)}`),
    enabled: Boolean(id),
  });

function invalidateProviders(qc: ReturnType<typeof useQueryClient>, id?: string) {
  void qc.invalidateQueries({ queryKey: keys.providers });
  void qc.invalidateQueries({ queryKey: id ? keys.provider(id) : ['provider'] });
  void qc.invalidateQueries({ queryKey: keys.clients });
  void qc.invalidateQueries({ queryKey: ['client-models'] });
}

/** Read-only scan of the other apps' provider lists; keys never come back, only masks. Refetched on every open. */
export const useImportSources = (enabled: boolean) =>
  useQuery({
    queryKey: keys.importSources,
    queryFn: () => api<ImportSource[]>('GET', '/api/providers/import/sources'),
    enabled,
    staleTime: 0,
    gcTime: 0,
  });

export function useImportProviders() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (selections: ImportSelection[]) => api<ImportResult>('POST', '/api/providers/import', selections),
    onSuccess: () => {
      invalidateProviders(qc);
      void qc.invalidateQueries({ queryKey: keys.importSources });
    },
  });
}

export function useCreateProvider() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: ProviderCreate) => api<Provider>('POST', '/api/providers', body),
    onSuccess: (p) => invalidateProviders(qc, p.id),
  });
}

export function useUpdateProvider(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (patch: Partial<ProviderUpdate>) => api<Provider>('PATCH', `/api/providers/${enc(id)}`, patch),
    onSuccess: (p) => {
      qc.setQueryData(keys.provider(id), p);
      invalidateProviders(qc, id);
      void qc.invalidateQueries({ queryKey: keys.providerModels(id) });
      void qc.invalidateQueries({ queryKey: keys.remoteModels(id) });
    },
  });
}

/** Replaces the provider's model mapping (requested model id → provider model id); stored as settings.model_map. */
export function useSetProviderModelMap(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (map: Record<string, string>) => api<Provider>('PUT', `/api/providers/${enc(id)}/model-map`, { map }),
    onSuccess: (p) => {
      qc.setQueryData(keys.provider(id), p);
      invalidateProviders(qc, id);
    },
  });
}


export function useDeleteProvider() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api<void>('DELETE', `/api/providers/${enc(id)}`),
    onSuccess: () => invalidateProviders(qc),
  });
}

// ---------- provider balance / quota query ----------

export const useQuotaTemplates = () =>
  useQuery({
    queryKey: keys.quotaTemplates,
    queryFn: () => api<QuotaTemplate[]>('GET', '/api/provider-quota/templates'),
    staleTime: Infinity,
  });

/** Writes a fresh quota state into the cached provider (detail and list) without refetching everything. */
function applyQuotaState(qc: ReturnType<typeof useQueryClient>, id: string, state: ProviderQuotaState) {
  const patch = (p: Provider): Provider =>
    p.id === id ? { ...p, quotaConfig: state.config, quota: state.snapshot ?? null, quotaCheckedAtUtc: state.checkedAtUtc ?? null } : p;
  qc.setQueryData<Provider>(keys.provider(id), (prev) => (prev ? patch(prev) : prev));
  qc.setQueryData<Provider[]>(keys.providers, (prev) => prev?.map(patch));
}

/** Runs the provider's balance / quota query now and stores the snapshot. */
export function useFetchProviderQuota() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api<ProviderQuotaState>('POST', `/api/providers/${enc(id)}/quota`),
    onSuccess: (state, id) => applyQuotaState(qc, id, state),
    // A configuration error is also recorded on the snapshot: pick it up.
    onError: (_e, id) => void qc.invalidateQueries({ queryKey: keys.provider(id) }),
  });
}

export function useSaveProviderQuotaConfig(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: ProviderQuotaConfigInput) => api<ProviderQuotaState>('PUT', `/api/providers/${enc(id)}/quota/config`, body),
    onSuccess: (state) => applyQuotaState(qc, id, state),
  });
}

/** One unsaved run of a quota configuration (nothing is stored). */
export const useTestProviderQuota = (id: string) =>
  useMutation({
    mutationFn: (body: ProviderQuotaConfigInput) => api<ProviderQuotaTest>('POST', `/api/providers/${enc(id)}/quota/test`, body),
  });

export function useDuplicateProvider() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api<Provider>('POST', `/api/providers/${enc(id)}/duplicate`),
    onSuccess: () => invalidateProviders(qc),
  });
}

export function useReorderProviders() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (ids: string[]) => api<void>('PUT', '/api/providers/order', { ids }),
    onSuccess: () => invalidateProviders(qc),
  });
}

/**
 * Runs one provider connection test, reporting its progress as it arrives. Not a react-query mutation: the answer is
 * a server-sent event stream, not one response body.
 */
export function runProviderTest(id: string, request: ProviderTestRequest, onEvent: (message: SseMessage) => void, signal?: AbortSignal): Promise<void> {
  return apiStream(`/api/providers/${enc(id)}/test`, request, onEvent, signal);
}

export const useRemoteModels = (id: string, enabled: boolean) =>
  useQuery({
    queryKey: keys.remoteModels(id),
    queryFn: () => api<RemoteModel[]>('GET', `/api/providers/${enc(id)}/remote-models`),
    enabled,
    retry: false,
  });

export const useTemplateUpdate = (id: string, enabled: boolean) =>
  useQuery({
    queryKey: keys.templateUpdate(id),
    queryFn: () =>
      api<{ currentVersion: number; latestVersion: number; changes: string[] }>(
        'GET',
        `/api/providers/${enc(id)}/template-update`,
      ),
    enabled,
  });

export function useApplyTemplateUpdate(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => api<Provider>('POST', `/api/providers/${enc(id)}/template-update`),
    onSuccess: () => {
      invalidateProviders(qc, id);
      void qc.invalidateQueries({ queryKey: keys.providerModels(id) });
      void qc.invalidateQueries({ queryKey: keys.remoteModels(id) });
      void qc.invalidateQueries({ queryKey: keys.templateUpdate(id) });
    },
  });
}

/** Moves an API-key provider onto another template / variant; the key, models and edited settings stay. */
export function useSwitchTemplate(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { templateId: string; variantId?: string | null }) =>
      api<Provider>('POST', `/api/providers/${enc(id)}/template-switch`, body),
    onSuccess: (p) => {
      qc.setQueryData(keys.provider(id), p);
      invalidateProviders(qc, id);
      void qc.invalidateQueries({ queryKey: keys.providerModels(id) });
      void qc.invalidateQueries({ queryKey: keys.remoteModels(id) });
      void qc.invalidateQueries({ queryKey: keys.templateUpdate(id) });
    },
  });
}

export const useProviderModels = (id: string | undefined) =>
  useQuery({
    queryKey: keys.providerModels(id ?? ''),
    queryFn: () => api<ProviderModel[]>('GET', `/api/providers/${enc(id!)}/models`),
    enabled: Boolean(id),
  });

function invalidateProviderModels(qc: ReturnType<typeof useQueryClient>, id: string) {
  void qc.invalidateQueries({ queryKey: keys.providerModels(id) });
  void qc.invalidateQueries({ queryKey: keys.remoteModels(id) });
  void qc.invalidateQueries({ queryKey: ['model'] });
  invalidateProviders(qc, id);
}

export function useAddProviderModels(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (modelIds: string[]) => api<ProviderModel[]>('POST', `/api/providers/${enc(id)}/models`, { modelIds }),
    onSuccess: () => invalidateProviderModels(qc, id),
  });
}

export function useUpdateProviderModel(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { pmId: number; systemModelId?: string | null; overrides?: ModelOverrides; enabled?: boolean }) => {
      const { pmId, ...body } = v;
      return api<ProviderModel>('PATCH', `/api/providers/${enc(id)}/models/${pmId}`, body);
    },
    onSuccess: () => invalidateProviderModels(qc, id),
  });
}

export function useDeleteProviderModel(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (pmId: number) => api<void>('DELETE', `/api/providers/${enc(id)}/models/${pmId}`),
    onSuccess: () => invalidateProviderModels(qc, id),
  });
}

// ---------- privacy guard ----------

export const usePrivacy = () =>
  useQuery({ queryKey: keys.privacy, queryFn: () => api<PrivacyOverview>('GET', '/api/privacy') });

export function useUpdatePrivacy() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (settings: PrivacySettingsDto) => api<PrivacySettingsDto>('PUT', '/api/privacy', settings),
    onSuccess: (saved) =>
      qc.setQueryData<PrivacyOverview>(keys.privacy, (prev) => (prev ? { ...prev, settings: saved } : prev)),
  });
}

export const usePrivacyDryRun = () =>
  useMutation({
    mutationFn: (req: { text?: string; json?: string; clientKind?: string }) =>
      api<PrivacyDryRunResult>('POST', '/api/privacy/dry-run', req),
  });

export interface PrivacyEventQuery {
  from?: string;
  to?: string;
  client?: string;
  provider?: string;
  category?: string;
  action?: string;
  blocked?: string;
  model?: string;
  page: number;
  pageSize: number;
}

/** Interception log: every request that produced a privacy guard outcome. */
export const usePrivacyEvents = (q: PrivacyEventQuery, live: boolean) =>
  useQuery({
    queryKey: keys.privacyEvents(q),
    queryFn: () => api<Page<PrivacyEvent>>('GET', `/api/privacy/events${qs({ ...q })}`),
    placeholderData: (prev) => prev,
    refetchInterval: live ? 5000 : false,
  });

export const usePrivacyEventStats = (range: Range) =>
  useQuery({
    queryKey: keys.privacyEventStats(range),
    queryFn: () => api<PrivacyEventStats>('GET', `/api/privacy/events/stats${qs({ range })}`),
    refetchInterval: 15_000,
    placeholderData: (prev) => prev,
  });

// ---------- subscription accounts ----------

export const useProviderAccounts = (id: string | undefined, enabled = true) =>
  useQuery({
    queryKey: keys.providerAccounts(id ?? ''),
    queryFn: () => api<ProviderAccount[]>('GET', `/api/providers/${enc(id!)}/accounts`),
    enabled: Boolean(id) && enabled,
  });

export const useProviderUsage = (id: string | undefined, enabled = true) =>
  useQuery({
    queryKey: keys.providerUsage(id ?? ''),
    queryFn: ({ signal }) => api<ProviderUsage>('GET', `/api/providers/${enc(id!)}/usage`, undefined, signal),
    enabled: Boolean(id) && enabled,
    refetchInterval: 5_000,
  });

function invalidateProviderAccounts(qc: ReturnType<typeof useQueryClient>, providerId: string) {
  void qc.invalidateQueries({ queryKey: keys.providerAccounts(providerId) });
  void qc.invalidateQueries({ queryKey: keys.providerUsage(providerId) });
}

export function useRefreshProviderAccount(providerId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api<ProviderAccount>('POST', `/api/provider-accounts/${enc(id)}/refresh`),
    onSuccess: () => invalidateProviderAccounts(qc, providerId),
  });
}

export function useFetchProviderAccountQuota(providerId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api<ProviderAccount>('POST', `/api/provider-accounts/${enc(id)}/quota`),
    onSuccess: () => invalidateProviderAccounts(qc, providerId),
  });
}

/** Switching endpoints answer with the provider's whole (re-ordered, re-flagged) account list. */
function useAccountListMutation<V>(providerId: string, fn: (v: V) => Promise<ProviderAccount[]>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSuccess: (list) => {
      qc.setQueryData(keys.providerAccounts(providerId), list);
      void qc.invalidateQueries({ queryKey: keys.providerUsage(providerId) });
    },
  });
}

/** "Switch to this account": it becomes the provider's current account for the next request. */
export const useActivateProviderAccount = (providerId: string) =>
  useAccountListMutation(providerId, (id: string) =>
    api<ProviderAccount[]>('POST', `/api/provider-accounts/${enc(id)}/activate`));

export const useUpdateProviderAccount = (providerId: string) =>
  useAccountListMutation(providerId, ({ id, ...patch }: { id: string; enabled?: boolean; displayName?: string; httpProxy?: string }) =>
    api<ProviderAccount[]>('PATCH', `/api/provider-accounts/${enc(id)}`, patch));

/** New failover order (first = tried first). */
export const useReorderProviderAccounts = (providerId: string) =>
  useAccountListMutation(providerId, (ids: string[]) =>
    api<ProviderAccount[]>('PUT', `/api/providers/${enc(providerId)}/accounts/order`, { ids }));

export const useSubscriptionPolicy = (providerId: string, enabled = true) =>
  useQuery({
    queryKey: keys.subscriptionPolicy(providerId),
    queryFn: () => api<SubscriptionPolicy>('GET', `/api/providers/${enc(providerId)}/subscription-policy`),
    enabled,
  });

export function useUpdateSubscriptionPolicy(providerId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (patch: Partial<Pick<SubscriptionPolicy, 'clientPolicy' | 'switchMode' | 'mimicClaudeCode'>>) =>
      api<SubscriptionPolicy>('PUT', `/api/providers/${enc(providerId)}/subscription-policy`, patch),
    onSuccess: (policy) => {
      qc.setQueryData(keys.subscriptionPolicy(providerId), policy);
      // The policy lives in the provider's settings JSON: keep the provider form in sync.
      void qc.invalidateQueries({ queryKey: keys.provider(providerId) });
    },
  });
}

export function useDeleteProviderAccount(providerId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api<void>('DELETE', `/api/provider-accounts/${enc(id)}`),
    onSuccess: () => invalidateProviderAccounts(qc, providerId),
  });
}

/** Starts a login; `accountId` re-logs that account in, `httpProxy` becomes the new account's own proxy. */
export const useStartProviderLogin = (providerId: string) =>
  useMutation({
    mutationFn: (vars: { accountId?: string; httpProxy?: string } = {}) =>
      api<SubscriptionLoginStart>('POST', `/api/providers/${enc(providerId)}/accounts/login`, {
        accountId: vars.accountId || undefined,
        httpProxy: vars.httpProxy?.trim() || undefined,
      }),
  });

export const usePollProviderLogin = (providerId: string) =>
  useMutation({
    mutationFn: (state: string) =>
      api<SubscriptionPollResult>('POST', `/api/providers/${enc(providerId)}/accounts/login/${enc(state)}/poll`),
  });

/** Whether this machine already has a usable `codex login` (~/.codex/auth.json). */
export const useLocalCodexLogin = (enabled = true) =>  useQuery({
    queryKey: keys.localCodexLogin,
    queryFn: () => api<LocalCodexLogin>('GET', '/api/subscription/codex/local-login'),
    enabled,
    retry: false,
  });

/** Adopts the machine's codex login as a subscription account (no browser round-trip). */
export function useImportCodexAccount(providerId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => api<ImportedCodexAccount>('POST', `/api/providers/${enc(providerId)}/accounts/import-codex`, {}),
    onSuccess: () => invalidateProviderAccounts(qc, providerId),
  });
}

/**
 * Finishes a manual-paste login (Claude): the authorization page shows the code because the client's
 * registered redirect_uri is not a loopback address, so the user pastes it back here.
 */
export const useCompleteProviderLogin = (providerId: string) =>
  useMutation({
    mutationFn: (vars: { state: string; code: string }) =>
      api<SubscriptionPollResult>(
        'POST',
        `/api/providers/${enc(providerId)}/accounts/login/${enc(vars.state)}/complete`,
        { code: vars.code },
      ),
  });

/**
 * Whether this machine already holds a GitHub authorization that could back a Copilot account
 * (the VS Code GitHub session, GH_TOKEN, or the Copilot plugin config).
 */
export const useLocalCopilotLogin = (enabled = true) =>
  useQuery({
    queryKey: keys.localCopilotLogin,
    queryFn: () => api<LocalCopilotLogin>('GET', '/api/subscription/copilot/local-login'),
    enabled,
    retry: false,
  });

/**
 * Adopts a GitHub authorization from this machine (or a pasted token) as a Copilot account;
 * the pass-through model list is refreshed in the same call.
 */
export function useImportCopilotAccount(providerId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (token?: string) =>
      api<ImportedCodexAccount>(
        'POST',
        `/api/providers/${enc(providerId)}/accounts/import-copilot`,
        { token: token && token.trim().length > 0 ? token.trim() : null },
      ),
    onSuccess: (_r, _token) => {
      invalidateProviderAccounts(qc, providerId);
      void qc.invalidateQueries({ queryKey: keys.localCopilotLogin });
    },
  });
}

export const useAccountImportInfo = (providerId: string, enabled = true) =>
  useQuery({
    queryKey: keys.accountImportInfo(providerId),
    queryFn: () => api<AccountImportInfo>('GET', `/api/providers/${enc(providerId)}/accounts/import-info`),
    enabled,
    staleTime: Infinity,
  });

/**
 * Unified import (refresh tokens, setup-tokens, credential JSON, API keys, GitHub tokens): every entry comes back
 * masked with its outcome. A dry run only classifies and de-duplicates.
 */
export function useImportAccounts(providerId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (req: AccountImportRequest) =>
      api<BatchImportResult>('POST', `/api/providers/${enc(providerId)}/accounts/import`, req),
    onSuccess: (_r, req) => {
      if (!req.dryRun) invalidateProviderAccounts(qc, providerId);
    },
  });
}

/** codex reset cards (live list — statuses change when a card is used or expires). */
export const useResetCredits = (accountId: string | null, enabled = true) =>
  useQuery({
    queryKey: keys.resetCredits(accountId ?? ''),
    queryFn: () => api<ResetCreditList>('GET', `/api/provider-accounts/${enc(accountId!)}/reset-credits`),
    enabled: Boolean(accountId) && enabled,
    retry: false,
  });

/** Consumes one reset card; the response carries the refreshed quota snapshot. */
export function useConsumeResetCredit(providerId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { accountId: string; creditId: string }) =>
      api<ResetCreditResult>('POST', `/api/provider-accounts/${enc(vars.accountId)}/reset-credits/${enc(vars.creditId)}/consume`, {}),
    onSuccess: (_r, vars) => {
      invalidateProviderAccounts(qc, providerId);
      void qc.invalidateQueries({ queryKey: keys.resetCredits(vars.accountId) });
    },
  });
}

// ---------- clients ----------

export const useClients = () =>
  useQuery({
    queryKey: keys.clients,
    queryFn: () => api<ClientInfo[]>('GET', '/api/clients'),
    // While an install / update runs, keep the list (busy marks, versions) current.
    refetchInterval: (q) => (q.state.data?.some((c) => c.install?.busy) ? 2000 : false),
  });

/** Rewrites every enabled client whose Astra settings are stale (e.g. after the port changed). */
export const useReapplyClients = () =>
  useClientMutation(() => api<{ updated: ClientKind[] }>('POST', '/api/clients/reapply', {}));

export const useClientModels = (kind: ClientKind | undefined, enabled = true) =>
  useQuery({
    queryKey: keys.clientModels(kind ?? ''),
    queryFn: () => api<string[]>('GET', `/api/clients/${kind}/models`),
    enabled: Boolean(kind) && enabled,
    retry: false,
  });

export const useBackups = (kind: ClientKind | undefined, enabled = true) =>
  useQuery({
    queryKey: keys.backups(kind ?? ''),
    queryFn: () => api<BackupInfo[]>('GET', `/api/clients/${kind}/backups`),
    enabled: Boolean(kind) && enabled,
  });

function useClientMutation<TVars, TResult>(fn: (vars: TVars) => Promise<TResult>, extraKeys: (vars: TVars) => QueryKey[] = () => []) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSuccess: (_r, vars) => {
      void qc.invalidateQueries({ queryKey: keys.clients });
      void qc.invalidateQueries({ queryKey: keys.providers });
      void qc.invalidateQueries({ queryKey: ['provider'] });
      void qc.invalidateQueries({ queryKey: ['client-models'] });
      for (const k of extraKeys(vars)) void qc.invalidateQueries({ queryKey: k });
    },
  });
}

export const useSetBinding = () =>
  useClientMutation(
    (v: { kind: ClientKind; providerId: string; accountId?: string }) =>
      // accountId: omitted keeps the current pin, "" clears it, an id pins that subscription account.
      api<ClientInfo>('PUT', `/api/clients/${v.kind}/binding`, { providerId: v.providerId, accountId: v.accountId }),
    (v) => [keys.clientModels(v.kind)],
  );

/** Replaces the client's provider list; the order is the routing order (first = primary). */
export const useSetBindings = () =>
  useClientMutation(
    (v: { kind: ClientKind; bindings: ClientBinding[] }) =>
      api<ClientInfo>('PUT', `/api/clients/${v.kind}/bindings`, { bindings: v.bindings }),
    (v) => [keys.clientModels(v.kind)],
  );

export const usePreviewEnable = () =>
  useMutation({
    mutationFn: (v: { kind: ClientKind; req: EnableRequest }) =>
      api<ConfigPreview>('POST', `/api/clients/${v.kind}/preview-enable`, v.req),
  });

export const useEnableClient = () =>
  useClientMutation(
    (v: { kind: ClientKind; req: EnableRequest }) => api<ClientInfo>('POST', `/api/clients/${v.kind}/enable`, v.req),
    (v) => [keys.backups(v.kind)],
  );

export const useDisableClient = () =>
  useClientMutation((kind: ClientKind) => api<DisableResult>('POST', `/api/clients/${kind}/disable`));

export const useForceRestore = () =>
  useClientMutation((kind: ClientKind) => api<DisableResult>('POST', `/api/clients/${kind}/force-restore`));

/** Fixes the client's config files it cannot read as they are (ClientInfo.repairs); backs them up first. */
export const useRepairClientConfig = () =>
  useClientMutation(
    (kind: ClientKind) => api<ClientInfo>('POST', `/api/clients/${kind}/repair`),
    (kind) => [keys.backups(kind)],
  );

export const useRestoreBackup = () =>
  useClientMutation(
    (v: { kind: ClientKind; id: string }) =>
      api<ClientInfo>('POST', `/api/clients/${v.kind}/backups/${enc(v.id)}/restore`),
    (v) => [keys.backups(v.kind)],
  );

/** Fetches the clients' latest versions (npm registry; the server keeps them 6 h unless forced) and returns the refreshed list. */
export function useCheckClientUpdates() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (force: boolean) => api<ClientInfo[]>('POST', '/api/clients/check-updates', { force }),
    onSuccess: (list) => qc.setQueryData(keys.clients, list),
  });
}

/** The current or last install / update of a client; polls while it runs. */
export const useClientInstallJob = (kind: ClientKind | undefined, enabled = true) =>
  useQuery({
    queryKey: keys.clientInstallJob(kind ?? ''),
    queryFn: () => api<ClientInstallJob>('GET', `/api/clients/${kind}/install`),
    enabled: Boolean(kind) && enabled,
    retry: false,
    refetchInterval: (q) => (q.state.data?.state === 'running' ? 800 : false),
  });

export function useStartClientInstall() {
  const qc = useQueryClient();
  return useMutation({
    /** elevated: run the npm command with admin rights (the system shows its own password prompt). */
    mutationFn: (v: { kind: ClientKind; action: ClientInstallJob['action']; elevated?: boolean }) =>
      api<ClientInstallJob>('POST', `/api/clients/${v.kind}/install`, { action: v.action, elevated: v.elevated ?? false }),
    onSuccess: (job) => {
      qc.setQueryData(keys.clientInstallJob(job.kind), job);
      void qc.invalidateQueries({ queryKey: keys.clients });
    },
  });
}

export function useCancelClientInstall() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (kind: ClientKind) => api<ClientInstallJob>('POST', `/api/clients/${kind}/install/cancel`, {}),
    onSuccess: (job) => qc.setQueryData(keys.clientInstallJob(job.kind), job),
  });
}

// ---------- Claude Code direct connection ----------

const CLAUDE_DIRECT = '/api/clients/claude-code/direct';

/**
 * The launcher's direct-connection setting and the Claude subscription accounts it can sign in with
 * (loopback-only). Only pass `enabled` for Claude Code: another client never queries this endpoint.
 */
export const useClaudeDirect = (enabled: boolean) =>
  useQuery({
    queryKey: keys.claudeDirect,
    queryFn: () => api<ClaudeDirectState>('GET', CLAUDE_DIRECT),
    enabled,
    retry: false,
  });

/**
 * Switches the launcher's mode / selected account, or records the statistics consent of that account.
 * The server answers 409 (its own message) when direct is refused: no active, enabled account, or the
 * client's gateway take-over is still on.
 */
export function useSelectClaudeDirect() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: ClaudeDirectSelect) => api<ClaudeDirectState>('PUT', CLAUDE_DIRECT, body),
    onSuccess: (state) => {
      qc.setQueryData(keys.claudeDirect, state);
      // Consent off stops ingestion immediately: drop any cached statistics as well.
      void qc.invalidateQueries({ queryKey: ['claude-direct-requests'] });
    },
  });
}

/** The account's most recent reported requests (at most 100, newest first); polls only when `live`. */
export const useClaudeDirectRequests = (accountId: string | null | undefined, live: boolean) =>
  useQuery({
    queryKey: keys.claudeDirectRequests(accountId ?? ''),
    queryFn: () => api<ClaudeDirectRequest[]>('GET', `${CLAUDE_DIRECT}/accounts/${enc(accountId!)}/requests`),
    enabled: Boolean(accountId),
    retry: false,
    refetchInterval: live ? 5000 : false,
  });

// ---------- tokens ----------

export const useTokens = () =>
  useQuery({
    queryKey: keys.tokens,
    queryFn: () => api<Token[]>('GET', '/api/tokens'),
    refetchInterval: 15_000,
  });

/** One token, with its pool members' live health (refreshed more often than the list). */
export const useToken = (id: string | undefined) =>
  useQuery({
    queryKey: [...keys.tokens, id] as const,
    queryFn: () => api<Token>('GET', `/api/tokens/${enc(id!)}`),
    enabled: !!id,
    refetchInterval: 10_000,
  });

function useTokenMutation<TVars, TResult>(fn: (vars: TVars) => Promise<TResult>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: keys.tokens });
      void qc.invalidateQueries({ queryKey: keys.clients });
    },
  });
}

export const useCreateToken = () =>
  useTokenMutation((body: { name: string; providerId?: string | null; accountId?: string | null; pool?: TokenPoolInput }) =>
    api<Token>('POST', '/api/tokens', body),
  );

/**
 * providerId: null clears direct calls; accountId: null / "" uses the provider's default account. pool replaces both
 * (never send them together); a providerId patch drops the pool.
 */
export const useUpdateToken = () =>
  useTokenMutation((v: {
    id: string; name?: string; enabled?: boolean; providerId?: string | null; accountId?: string | null;
    pool?: TokenPoolInput; limits?: TokenLimits | null; allowedClients?: ClientKind[] | null;
  }) => {
    const { id, ...body } = v;
    return api<Token>('PATCH', `/api/tokens/${enc(id)}`, body);
  });

export const useResetToken = () =>
  useTokenMutation((id: string) => api<TokenMutationResult>('POST', `/api/tokens/${enc(id)}/reset`));

export const useDeleteToken = () =>
  useTokenMutation((id: string) => api<TokenMutationResult>('DELETE', `/api/tokens/${enc(id)}`));

/** The plaintext token, fetched only when the user copies it. */
export const useRevealToken = () =>
  useMutation({ mutationFn: (id: string) => api<{ token: string }>('POST', `/api/tokens/${enc(id)}/reveal`) });

// ---------- requests & stats ----------

export interface RequestQuery {
  from?: string;
  to?: string;
  client?: string;
  token?: string;
  provider?: string;
  model?: string;
  status?: string;
  /** "remote" | "local"; empty = both. */
  origin?: string;
  page: number;
  pageSize: number;
}

export const useRequests = (q: RequestQuery, live: boolean) =>
  useQuery({
    queryKey: keys.requests(q),
    queryFn: () => api<RequestPage>('GET', `/api/requests${qs({ ...q })}`),
    placeholderData: (prev) => prev,
    refetchInterval: live ? 5000 : false,
  });

export const useRequest = (id: string | null) =>
  useQuery({
    queryKey: keys.request(id ?? ''),
    queryFn: () => api<RequestDetail>('GET', `/api/requests/${enc(id!)}`),
    enabled: Boolean(id),
    // An in-flight request is served from the live feed; follow it until it finishes.
    refetchInterval: (query) => (query.state.data?.status === 'pending' ? 1000 : false),
    // Not kept once the sheet closes: a detail carries captured bodies, and every opened request would otherwise
    // stay in memory for the default five minutes.
    gcTime: 0,
  });

/** One whole captured body part, fetched only when asked for (the detail carries the start of each). */
export const useRequestBody = (id: string, part: string, enabled: boolean) =>
  useQuery({
    queryKey: [...keys.request(id), 'body', part] as const,
    queryFn: ({ signal }) => apiText(`/api/requests/${enc(id)}/bodies/${enc(part)}`, signal),
    enabled,
    staleTime: Infinity,
    gcTime: 0,
  });

/** The list's filters, minus paging — exactly what the live rate endpoint accepts. */
export type RequestRateFilter = Pick<RequestQuery, 'client' | 'token' | 'provider' | 'model' | 'status' | 'origin'>;

/** Live RPM / TPM / cache-hit ratio for the request-log header: a trailing window (default 60 s) under the list's filters. */
export const useRequestRate = (filters: RequestRateFilter) =>
  useQuery({
    queryKey: keys.stats('rate', filters),
    queryFn: () => api<RateStats>('GET', `/api/stats/rate${qs({ ...filters })}`),
    placeholderData: (prev) => prev,
    refetchInterval: 5000,
  });

/** The viewer's UTC offset in minutes, so timeseries buckets follow local days and hours. */
const tzOffset = () => -new Date().getTimezoneOffset();

// `client` (a client kind) / `token` (a token id) narrow stats to those requests; omitted means every request.
export const useStatsSummary = (range: Range, client?: string, token?: string) =>
  useQuery({
    queryKey: keys.stats('summary', range, client ?? '', token ?? ''),
    queryFn: () => api<StatsSummary>('GET', `/api/stats/summary${qs({ range, client, token })}`),
    refetchInterval: 15_000,
    placeholderData: (prev) => prev,
  });

export const useTimeseries = (
  range: Range,
  groupBy: 'day' | 'hour',
  by: 'model' | 'provider' | 'client' | 'token',
  client?: string,
  token?: string,
) =>
  useQuery({
    queryKey: keys.stats('timeseries', range, groupBy, by, client ?? '', token ?? '', tzOffset()),
    queryFn: () =>
      api<TimeseriesPoint[]>('GET', `/api/stats/timeseries${qs({ range, groupBy, by, client, token, tzOffset: tzOffset() })}`),
    refetchInterval: 30_000,
    placeholderData: (prev) => prev,
  });

export const useTopModels = (range: Range, client?: string, limit = 8, token?: string) =>
  useQuery({
    queryKey: keys.stats('top', range, client ?? '', limit, token ?? ''),
    queryFn: () => api<TopModel[]>('GET', `/api/stats/top-models${qs({ range, limit, client, token })}`),
    refetchInterval: 30_000,
    placeholderData: (prev) => prev,
  });

/** Per provider: answered requests, success, cost, TTFT and provider-pool failovers (a token's pool members). */
export const useProviderStats = (range: Range, token?: string, client?: string) =>
  useQuery({
    queryKey: keys.stats('providers', range, client ?? '', token ?? ''),
    queryFn: () => api<ProviderStat[]>('GET', `/api/stats/providers${qs({ range, client, token })}`),
    refetchInterval: 30_000,
    placeholderData: (prev) => prev,
  });

/** Daily request counts for the activity heatmap; the window is its own (default 365 days), not the page's range. */
export const useActivityHeatmap = (days = 365, client?: string, token?: string) =>
  useQuery({
    queryKey: keys.stats('activity', days, client ?? '', token ?? '', tzOffset()),
    queryFn: () => api<DailyActivity[]>('GET', `/api/stats/activity-heatmap${qs({ days, client, token, tzOffset: tzOffset() })}`),
    refetchInterval: 60_000,
    placeholderData: (prev) => prev,
  });

// ---------- plugins ----------

export const usePlugins = () =>
  useQuery({ queryKey: keys.plugins, queryFn: () => api<PluginList>('GET', '/api/plugins'), refetchInterval: 10_000 });

export const usePluginHost = (enabled = true) =>
  useQuery({ queryKey: keys.pluginHost, queryFn: () => api<PluginHostStatus>('GET', '/api/plugins/host'), enabled, refetchInterval: 15_000 });

/** A plugin job, followed while it runs. */
export const usePluginJob = (id: string | null) =>
  useQuery({
    queryKey: keys.pluginJob(id ?? ''),
    queryFn: () => api<PluginJob>('GET', `/api/plugins/jobs/${enc(id!)}`),
    enabled: Boolean(id),
    refetchInterval: (q) => (q.state.data?.state === 'running' || !q.state.data ? 1000 : false),
  });

function usePluginMutation<TVars, TResult>(fn: (vars: TVars) => Promise<TResult>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: keys.plugins });
      void qc.invalidateQueries({ queryKey: keys.pluginHost });
      void qc.invalidateQueries({ queryKey: keys.clients });
      void qc.invalidateQueries({ queryKey: keys.providers });
    },
  });
}

/** Installs a plugin: an npm package, a git repository (github:owner/repo) or a path. Returns the job. */
export const useAddPlugin = () =>
  usePluginMutation((vars: { spec: string; source?: string }) => api<PluginJob>('POST', '/api/plugins', vars));

export const useUpdatePlugin = () =>
  usePluginMutation((spec?: string) => api<PluginJob>('POST', '/api/plugins/update', { spec }));

export const useRemovePlugin = () =>
  usePluginMutation((spec: string) => api<PluginJob>('DELETE', `/api/plugins${qs({ spec })}`));

/** enabled switches it on or off; options (an object, or null to clear) sets its options. */
export const usePatchPlugin = () =>
  usePluginMutation((vars: { spec: string; enabled?: boolean; options?: Record<string, unknown> | null }) =>
    api<PluginList>('PATCH', `/api/plugins${qs({ spec: vars.spec })}`, { enabled: vars.enabled, options: vars.options }));

export const useReorderPlugins = () => usePluginMutation((specs: string[]) => api<PluginList>('PUT', '/api/plugins/order', { specs }));

export const usePreferPlugin = () =>
  usePluginMutation((vars: { spec: string; provider: string }) => api<PluginList>('POST', '/api/plugins/prefer', vars));

export const useInstallBun = () => usePluginMutation(() => api<PluginJob>('POST', '/api/plugins/runtime/install'));

export const usePluginMarket = (enabled = true) =>
  useQuery({ queryKey: keys.pluginMarket, queryFn: () => api<MarketListing[]>('GET', '/api/plugins/market'), enabled, staleTime: 10 * 60_000 });

export const usePluginSearch = (q: string) =>
  useQuery({
    queryKey: keys.pluginSearch(q),
    queryFn: () => api<MarketSearchResult[]>('GET', `/api/plugins/search${qs({ q })}`),
    enabled: q.trim().length >= 2,
    staleTime: 10 * 60_000,
  });

// signing in to a plugin's provider ({provider} is the provider row id, "plugin-<id>")

export const usePluginMethods = (provider: string, enabled = true) =>
  useQuery({
    queryKey: keys.pluginMethods(provider),
    queryFn: () => api<PluginAuthMethod[]>('GET', `/api/plugin-providers/${enc(provider)}/methods`),
    enabled,
  });

export const pluginPrompt = (provider: string, method: number, inputs: Record<string, string>) =>
  api<{ prompt: PluginPrompt | null }>('POST', `/api/plugin-providers/${enc(provider)}/prompt`, { method, inputs });

export const pluginValidate = (provider: string, method: number, key: string, value: string) =>
  api<{ error: string | null }>('POST', `/api/plugin-providers/${enc(provider)}/validate`, { method, key, value });

function invalidatePluginAccounts(qc: ReturnType<typeof useQueryClient>, provider: string) {
  void qc.invalidateQueries({ queryKey: keys.providerAccounts(provider) });
  void qc.invalidateQueries({ queryKey: keys.providerUsage(provider) });
  void qc.invalidateQueries({ queryKey: keys.providers });
}

export const useStartPluginSignIn = (provider: string) =>
  useMutation({
    mutationFn: (vars: { method: number; inputs: Record<string, string>; account?: string }) =>
      api<PluginSignIn>('POST', `/api/plugin-providers/${enc(provider)}/sign-in`, vars),
  });

/** A browser sign-in, followed until it is done or failed. */
export const usePluginSignIn = (provider: string, flow: string | null) => {
  const qc = useQueryClient();
  return useQuery({
    queryKey: keys.pluginSignIn(provider, flow ?? ''),
    queryFn: async () => {
      const s = await api<PluginSignIn>('GET', `/api/plugin-providers/${enc(provider)}/sign-in/${enc(flow!)}`);
      if (s.state === 'done') invalidatePluginAccounts(qc, provider);
      return s;
    },
    enabled: Boolean(flow),
    refetchInterval: (q) => (q.state.data?.state === 'waiting' || !q.state.data ? 1500 : false),
  });
};

export const useSubmitPluginCode = (provider: string) => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { flow: string; code: string }) =>
      api<PluginSignIn>('POST', `/api/plugin-providers/${enc(provider)}/sign-in/${enc(vars.flow)}/code`, { code: vars.code }),
    onSuccess: (s) => {
      qc.setQueryData(keys.pluginSignIn(provider, s.id), s);
      if (s.state === 'done') invalidatePluginAccounts(qc, provider);
    },
  });
};

export const usePluginApiKey = (provider: string) => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { method: number; inputs: Record<string, string>; key: string }) =>
      api<{ account: string | null }>('POST', `/api/plugin-providers/${enc(provider)}/api-key`, vars),
    onSuccess: () => invalidatePluginAccounts(qc, provider),
  });
};

export const usePluginSignOut = (provider: string) => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (account: string) => api<unknown>('POST', `/api/plugin-providers/${enc(provider)}/accounts/${enc(account)}/sign-out`),
    onSuccess: () => invalidatePluginAccounts(qc, provider),
  });
};

export const usePluginUsage = (provider: string, account: string, enabled: boolean) =>
  useQuery({
    queryKey: keys.pluginUsage(provider, account),
    queryFn: () => api<PluginUsage>('GET', `/api/plugin-providers/${enc(provider)}/accounts/${enc(account)}/usage`),
    enabled,
    staleTime: 60_000,
    retry: false,
  });

export const usePluginCheckin = (provider: string) =>
  useQuery({ queryKey: keys.pluginCheckin(provider), queryFn: () => api<PluginCheckinState>('GET', `/api/plugin-providers/${enc(provider)}/checkin`) });

/** on: switch the daily check-in on or off; omitted: check in every account now. */
export const usePluginCheckinAction = (provider: string) => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (on?: boolean) =>
      on === undefined
        ? api<PluginCheckinState>('POST', `/api/plugin-providers/${enc(provider)}/checkin`)
        : api<PluginCheckinState>('PUT', `/api/plugin-providers/${enc(provider)}/checkin`, { on }),
    onSuccess: (state) => qc.setQueryData(keys.pluginCheckin(provider), state),
  });
};

/** What importing from a magpie install on this machine would bring. */
export const useMagpieImportPreview = (enabled = true) =>
  useQuery({ queryKey: keys.magpieImport, queryFn: () => api<MagpieImportPreview>('GET', '/api/plugins/import-magpie'), enabled, staleTime: 60_000 });

export const useImportMagpie = () =>
  usePluginMutation((signIns: boolean) => api<PluginJob>('POST', '/api/plugins/import-magpie', { signIns }));

// ---------- alerts & webhooks ----------

export const useWebhooks = () => useQuery({ queryKey: keys.webhooks, queryFn: () => api<Webhook[]>('GET', '/api/webhooks') });

export type WebhookBody = {
  name?: string; url?: string; format?: WebhookFormat; enabled?: boolean; timeoutSec?: number; useProxy?: boolean;
  /** Feishu / DingTalk: the bot's signing secret ("" clears it). Not accepted for other formats. */
  secret?: string | null;
  /** Replaces the set; an empty value keeps that header's stored value; null clears all. */
  headers?: Record<string, string> | null;
  /** false drops the secret (create: no secret). */
  signed?: boolean;
};

function useAlertMutation<TVars, TResult>(fn: (vars: TVars) => Promise<TResult>) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: keys.webhooks });
      void qc.invalidateQueries({ queryKey: keys.alertRules });
      void qc.invalidateQueries({ queryKey: keys.alertStates });
      void qc.invalidateQueries({ queryKey: ['webhook-deliveries'] });
    },
  });
}

export const useCreateWebhook = () => useAlertMutation((body: WebhookBody) => api<WebhookSecret>('POST', '/api/webhooks', body));
export const useUpdateWebhook = () =>
  useAlertMutation(({ id, ...body }: WebhookBody & { id: string }) => api<Webhook>('PATCH', `/api/webhooks/${enc(id)}`, body));
export const useDeleteWebhook = () => useAlertMutation((id: string) => api<void>('DELETE', `/api/webhooks/${enc(id)}`));
export const useResetWebhookSecret = () =>
  useAlertMutation((id: string) => api<WebhookSecret>('POST', `/api/webhooks/${enc(id)}/secret`));
export const useTestWebhook = () => useAlertMutation((id: string) => api<WebhookDelivery>('POST', `/api/webhooks/${enc(id)}/test`));
export const useWebhookDeliveries = (id: string | undefined) =>
  useQuery({
    queryKey: keys.webhookDeliveries(id ?? ''),
    queryFn: () => api<WebhookDelivery[]>('GET', `/api/webhooks/${enc(id!)}/deliveries`),
    enabled: Boolean(id),
  });
export const useRedeliver = () =>
  useAlertMutation((id: string) => api<WebhookDelivery>('POST', `/api/webhook-deliveries/${enc(id)}/redeliver`));

export const useAlertEvents = () =>
  useQuery({ queryKey: keys.alertEvents, queryFn: () => api<AlertEvent[]>('GET', '/api/alerts/events'), staleTime: Infinity });
export const useAlertRules = () => useQuery({ queryKey: keys.alertRules, queryFn: () => api<AlertRule[]>('GET', '/api/alerts/rules') });
export const useAlertStates = (firingOnly = false) =>
  useQuery({
    queryKey: [...keys.alertStates, firingOnly],
    queryFn: () => api<AlertState[]>('GET', `/api/alerts/states${firingOnly ? '?firing=true' : ''}`),
    refetchInterval: 60_000,
  });

export type AlertRuleBody = Partial<Pick<AlertRule, 'name' | 'enabled' | 'event' | 'subjects' | 'condition' | 'notifyResolved' | 'repeatMinutes' | 'cooldownMinutes' | 'webhookIds'>>;
export const useCreateAlertRule = () => useAlertMutation((body: AlertRuleBody) => api<AlertRule>('POST', '/api/alerts/rules', body));
export const useUpdateAlertRule = () =>
  useAlertMutation(({ id, ...body }: AlertRuleBody & { id: string }) => api<AlertRule>('PATCH', `/api/alerts/rules/${enc(id)}`, body));
export const useDeleteAlertRule = () => useAlertMutation((id: string) => api<void>('DELETE', `/api/alerts/rules/${enc(id)}`));

/** The token self-service view (no admin session: the key itself authenticates). Plain fetch, not the admin client. */
export async function fetchKeyInfo(key: string): Promise<KeyInfo> {
  const res = await fetch(`${apiBase()}/v1/astra/key`, { headers: { Authorization: `Bearer ${key}` } });
  if (!res.ok) throw new KeyInfoError(res.status);
  return (await res.json()) as KeyInfo;
}

/** The holder's own request log, newest first; `before` = the previous page's `nextBefore`. */
export async function fetchKeyRequests(key: string, q: { before?: string | null; status?: string; model?: string; limit?: number } = {}): Promise<KeyRequestPage> {
  const res = await fetch(`${apiBase()}/v1/astra/key/requests${qs({ ...q, before: q.before ?? undefined })}`, { headers: { Authorization: `Bearer ${key}` } });
  if (!res.ok) throw new KeyInfoError(res.status);
  return (await res.json()) as KeyRequestPage;
}

/** The holder's statistics for a range (usage time zone). */
export async function fetchKeyStats(key: string, range: KeyStatsRange): Promise<KeyStats> {
  const res = await fetch(`${apiBase()}/v1/astra/key/stats${qs({ range })}`, { headers: { Authorization: `Bearer ${key}` } });
  if (!res.ok) throw new KeyInfoError(res.status);
  return (await res.json()) as KeyStats;
}

export class KeyInfoError extends Error {
  constructor(public readonly status: number) {
    super(`HTTP ${status}`);
  }
}

// ---------------------------------------------------------------- traces (.plan/observability.md §4)

/** Topology aggregates; the live window refreshes every 5 s. */
export const useTraceTopology = (range: TraceRange) =>
  useQuery({
    queryKey: keys.traceTopology(range),
    queryFn: () => api<TraceTopology>('GET', `/api/traces/topology${qs({ range })}`),
    placeholderData: (prev) => prev,
    refetchInterval: range === 'live' ? 5000 : 30_000,
  });

/** The latest requests with why each was sent, where it went and what came back (the topology's activity feed). */
export const useTraceActivity = (limit = 30) =>
  useQuery({
    queryKey: keys.traceActivity(limit),
    queryFn: () => api<TraceActivity[]>('GET', `/api/traces/activity${qs({ limit })}`),
    placeholderData: (prev) => prev,
    // the live feed's "persisted" event refetches it at once; this only catches up when the feed is down
    refetchInterval: 15_000,
  });

export const useTraceSessions = (range: string, client?: string) =>
  useQuery({
    queryKey: keys.traceSessions(range, client),
    queryFn: () => api<TraceSession[]>('GET', `/api/traces/sessions${qs({ range, client })}`),
    placeholderData: (prev) => prev,
    refetchInterval: 10_000,
  });

/** One session with its turns; the calls and spans of <paramref>turn</paramref> (the latest when omitted). */
export const useTraceSession = (key: string | undefined, turn?: string) =>
  useQuery({
    queryKey: keys.traceSession(key ?? '', turn),
    queryFn: () => api<TraceSessionDetail>('GET', `/api/traces/sessions/${enc(key!)}${qs({ turn })}`),
    enabled: Boolean(key),
    placeholderData: (prev) => prev,
    refetchInterval: (query) => (query.state.data?.session.active ? 3000 : false),
  });

/** One request's span tree; polled while the request is still in flight. */
export const useTraceRequest = (id: string | undefined) =>
  useQuery({
    queryKey: keys.traceRequest(id ?? ''),
    queryFn: () => api<TraceRequest>('GET', `/api/traces/requests/${enc(id!)}`),
    enabled: Boolean(id),
    refetchInterval: (query) => (query.state.data?.inFlight ? 1000 : false),
  });

// ---------- smart routing ----------

/** The settings, with the live status (a judge being left alone, sessions holding a grade): polled slowly. */
export const useSmartRouting = () =>
  useQuery({
    queryKey: keys.smartRouting,
    queryFn: ({ signal }) => api<SmartRouting>('GET', '/api/smart-routing', undefined, signal),
    refetchInterval: 30_000,
  });

export function useUpdateSmartRouting() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (update: SmartRoutingUpdate) => api<SmartRouting>('PUT', '/api/smart-routing', update),
    onSuccess: (saved) => {
      qc.setQueryData(keys.smartRouting, saved);
      // The virtual model joins (or leaves) the clients' model pickers and the lists of grade models.
      void qc.invalidateQueries({ queryKey: ['client-models'] });
      void qc.invalidateQueries({ queryKey: keys.routingModels });
    },
  });
}

/** Asks the judge about a sample text with the settings as they are being edited; saves nothing. */
export const useTestSmartRouting = () =>
  useMutation({ mutationFn: (req: SmartRoutingTestRequest) => api<SmartRoutingTestResult>('POST', '/api/smart-routing/test', req) });

/** Every model id a provider serves: what a grade can be given. */
export const useRoutingModels = () =>
  useQuery({ queryKey: keys.routingModels, queryFn: ({ signal }) => api<RoutingModel[]>('GET', '/api/smart-routing/models', undefined, signal) });
