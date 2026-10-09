import { useMutation, useQuery, useQueryClient, type QueryKey } from '@tanstack/react-query';

import { api, apiStream, enc, qs, type SseMessage } from './client';
import type {
  AuthStatus,
  BackupInfo,
  BillingResult,
  BillingSimulateRequest,
  ClientBinding,
  ClientInfo,
  ClientInstallJob,
  ClientKind,
  ConfigPreview,
  DisableResult,
  DailyActivity,
  EnableRequest,
  ImportedCodexAccount,
  ImportResult,
  ImportSelection,
  ImportSource,
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
  Range,
  RateStats,
  RemoteModel,
  RequestDetail,
  RequestSummary,
  Settings,
  SettingsPatch,
  StatsSummary,
  SubscriptionLoginStart,
  SubscriptionPolicy,
  SubscriptionPollResult,
  SyncPreview,
  TimeseriesPoint,
  Token,
  TokenMutationResult,
  TopModel,
  UpdateStatus,
  VersionInfo,
  ModelOverrides,
} from './types';
import type { PrivacyEvent } from './types';

export const keys = {
  health: ['health'] as const,
  version: ['version'] as const,
  auth: ['auth'] as const,
  settings: ['settings'] as const,
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
  tokens: ['tokens'] as const,
  clientModels: (kind: string) => ['client-models', kind] as const,
  backups: (kind: string) => ['backups', kind] as const,
  clientInstallJob: (kind: string) => ['client-install-job', kind] as const,
  privacy: ['privacy'] as const,
  privacyEvents: (q: PrivacyEventQuery) => ['privacy-events', q] as const,
  privacyEventStats: (...args: unknown[]) => ['privacy-event-stats', ...args] as const,
  providerAccounts: (id: string) => ['provider-accounts', id] as const,
  subscriptionPolicy: (id: string) => ['subscription-policy', id] as const,
  localCodexLogin: ['local-codex-login'] as const,
  localCopilotLogin: ['local-copilot-login'] as const,
  resetCredits: (accountId: string) => ['reset-credits', accountId] as const,
  requests: (q: RequestQuery) => ['requests', q] as const,
  request: (id: string) => ['request', id] as const,
  stats: (kind: string, ...args: unknown[]) => ['stats', kind, ...args] as const,
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

function invalidateProviderAccounts(qc: ReturnType<typeof useQueryClient>, providerId: string) {
  void qc.invalidateQueries({ queryKey: keys.providerAccounts(providerId) });
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
    onSuccess: (list) => qc.setQueryData(keys.providerAccounts(providerId), list),
  });
}

/** "Switch to this account": it becomes the provider's current account for the next request. */
export const useActivateProviderAccount = (providerId: string) =>
  useAccountListMutation(providerId, (id: string) =>
    api<ProviderAccount[]>('POST', `/api/provider-accounts/${enc(id)}/activate`));

export const useUpdateProviderAccount = (providerId: string) =>
  useAccountListMutation(providerId, ({ id, ...patch }: { id: string; enabled?: boolean; displayName?: string }) =>
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

export const useStartProviderLogin = (providerId: string) =>
  useMutation({
    mutationFn: (accountId?: string) =>
      api<SubscriptionLoginStart>('POST', `/api/providers/${enc(providerId)}/accounts/login`, {
        accountId: accountId || undefined,
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

// ---------- tokens ----------

export const useTokens = () =>
  useQuery({
    queryKey: keys.tokens,
    queryFn: () => api<Token[]>('GET', '/api/tokens'),
    refetchInterval: 15_000,
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
  useTokenMutation((body: { name: string; providerId?: string | null; accountId?: string | null }) =>
    api<Token>('POST', '/api/tokens', body),
  );

/** providerId: null clears direct calls; accountId: null / "" uses the provider's default account. */
export const useUpdateToken = () =>
  useTokenMutation((v: { id: string; name?: string; enabled?: boolean; providerId?: string | null; accountId?: string | null }) => {
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
  page: number;
  pageSize: number;
}

export const useRequests = (q: RequestQuery, live: boolean) =>
  useQuery({
    queryKey: keys.requests(q),
    queryFn: () => api<Page<RequestSummary>>('GET', `/api/requests${qs({ ...q })}`),
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
  });

/** The list's filters, minus paging — exactly what the live rate endpoint accepts. */
export type RequestRateFilter = Pick<RequestQuery, 'client' | 'token' | 'provider' | 'model' | 'status'>;

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

/** Daily request counts for the activity heatmap; the window is its own (default 365 days), not the page's range. */
export const useActivityHeatmap = (days = 365, client?: string, token?: string) =>
  useQuery({
    queryKey: keys.stats('activity', days, client ?? '', token ?? '', tzOffset()),
    queryFn: () => api<DailyActivity[]>('GET', `/api/stats/activity-heatmap${qs({ days, client, token, tzOffset: tzOffset() })}`),
    refetchInterval: 60_000,
    placeholderData: (prev) => prev,
  });
