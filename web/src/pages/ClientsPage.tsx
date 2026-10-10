import { closestCenter, DndContext, KeyboardSensor, PointerSensor, useSensor, useSensors, type DragEndEvent } from '@dnd-kit/core';
import { arrayMove, SortableContext, sortableKeyboardCoordinates, useSortable, verticalListSortingStrategy } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { useQueryClient } from '@tanstack/react-query';
import { Archive, Check, CircleArrowUp, Copy, Download, ExternalLink, FolderOpen, GripVertical, KeyRound, LoaderCircle, Plus, RefreshCw, RotateCcw, ScrollText, ShieldAlert, X } from 'lucide-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';

import {
  keys,
  useBackups,
  useCancelClientInstall,
  useCheckClientUpdates,
  useClaudeDirect,
  useClientInstallJob,
  useClientModels,
  useClients,
  useDisableClient,
  useEnableClient,
  useForceRestore,
  usePreviewEnable,
  useProviderAccounts,
  useProviders,
  useRestoreBackup,
  useSetBinding,
  useSetBindings,
  useStartClientInstall,
  useTokens,
} from '../api/hooks';
import type { ClientBinding, ClientInfo, ClientInstallJob, ClientKind, ConfigPreview, Provider } from '../api/types';
import { Alert } from '../components/arc/alert/alert';
import { ClaudeDirectPanel } from '../components/ClaudeDirectPanel';
import { CLIENT_META, CLIENT_ORDER, ClientIcon, ProviderIcon } from '../components/icons';
import { Page } from '../components/layout/Page';
import { Badge, Button, Dot, EmptyState, Group, Row, Spinner } from '../components/ui/controls';
import { errorText, Select, Sheet, useFeedback } from '../components/ui/overlays';
import { useI18n } from '../i18n';
import { cn } from '../lib/cn';
import { formatDateTime } from '../lib/format';
import { CLAUDE_CODE_MODELS, modelSlotsKey, modelSlotsOf, type ModelSlots } from '../lib/modelSlots';
import { ROLES, roleKey, rolesOf, type RoleMap } from '../lib/roleMap';
import { dirnameOf, systemActions } from '../shell/systemActions';

export function ClientsPage() {
  const { t } = useI18n();
  const navigate = useNavigate();
  const { toast } = useFeedback();
  const params = useParams<{ kind?: string }>();
  const clients = useClients();
  const checkUpdates = useCheckClientUpdates();
  const byKind = useMemo(() => new Map((clients.data ?? []).map((c) => [c.kind, c])), [clients.data]);
  const kind: ClientKind = CLIENT_ORDER.includes(params.kind as ClientKind) ? (params.kind as ClientKind) : 'codex';
  const client = byKind.get(kind);

  // Latest versions are fetched once per visit; the server keeps them six hours, so this is usually instant.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => checkUpdates.mutate(false), []);

  const checkNow = () =>
    checkUpdates.mutate(true, {
      onSuccess: (list) => {
        const count = list.filter((c) => c.install?.updateAvailable && c.install.blocker !== 'shadowed').length;
        toast(count > 0 ? t('clients.updatesFound', { count }) : t('clients.allUpToDate'), count > 0 ? undefined : 'success');
      },
      onError: (e) => toast(errorText(e), 'error'),
    });

  return (
    <Page
      title={t('nav.clients')}
      subtitle={t('clients.subtitle')}
      actions={
        <Button icon={<RefreshCw className="size-3.5" />} loading={checkUpdates.isPending} onClick={checkNow}>
          {t('clients.checkUpdates')}
        </Button>
      }
    >
      {/* Master-detail: client list on the left, the selected client's panel on the right. Below the container
          breakpoint the list folds into a horizontal strip above the panel. The panel is capped so rows stay
          readable on wide windows, and centers in whatever space the list leaves. */}
      <div className="@container mx-auto max-w-[1200px]">
        <div className="flex flex-col gap-4 @3xl:flex-row @3xl:items-start @3xl:gap-5">
          <ClientList clients={clients.data} loading={clients.isLoading} selected={kind} onSelect={(k) => navigate(`/clients/${k}`, { replace: true })} />
          <div className="mx-auto w-full max-w-[760px] min-w-0 flex-1">
            {clients.isLoading && <Spinner lines={4} />}
            {clients.isError && <Alert tone="danger" title={t('common.error')}>{errorText(clients.error)}</Alert>}
            {client && <ClientPanel client={client} />}
          </div>
        </div>
      </div>
    </Page>
  );
}

/** Clients whose config lists the bound provider's models (ClientKinds.WithModelList on the server). */
const MODEL_LIST_CLIENTS: ReadonlySet<ClientKind> = new Set<ClientKind>([
  'opencode', 'pi', 'minimax-code', 'copilot-cli', 'vscode-copilot',
  'crush', 'qwen-code', 'droid', 'kimi-code', 'zed', 'vscode-insiders', 'vscodium', 'omp', 'mimo-code', 'deepseek-harness', 'workbuddy', 'nextcowork',
  'muse-code',
]);

type ClientGroup = 'installed' | 'notInstalled' | 'comingSoon';
const GROUP_ORDER: ClientGroup[] = ['installed', 'notInstalled', 'comingSoon'];

function groupOf(c: ClientInfo): ClientGroup {
  if (c.availability === 'coming_soon') return 'comingSoon';
  // An enabled client has a config Astra wrote, so it counts as installed even if detection missed the binary.
  return c.detection.installed || c.enabled || c.install?.installed ? 'installed' : 'notInstalled';
}

/** The installed version: probed from the client itself, else whatever detection reported. */
function versionOf(c: ClientInfo): string | null {
  return c.install?.version ?? c.detection.version ?? null;
}

/**
 * Client list (the master side): grouped by install state in CLIENT_ORDER, each row shows the detected
 * version and an enabled / drifted mark. Narrow containers render it as a horizontal strip without groups.
 */
function ClientList({
  clients,
  loading,
  selected,
  onSelect,
}: {
  clients: ClientInfo[] | undefined;
  loading: boolean;
  selected: ClientKind;
  onSelect: (k: ClientKind) => void;
}) {
  const { t } = useI18n();
  const byKind = new Map((clients ?? []).map((c) => [c.kind, c]));
  const groups = GROUP_ORDER.map((g) => ({
    group: g,
    items: CLIENT_ORDER.flatMap((k) => {
      const c = byKind.get(k);
      return c && groupOf(c) === g ? [c] : [];
    }),
  })).filter((g) => g.items.length > 0);

  return (
    <nav aria-label={t('nav.clients')} className="card shrink-0 p-1.5 @3xl:sticky @3xl:top-0 @3xl:w-[232px]">
      {loading && <Spinner lines={6} className="p-2" />}
      <div className="flex gap-1 overflow-x-auto @3xl:flex-col @3xl:gap-0 @3xl:overflow-visible">
        {groups.map(({ group, items }) => (
          <div key={group} className="contents @3xl:block">
            <h3 className="hidden px-2.5 pt-2 pb-1 text-[11px] font-medium text-[var(--text-muted)] @3xl:block">
              {t(`clients.group.${group}` as 'clients.group.installed')} · {items.length}
            </h3>
            {items.map((c) => (
              <ClientListItem key={c.kind} client={c} active={c.kind === selected} onSelect={() => onSelect(c.kind)} />
            ))}
          </div>
        ))}
      </div>
    </nav>
  );
}

function ClientListItem({ client, active, onSelect }: { client: ClientInfo; active: boolean; onSelect: () => void }) {
  const { t } = useI18n();
  const dim = groupOf(client) !== 'installed';
  const version = versionOf(client);
  // Shadowed: the latest version is already installed behind the copy that runs — PATH needs fixing, not an update.
  const shadowed = client.install?.blocker === 'shadowed';
  const update = client.install?.updateAvailable && !shadowed ? client.install.latestVersion : null;
  return (
    <button
      type="button"
      aria-label={CLIENT_META[client.kind].name}
      aria-current={active ? 'page' : undefined}
      onClick={onSelect}
      className={cn(
        'flex shrink-0 items-center gap-2.5 rounded-[var(--radius-control)] px-2.5 py-1.5 text-left transition-colors @3xl:w-full',
        active ? 'bg-[var(--accent-subtle)]' : 'hover:bg-[var(--surface-muted)]',
      )}
    >
      <span className={cn('inline-flex', dim && !active && 'opacity-60')}>
        <ClientIcon kind={client.kind} size={24} />
      </span>
      <span className="min-w-0 flex-1">
        <span className={cn('block truncate text-[13px] whitespace-nowrap', active && 'font-medium')}>{CLIENT_META[client.kind].name}</span>
        {version && (
          <span className="hidden truncate text-[11px] text-[var(--text-secondary)] @3xl:block">
            {version}
            {update && <span className="text-[var(--warning)]"> → {update}</span>}
          </span>
        )}
      </span>
      {client.install?.busy && <ProgressSpinner />}
      {update && !client.install?.busy && (
        <Badge tone="orange" title={t('clients.updateTo', { version: update })}>
          {t('clients.updateBadge')}
        </Badge>
      )}
      {shadowed && !client.install?.busy && (
        <Badge tone="orange" title={t('clients.shadowed.title', { version: client.install?.latestVersion ?? '' })}>
          {t('clients.shadowedBadge')}
        </Badge>
      )}
      {client.status === 'drifted' ? (
        <span title={t('clients.drifted')} aria-label={t('clients.drifted')} className="inline-flex">
          <Dot tone="orange" />
        </span>
      ) : client.enabled ? (
        <Check className="size-3.5 shrink-0 text-[var(--success)]" strokeWidth={2.5} aria-label={t('clients.enabled')} />
      ) : null}
    </button>
  );
}

/** Small inline activity indicator (an install / update is running). */
function ProgressSpinner() {
  return <LoaderCircle className="size-3.5 shrink-0 animate-spin text-[var(--text-secondary)] motion-reduce:animate-none" aria-hidden />;
}

function statusBadge(c: ClientInfo, t: ReturnType<typeof useI18n>['t']) {
  if (c.availability === 'coming_soon') return <Badge>{t('clients.comingSoon')}</Badge>;
  if (c.status === 'drifted') return <Badge tone="orange">{t('clients.drifted')}</Badge>;
  if (c.enabled) return <Badge tone="green">{t('clients.enabled')}</Badge>;
  return <Badge>{t('clients.disabled')}</Badge>;
}

function ClientPanel({ client }: { client: ClientInfo }) {
  const { t, locale } = useI18n();
  const navigate = useNavigate();
  const { toast, confirm } = useFeedback();
  const providers = useProviders();
  const setBinding = useSetBinding();
  const disable = useDisableClient();
  const forceRestore = useForceRestore();
  const tokens = useTokens();
  const models = useClientModels(client.kind, Boolean(client.providerId));
  const [model, setModel] = useState<string | null>(client.selectedModel ?? null);
  const [account, setAccount] = useState<string>(client.accountId ?? '');
  const savedToken = client.tokenId ?? 'default';
  const [tokenId, setTokenId] = useState<string>(savedToken);
  // Enabled tokens, plus the client's current one even when it is disabled (so the picker never shows blank).
  const tokenOptions = (tokens.data ?? [])
    .filter((tk) => tk.enabled || tk.id === savedToken)
    .map((tk) => ({ value: tk.id, label: tk.enabled ? tk.name : t('clients.tokenDisabled', { name: tk.name }), disabled: !tk.enabled }));
  const [enableOpen, setEnableOpen] = useState(false);
  const [backupsOpen, setBackupsOpen] = useState(false);
  /** Install sheet: the action to confirm and run, "log" to watch the current / last run, null when closed. */
  const [installAction, setInstallAction] = useState<InstallSheetMode | null>(null);
  const soon = client.availability === 'coming_soon';
  const name = CLIENT_META[client.kind].name;
  const enabledProviders = (providers.data ?? []).filter((p) => p.enabled);
  const configPath = client.detection.configPaths[0];
  const boundProvider = enabledProviders.find((p) => p.id === client.providerId);
  const isSubscriptionBinding = boundProvider?.authScheme === 'oauth-subscription';
  const accounts = useProviderAccounts(boundProvider?.id, isSubscriptionBinding);
  const isDesktopClient = client.kind === 'claude-desktop';
  const isClaudeCode = client.kind === 'claude-code';
  // Claude Code's connection mode (Astra launcher): in direct mode the client talks to Anthropic itself, so the
  // gateway-only groups below are meaningless. Unknown state counts as gateway, which is also the server default.
  const direct = useClaudeDirect(isClaudeCode);
  const isDirectMode = isClaudeCode && direct.data?.mode === 'direct';
  const savedRoles = rolesOf(client.extras);
  const [roles, setRoles] = useState<RoleMap>(savedRoles);
  const hasRole = ROLES.some((r) => roles[r]);
  const rolesChanged = roleKey(roles) !== roleKey(savedRoles);
  // Claude Code model slots: the default model lives in selectedModel, the tiers in extras.models (plan §7.3).
  const savedSlots = modelSlotsOf(client.extras);
  const [slots, setSlots] = useState<ModelSlots>(savedSlots);
  const claudeModelsChanged = (model ?? null) !== (client.selectedModel ?? null) || modelSlotsKey(slots) !== modelSlotsKey(savedSlots);
  // The default model is the first slot, so only the extra tiers get a row of their own.
  const tierSlots = isClaudeCode ? CLAUDE_CODE_MODELS.filter((s) => s !== 'ANTHROPIC_MODEL') : [];
  const modelFooter = isClaudeCode
    ? client.enabled
      ? t('clients.modelSlotsFooterEnabled')
      : t('clients.modelSlotsFooter')
    : client.enabled
      ? t('clients.modelFooterEnabled')
      : t('clients.modelFooter');
  const extras = isDesktopClient
    ? { ...client.extras, roleMap: roles }
    : isClaudeCode
      ? { ...client.extras, models: slots }
      : undefined;

  useEffect(() => setModel(client.selectedModel ?? null), [client.kind, client.selectedModel]);
  useEffect(() => setAccount(client.accountId ?? ''), [client.kind, client.providerId, client.accountId]);
  useEffect(() => setTokenId(savedToken), [client.kind, savedToken]);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => setRoles(rolesOf(client.extras)), [client.kind, roleKey(savedRoles)]);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => setSlots(modelSlotsOf(client.extras)), [client.kind, modelSlotsKey(savedSlots)]);

  const saveAccount = () => {
    if (!client.providerId) return;
    setBinding.mutate(
      { kind: client.kind, providerId: client.providerId, accountId: account },
      {
        onSuccess: () => toast(t('common.saved'), 'success'),
        onError: (e) => toast(errorText(e), 'error'),
      },
    );
  };

  const doDisable = async () => {
    const ok = await confirm({ title: t('clients.disableConfirm', { name }), detail: t('clients.disableDetail'), confirmLabel: t('clients.disable') });
    if (!ok) return;
    disable.mutate(client.kind, {
      onSuccess: async (r) => {
        if (r.drifted.length === 0) {
          toast(t('clients.disabledToast', { name }), 'success');
          return;
        }
        const force = await confirm({
          title: t('clients.driftTitle'),
          detail: t('clients.driftDetail', { keys: r.drifted.join(', ') }),
          confirmLabel: t('clients.forceRestore'),
          destructive: true,
        });
        if (force) forceRestore.mutate(client.kind, { onSuccess: () => toast(t('clients.disabledToast', { name }), 'success'), onError: (e) => toast(errorText(e), 'error') });
      },
      onError: (e) => toast(errorText(e), 'error'),
    });
  };

  const openConfigDir = async () => {
    if (!configPath) return;
    if (systemActions.canOpenPaths) await systemActions.openPath(dirnameOf(configPath));
    else if (await systemActions.copy(configPath)) toast(t('common.copied'));
  };

  return (
    <>
      <Group>
        <Row
          icon={<ClientIcon kind={client.kind} size={36} />}
          label={
            <span className="flex items-center gap-2">
              <span className="text-[15px] font-medium">{name}</span>
              {isDirectMode ? <Badge>{t('clients.claudeDirect.direct')}</Badge> : statusBadge(client, t)}
            </span>
          }
          detail={
            soon
              ? client.availabilityReason ?? t('clients.comingSoonDetail')
              : [
                  groupOf(client) === 'installed' ? t('clients.detected', { version: versionOf(client) ?? '' }) : t('clients.notDetected'),
                  isDirectMode ? null : configPath,
                ]
                  .filter(Boolean)
                  .join(' · ')
          }
          className="py-3"
        >
          {!soon && <InstallActions client={client} onOpen={setInstallAction} />}
          {isDirectMode ? (
            <Button variant="primary" onClick={() => document.getElementById(CLAUDE_DIRECT_ANCHOR_ID)?.scrollIntoView({ behavior: 'smooth', block: 'start' })}>
              {t('clients.claudeDirect.configure')}
            </Button>
          ) : client.enabled ? (
            <Button onClick={() => void doDisable()} loading={disable.isPending}>
              {t('clients.disableRestore')}
            </Button>
          ) : (
            <Button
              variant="primary"
              disabled={soon || !client.providerId || (isDesktopClient && !hasRole)}
              onClick={() => setEnableOpen(true)}
              title={!client.providerId ? t('clients.pickProviderFirst') : isDesktopClient && !hasRole ? t('clients.roleMapRequired') : undefined}
            >
              {t('clients.enable')}
            </Button>
          )}
        </Row>
        {!isDirectMode && client.appliedAt && (
          <Row label={<span className="text-[12px] text-[var(--text-secondary)]">{t('clients.appliedAt', { time: formatDateTime(client.appliedAt, locale) })}</span>} />
        )}
      </Group>
      {!isDirectMode && client.warnings.length > 0 && (
        <div className="mb-5 flex flex-col gap-2">
          {client.warnings.map((w, i) => (
            <Alert key={i} tone="warning" title={w} />
          ))}
        </div>
      )}

      {!isDirectMode && <ProviderBindings client={client} providers={providers.data ?? []} loading={providers.isLoading} soon={soon} />}

      {!isDirectMode && isSubscriptionBinding && (
        <Group title={t('clients.accountTitle')} footer={t('clients.accountFooter')}>
          {accounts.isLoading ? (
            <div className="p-4">
              <Spinner lines={2} />
            </div>
          ) : (
            <Row label={t('clients.account')}>
              <Select
                className="max-w-[300px]"
                ariaLabel={t('clients.account')}
                value={account}
                onChange={setAccount}
                options={[
                  { value: '', label: t('clients.accountDefault') },
                  ...(accounts.data ?? []).map((a) => ({
                    value: a.id,
                    label:
                      (a.displayName || a.id) +
                      (a.status !== 'active' ? ` · ${t(`providers.subscription.status.${a.status}` as 'providers.subscription.status.active')}` : ''),
                  })),
                ]}
              />
              {account !== (client.accountId ?? '') && (
                <Button size="sm" variant="primary" loading={setBinding.isPending} onClick={saveAccount}>
                  {t('common.apply')}
                </Button>
              )}
            </Row>
          )}
        </Group>
      )}

      {!soon && !isDirectMode && (
        <Group title={t('clients.tokenTitle')} footer={client.enabled ? t('clients.tokenFooterEnabled') : t('clients.tokenFooter')}>
          <Row label={t('clients.token')}>
            <Select className="max-w-[300px]" ariaLabel={t('clients.token')} value={tokenId} onChange={setTokenId} options={tokenOptions} />
            {client.enabled && tokenId !== savedToken && (
              <Button size="sm" variant="primary" onClick={() => setEnableOpen(true)}>
                {t('common.apply')}
              </Button>
            )}
          </Row>
          <Row icon={<KeyRound className="size-4 text-[var(--text-secondary)]" />} label={t('clients.manageTokens')} onClick={() => navigate('/tokens')} />
        </Group>
      )}

      {!soon && !isDirectMode && client.providerId && isDesktopClient && (
        <Group title={t('clients.roleMapTitle')} footer={client.enabled ? t('clients.roleMapFooterEnabled') : t('clients.roleMapFooter')}>
          {ROLES.map((r) => (
            <Row key={r} label={t(`clients.role.${r}` as 'clients.role.sonnet')}>
              <Select
                ariaLabel={t(`clients.role.${r}` as 'clients.role.sonnet')}
                value={roles[r] ?? ''}
                onChange={(v) => setRoles((cur) => ({ ...cur, [r]: v || undefined }))}
                placeholder={t('clients.modelPlaceholder')}
                options={[{ value: '', label: t('clients.modelUnset') }, ...(models.data ?? []).map((m) => ({ value: m, label: m }))]}
                className="max-w-[300px]"
              />
            </Row>
          ))}
          {client.enabled && rolesChanged && (
            <Row label={hasRole ? '' : <span className="text-[12px] text-[var(--warning)]">{t('clients.roleMapRequired')}</span>}>
              <Button size="sm" variant="primary" disabled={!hasRole} onClick={() => setEnableOpen(true)}>
                {t('common.apply')}
              </Button>
            </Row>
          )}
        </Group>
      )}

      {!soon && !isDirectMode && client.providerId && !isDesktopClient && (
        <Group title={t('clients.modelTitle')} footer={modelFooter}>
          <Row label={t('clients.defaultModel')}>
            <Select
              value={model ?? ''}
              onChange={(v) => setModel(v || null)}
              placeholder={t('clients.modelPlaceholder')}
              options={[{ value: '', label: t('clients.modelUnset') }, ...(models.data ?? []).map((m) => ({ value: m, label: m }))]}
              className="max-w-[300px]"
            />
          </Row>
          {tierSlots.map((slot) => (
            <Row key={slot} label={t(`clients.modelSlot.${slot}` as 'clients.modelSlot.ANTHROPIC_MODEL')}>
              <Select
                ariaLabel={t(`clients.modelSlot.${slot}` as 'clients.modelSlot.ANTHROPIC_MODEL')}
                value={slots[slot] ?? ''}
                onChange={(v) => setSlots((cur) => ({ ...cur, [slot]: v || undefined }))}
                placeholder={t('clients.modelPlaceholder')}
                options={[{ value: '', label: t('clients.modelUnset') }, ...(models.data ?? []).map((m) => ({ value: m, label: m }))]}
                className="max-w-[300px]"
              />
            </Row>
          ))}
          {client.enabled && (isClaudeCode ? claudeModelsChanged : (model ?? null) !== (client.selectedModel ?? null)) && (
            <Row label="">
              <Button size="sm" variant="primary" onClick={() => setEnableOpen(true)}>
                {t('common.apply')}
              </Button>
            </Row>
          )}
        </Group>
      )}

      {!soon && client.install && <VersionGroup client={client} onShowLog={() => setInstallAction('log')} />}

      {isClaudeCode && <div id={CLAUDE_DIRECT_ANCHOR_ID}><ClaudeDirectPanel enabled={isClaudeCode} /></div>}

      {!soon && !isDirectMode && (
        <Group title={t('clients.maintenance')}>
          {configPath && (
            <Row
              icon={systemActions.canOpenPaths ? <FolderOpen className="size-4 text-[var(--text-secondary)]" /> : <Copy className="size-4 text-[var(--text-secondary)]" />}
              label={systemActions.canOpenPaths ? t('clients.openConfigDir') : t('clients.copyConfigPath')}
              detail={configPath}
              onClick={() => void openConfigDir()}
            />
          )}
          <Row icon={<Archive className="size-4 text-[var(--text-secondary)]" />} label={t('clients.backups')} detail={t('clients.backupsDetail')} onClick={() => setBackupsOpen(true)} />
        </Group>
      )}

      <EnableSheet open={enableOpen} onOpenChange={setEnableOpen} client={client} model={model} extras={extras} tokenId={tokenId} />
      <BackupsSheet open={backupsOpen} onOpenChange={setBackupsOpen} client={client} />
      <InstallSheet mode={installAction} onClose={() => setInstallAction(null)} client={client} />
    </>
  );
}

type InstallSheetMode = ClientInstallJob['action'] | 'log';

function providerDetail(p: Provider, t: ReturnType<typeof useI18n>['t']): string {
  return [p.priceKey ?? p.templateId, t('providers.modelCount', { count: p.modelCount }), p.priceMultiplier !== 1 ? `×${p.priceMultiplier}` : null]
    .filter(Boolean)
    .join(' · ');
}

/**
 * The client's providers in routing order (drag the handle to reorder) and the enabled providers it can add. The
 * gateway sends a request to the first bound provider that serves the requested model id (one of its enabled models, or
 * its model mapping onto one), else to the first one; the client's model list is the union (server ClientRouting).
 */
function ProviderBindings({ client, providers, loading, soon }: { client: ClientInfo; providers: Provider[]; loading: boolean; soon: boolean }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const setBindings = useSetBindings();
  const saved = useMemo<ClientBinding[]>(
    () => client.bindings ?? (client.providerId ? [{ providerId: client.providerId, accountId: client.accountId }] : []),
    [client.bindings, client.providerId, client.accountId],
  );
  // Shown immediately while the save is in flight; reverts when it fails.
  const [order, setOrder] = useState<ClientBinding[]>(saved);
  useEffect(() => setOrder(saved), [saved]);
  const byId = useMemo(() => new Map(providers.map((p) => [p.id, p])), [providers]);
  const bound = order.flatMap((b) => {
    const provider = byId.get(b.providerId);
    return provider ? [{ binding: b, provider }] : [];
  });
  const available = providers.filter((p) => p.enabled && !order.some((b) => b.providerId === p.id));
  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 4 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );
  const busy = soon || setBindings.isPending;

  const save = (next: ClientBinding[], done: string) => {
    const previous = order;
    setOrder(next);
    setBindings.mutate(
      { kind: client.kind, bindings: next },
      {
        onSuccess: () =>
          toast(MODEL_LIST_CLIENTS.has(client.kind) && client.enabled ? t('clients.providersModelsSynced', { client: CLIENT_META[client.kind].name }) : done),
        onError: (e) => {
          setOrder(previous);
          toast(errorText(e), 'error');
        },
      },
    );
  };

  const onDragEnd = ({ active, over }: DragEndEvent) => {
    if (!over || active.id === over.id) return;
    const from = order.findIndex((b) => b.providerId === active.id);
    const to = order.findIndex((b) => b.providerId === over.id);
    if (from < 0 || to < 0) return;
    save(arrayMove(order, from, to), t('clients.providerOrderSaved'));
  };

  const add = (p: Provider) => save([...order, { providerId: p.id }], t('clients.providerAdded', { provider: p.name }));
  const remove = (p: Provider) => save(order.filter((b) => b.providerId !== p.id), t('clients.providerRemoved', { provider: p.name }));

  const availableRows = available.map((p) => (
    <Row
      key={p.id}
      icon={<ProviderIcon name={p.name} icon={p.icon} colorKey={p.templateId} size={24} />}
      label={p.name}
      detail={providerDetail(p, t)}
      onClick={busy ? undefined : () => add(p)}
    >
      {!soon && <Plus className="size-4 text-[var(--text-secondary)]" aria-label={t('common.add')} />}
    </Row>
  ));

  return (
    <>
      <Group title={t('clients.providerTitle')} footer={t('clients.providerFooter')}>
        {loading && (
          <div className="p-4">
            <Spinner lines={2} />
          </div>
        )}
        {!loading && bound.length === 0 && available.length === 0 && (
          <EmptyState
            title={t('clients.noProviders')}
            detail={t('clients.noProvidersDetail')}
            action={
              <Link to="/providers?new=1">
                <Button variant="primary" icon={<Plus className="size-3.5" />}>
                  {t('providers.add')}
                </Button>
              </Link>
            }
          />
        )}
        {bound.length > 0 && (
          <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={onDragEnd}>
            <SortableContext items={bound.map((b) => b.provider.id)} strategy={verticalListSortingStrategy}>
              {bound.map(({ provider }, i) => (
                <SortableProviderRow
                  key={provider.id}
                  provider={provider}
                  primary={i === 0}
                  disabled={busy}
                  onRemove={bound.length > 1 ? () => remove(provider) : undefined}
                />
              ))}
            </SortableContext>
          </DndContext>
        )}
        {!loading && bound.length === 0 && availableRows}
      </Group>
      {bound.length > 0 && available.length > 0 && <Group title={t('clients.moreProvidersTitle')}>{availableRows}</Group>}
    </>
  );
}

function SortableProviderRow({ provider, primary, disabled, onRemove }: { provider: Provider; primary: boolean; disabled: boolean; onRemove?: () => void }) {
  const { t } = useI18n();
  const { attributes, listeners, setNodeRef, setActivatorNodeRef, transform, transition, isDragging } = useSortable({ id: provider.id, disabled });
  return (
    <div
      ref={setNodeRef}
      style={{ transform: CSS.Transform.toString(transform), transition }}
      className={cn(isDragging && 'relative z-10 bg-[var(--surface)] shadow-lg')}
    >
      <Row
        icon={
          <span className="flex items-center gap-2">
            <button
              ref={setActivatorNodeRef}
              type="button"
              aria-label={t('clients.dragToReorder', { provider: provider.name })}
              title={t('clients.dragToReorder', { provider: provider.name })}
              className="-ml-1 inline-flex cursor-grab touch-none text-[var(--text-muted)] hover:text-[var(--text-secondary)] active:cursor-grabbing disabled:cursor-default disabled:opacity-50"
              {...attributes}
              {...listeners}
            >
              <GripVertical className="size-4" />
            </button>
            <ProviderIcon name={provider.name} icon={provider.icon} colorKey={provider.templateId} size={24} />
          </span>
        }
        label={
          <span className="flex items-center gap-2">
            <span className="truncate">{provider.name}</span>
            {primary && <Badge tone="accent">{t('clients.primaryProvider')}</Badge>}
            {!provider.enabled && <Badge tone="orange">{t('clients.providerDisabledBadge')}</Badge>}
          </span>
        }
        detail={providerDetail(provider, t)}
      >
        {onRemove && (
          <Button
            size="sm"
            variant="plain"
            icon={<X className="size-3.5" />}
            aria-label={t('clients.removeProvider', { provider: provider.name })}
            title={t('clients.removeProvider', { provider: provider.name })}
            disabled={disabled}
            onClick={onRemove}
          />
        )}
      </Row>
    </div>
  );
}

/**
 * Header buttons: install a missing client, update an outdated one, or open the download page of a manual one.
 * When npm's global directories belong to root, the button says so up front: running it will ask for the
 * administrator password (macOS does not let any app elevate silently).
 */
function InstallActions({ client, onOpen }: { client: ClientInfo; onOpen: (mode: InstallSheetMode) => void }) {
  const { t } = useI18n();
  const inst = client.install;
  if (!inst) return null;
  const admin = inst.needsAdmin ? (
    <span className="inline-flex items-center gap-1 text-[11px] text-[var(--warning)]">
      <ShieldAlert className="size-3" aria-hidden />
      {t('clients.admin.badge')}
    </span>
  ) : null;
  const adminTip = inst.needsAdmin ? t(inst.adminAvailable ? 'clients.admin.tip' : 'clients.admin.tipManual') : undefined;
  if (inst.busy) {
    return (
      <Button icon={<ProgressSpinner />} onClick={() => onOpen('log')}>
        {t('clients.installRunning')}
      </Button>
    );
  }
  if (inst.updateAvailable && inst.updateCommand && inst.latestVersion) {
    return (
      <Button icon={<CircleArrowUp className="size-3.5" />} title={adminTip} onClick={() => onOpen('update')}>
        {t('clients.updateTo', { version: inst.latestVersion })}
        {admin}
      </Button>
    );
  }
  if (inst.updateAvailable && inst.blocker === 'shadowed') {
    return (
      <Button
        icon={<ShieldAlert className="size-3.5" />}
        onClick={() => document.getElementById(SHADOWED_ALERT_ID)?.scrollIntoView({ behavior: 'smooth', block: 'center' })}
      >
        {t('clients.shadowedBadge')}
      </Button>
    );
  }
  if (!inst.installed && inst.installCommand) {
    return (
      <Button icon={<Download className="size-3.5" />} title={adminTip} onClick={() => onOpen('install')}>
        {t('clients.install')}
        {admin}
      </Button>
    );
  }
  if (!inst.installed && inst.method === 'manual') {
    return (
      <Button icon={<ExternalLink className="size-3.5" />} onClick={() => window.open(inst.homepageUrl, '_blank', 'noreferrer')}>
        {t('clients.download')}
      </Button>
    );
  }
  return null;
}

const SHADOWED_ALERT_ID = 'client-shadowed-alert';

/** Anchor of the Claude Code direct panel, so the header button can scroll to it. */
const CLAUDE_DIRECT_ANCHOR_ID = 'claude-direct-panel';

/** Installed vs latest version, how the client is installed, and why an action is unavailable. */
function VersionGroup({ client, onShowLog }: { client: ClientInfo; onShowLog: () => void }) {
  const { t, locale } = useI18n();
  const inst = client.install!;
  const job = useClientInstallJob(client.kind);
  const value = (text: string, muted = false) => (
    <span className={cn('text-[13px] tabular-nums selectable', muted && 'text-[var(--text-secondary)]')}>{text}</span>
  );
  const blocker =
    inst.blocker === 'external-install'
      ? t('clients.blocker.external-install', { path: inst.executable ?? '' })
      : inst.blocker
        ? t(`clients.blocker.${inst.blocker}` as 'clients.blocker.npm-missing')
        : undefined;
  const others = inst.otherCopies ?? [];
  // A newer copy behind the one that runs: an earlier install/update went where the terminal does not look first.
  const shadowing = others.find((c) => c.newer);
  return (
    <>
      {shadowing && (
        <div id={SHADOWED_ALERT_ID} className="mb-3">
          <Alert tone="warning" title={t('clients.shadowed.title', { version: shadowing.version })}>
            {t('clients.shadowed.detail', {
              path: inst.executable ?? '',
              version: inst.version ?? '',
              other: shadowing.path,
              dir: dirnameOf(shadowing.path),
            })}
          </Alert>
        </div>
      )}
      {!shadowing && inst.notOnPath && inst.executable && (
        <div className="mb-3">
          <Alert tone="warning" title={t('clients.notOnPath.title')}>
            {t('clients.notOnPath.detail', { dir: dirnameOf(inst.executable) })}
          </Alert>
        </div>
      )}
    <Group title={t('clients.versionTitle')} footer={blocker}>
      <Row label={t('clients.currentVersion')} detail={inst.installed ? inst.executable : undefined}>
        {inst.installed ? value(inst.version ?? t('clients.versionUnknown'), !inst.version) : value(t('clients.notInstalled'), true)}
      </Row>
      {others.map((c) => (
        <Row key={c.path} label={t('clients.otherCopy')} detail={c.path}>
          {c.newer && <Badge tone="orange">{t('clients.otherCopyNewer')}</Badge>}
          {value(c.version, !c.newer)}
        </Row>
      ))}
      {(inst.method === 'npm' || inst.method === 'script') && (
        <Row
          label={t('clients.latestVersion')}
          detail={inst.latestError ?? (inst.latestCheckedAt ? t('clients.checkedAt', { time: formatDateTime(inst.latestCheckedAt, locale) }) : undefined)}
        >
          {inst.updateAvailable && <Badge tone="orange">{t('clients.updateBadge')}</Badge>}
          {value(inst.latestVersion ?? t('clients.latestUnknown'), !inst.latestVersion)}
        </Row>
      )}
      <Row
        label={t('clients.installMethod')}
        detail={t(`clients.method.${inst.method}` as 'clients.method.npm', { package: inst.package ?? '' })}
      >
        <Button size="sm" variant="plain" icon={<ExternalLink className="size-3" />} onClick={() => window.open(inst.homepageUrl, '_blank', 'noreferrer')}>
          {t('clients.homepage')}
        </Button>
      </Row>
      {job.data && (
        <Row
          icon={<ScrollText className="size-4 text-[var(--text-secondary)]" />}
          label={t('clients.installLog')}
          detail={`${job.data.command} · ${t(`clients.job.${job.data.state}` as 'clients.job.running')}`}
          onClick={onShowLog}
        />
      )}
    </Group>
    </>
  );
}

/**
 * Runs an install / update after showing the exact command, then streams its output (polled). With mode "log"
 * it only shows the current or last run. Closing the sheet does not stop a run; "Stop" does.
 */
function InstallSheet({ mode, onClose, client }: { mode: InstallSheetMode | null; onClose: () => void; client: ClientInfo }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const qc = useQueryClient();
  const open = mode !== null;
  const job = useClientInstallJob(client.kind, open || Boolean(client.install?.busy));
  const start = useStartClientInstall();
  const cancel = useCancelClientInstall();
  const [started, setStarted] = useState(false);
  const logRef = useRef<HTMLPreElement>(null);
  const lastState = useRef<string | undefined>(undefined);
  const name = CLIENT_META[client.kind].name;
  const inst = client.install;
  const action = mode === 'install' || mode === 'update' ? mode : null;
  const command = action === 'install' ? inst?.installCommand : action === 'update' ? inst?.updateCommand : null;
  const data = job.data;
  // Confirm first unless a run is already going (or the sheet was opened just to watch the log).
  const confirming = action !== null && !started && data?.state !== 'running';
  const needsAdmin = Boolean(inst?.needsAdmin);
  const canElevate = Boolean(inst?.adminAvailable);
  // A failed run that hit a permission error can be retried with admin rights (once; an elevated run is final).
  const retryAsAdmin = data?.state === 'failed' && data.hint === 'permission-denied' && !data.elevated && canElevate;

  useEffect(() => {
    if (open) setStarted(false);
  }, [open, mode]);

  // A run that just finished: refresh versions and say so (also when the sheet was closed meanwhile).
  useEffect(() => {
    const state = data?.state;
    if (lastState.current === 'running' && state && state !== 'running') {
      void qc.invalidateQueries({ queryKey: keys.clients });
      if (state === 'succeeded' && data.hint) toast(t('clients.notEffectiveToast', { name }));
      else if (state === 'succeeded') toast(t(data.action === 'install' ? 'clients.installedToast' : 'clients.updatedToast', { name }), 'success');
    }
    lastState.current = state;
  }, [data?.state, data?.action, data?.hint, name, qc, t, toast]);

  useEffect(() => {
    const el = logRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [data?.log.length]);

  const run = (which: ClientInstallJob['action'], elevated: boolean) =>
    start.mutate({ kind: client.kind, action: which, elevated }, { onSuccess: () => setStarted(true), onError: (e) => toast(errorText(e), 'error') });

  const copy = async (text: string) => {
    if (await systemActions.copy(text)) toast(t('common.copied'));
  };

  const title = t((action ?? data?.action) === 'update' ? 'clients.updateTitle' : 'clients.installTitle', { name });
  const statusTone = { running: 'info', succeeded: 'success', failed: 'danger', cancelled: 'warning' } as const;
  const codeBox = 'rounded-[var(--radius-control)] border border-[var(--border)] bg-[var(--surface-muted)] px-3 py-2 font-mono text-[12px] break-all whitespace-pre-wrap selectable';
  // The one-time fix that makes admin rights unnecessary from then on.
  const fix = inst?.adminFixCommand ? (
    <div className="flex flex-col gap-2">
      <p className="text-[11px] text-[var(--text-secondary)]">{t('clients.admin.fixTip')}</p>
      <div className="flex items-start gap-2">
        <pre className={cn(codeBox, 'min-w-0 flex-1')}>{inst.adminFixCommand}</pre>
        <Button size="sm" icon={<Copy className="size-3" />} aria-label={t('common.copy')} onClick={() => void copy(inst.adminFixCommand!)} />
      </div>
    </div>
  ) : null;

  return (
    <Sheet
      open={open}
      onOpenChange={(o) => !o && onClose()}
      width={620}
      title={title}
      description={confirming ? t('clients.installRunDetail') : undefined}
      footer={
        confirming ? (
          <>
            <Button onClick={onClose}>{t('common.cancel')}</Button>
            {needsAdmin && !canElevate ? (
              // No system prompt here (e.g. Linux without pkexec): the user runs it in a terminal.
              <Button variant="primary" icon={<Copy className="size-3.5" />} disabled={!command} onClick={() => void copy(`sudo ${command}`)}>
                {t('clients.admin.copyCommand')}
              </Button>
            ) : (
              <Button
                variant="primary"
                icon={needsAdmin ? <ShieldAlert className="size-3.5" /> : undefined}
                disabled={!command}
                loading={start.isPending}
                onClick={() => action && run(action, needsAdmin)}
              >
                {needsAdmin ? t('clients.admin.start') : t('clients.installStart')}
              </Button>
            )}
          </>
        ) : data?.state === 'running' ? (
          <>
            {/* An elevated command runs as root under the system prompt; it cannot be stopped from here. */}
            {!data.elevated && (
              <Button variant="destructive" loading={cancel.isPending} onClick={() => cancel.mutate(client.kind, { onError: (e) => toast(errorText(e), 'error') })}>
                {t('clients.installStop')}
              </Button>
            )}
            <Button onClick={onClose}>{t('common.close')}</Button>
          </>
        ) : (
          <>
            <Button onClick={onClose}>{t('common.done')}</Button>
            {retryAsAdmin && (
              <Button variant="primary" icon={<ShieldAlert className="size-3.5" />} loading={start.isPending} onClick={() => run(data.action, true)}>
                {t('clients.admin.retry')}
              </Button>
            )}
          </>
        )
      }
    >
      {confirming ? (
        <div className="flex flex-col gap-3">
          {needsAdmin && (
            <Alert tone="warning" title={t('clients.admin.title')}>
              {t(canElevate ? 'clients.admin.prompt' : 'clients.admin.manual')}
            </Alert>
          )}
          {command ? <pre className={codeBox}>{needsAdmin ? `sudo ${command}` : command}</pre> : <Alert tone="warning" title={t('clients.installUnavailable')} />}
          {inst?.method === 'npm' && <p className="text-[11px] text-[var(--text-muted)]">{t('clients.installNpmNote')}</p>}
          {needsAdmin && fix}
        </div>
      ) : data ? (
        <div className="flex flex-col gap-3">
          <Alert
            tone={data.state === 'succeeded' && data.hint ? 'warning' : statusTone[data.state]}
            title={t(data.state === 'succeeded' && data.hint ? 'clients.job.notEffective' : (`clients.job.${data.state}` as 'clients.job.running'))}
          >
            {data.state === 'running' && data.elevated
              ? t('clients.admin.running')
              : data.hint
                ? t(`clients.hint.${data.hint}` as 'clients.hint.timeout') + (retryAsAdmin ? t('clients.admin.retryHint') : '')
                : undefined}
          </Alert>
          <pre
            ref={logRef}
            className="scroll max-h-[340px] min-h-[160px] rounded-[var(--radius-control)] border border-[var(--border)] bg-[var(--surface-muted)] px-3 py-2 font-mono text-[11px] leading-relaxed break-all whitespace-pre-wrap selectable"
          >
            {data.log.join('\n')}
          </pre>
          {data.hint === 'permission-denied' && fix}
        </div>
      ) : job.isLoading ? (
        <Spinner lines={4} />
      ) : (
        <EmptyState icon={<ScrollText className="size-6" />} title={t('clients.noInstallLog')} />
      )}
    </Sheet>
  );
}

/** Preview of the config edits (unified diff) before enabling — nothing is written until confirmed. */
function EnableSheet({
  open,
  onOpenChange,
  client,
  model,
  extras,
  tokenId,
}: {
  open: boolean;
  onOpenChange: (o: boolean) => void;
  client: ClientInfo;
  model: string | null;
  /** Replaces the client's stored extras (Claude model slots / Claude Desktop role map); omitted = keep them. */
  extras?: Record<string, unknown>;
  /** Token written into the config as "<token>.<kind>". */
  tokenId: string;
}) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const preview = usePreviewEnable();
  const enable = useEnableClient();
  const name = CLIENT_META[client.kind].name;
  const req = extras ? { providerId: client.providerId, model, extras, tokenId } : { providerId: client.providerId, model, tokenId };

  useEffect(() => {
    if (open) preview.mutate({ kind: client.kind, req });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  const confirmEnable = () =>
    enable.mutate(
      { kind: client.kind, req },
      {
        onSuccess: (c) => {
          onOpenChange(false);
          toast(c.requiresRestart ? t('clients.enabledRestart', { name }) : t('clients.enabledToast', { name }), 'success');
        },
        onError: (e) => toast(errorText(e), 'error'),
      },
    );

  return (
    <Sheet
      open={open}
      onOpenChange={onOpenChange}
      width={620}
      title={t('clients.enableTitle', { name })}
      description={t('clients.enableDetail')}
      footer={
        <>
          <Button onClick={() => onOpenChange(false)}>{t('common.cancel')}</Button>
          <Button variant="primary" disabled={!preview.data} loading={enable.isPending} onClick={confirmEnable}>
            {client.enabled ? t('common.apply') : t('clients.enable')}
          </Button>
        </>
      }
    >
      {preview.isPending && <Spinner lines={4} />}
      {preview.isError && <Alert tone="danger" title={t('common.error')}>{errorText(preview.error)}</Alert>}
      {preview.data && <PreviewView preview={preview.data} />}
    </Sheet>
  );
}

function PreviewView({ preview }: { preview: ConfigPreview }) {
  const { t } = useI18n();
  return (
    <div className="flex flex-col gap-4">
      {preview.warnings.map((w, i) => (
        <Alert key={i} tone="warning" title={w} />
      ))}
      {preview.changes.length === 0 && <Alert tone="info" title={t('clients.noChanges')} />}
      {preview.diffs.map((d) => (
        <div key={d.file}>
          <div className="mb-2 font-mono text-[11px] text-[var(--text-secondary)]">{d.file}</div>
          <DiffView diff={d.unifiedDiff} />
        </div>
      ))}
    </div>
  );
}

export function DiffView({ diff }: { diff: string }) {
  return (
    <pre className="scroll max-h-[300px] rounded-[var(--radius-control)] border border-[var(--border)] bg-[var(--surface-muted)] py-2 font-mono text-[11px] leading-relaxed selectable">
      {diff.split('\n').map((line, i) => (
        <div
          key={i}
          className={cn(
            'px-3 whitespace-pre-wrap break-all',
            line.startsWith('+') && !line.startsWith('+++') && 'bg-[color-mix(in_oklab,var(--success)_12%,transparent)] text-[var(--success)]',
            line.startsWith('-') && !line.startsWith('---') && 'bg-[color-mix(in_oklab,var(--danger)_10%,transparent)] text-[var(--danger)]',
            line.startsWith('@@') && 'text-[var(--text-muted)]',
          )}
        >
          {line || ' '}
        </div>
      ))}
    </pre>
  );
}

function BackupsSheet({ open, onOpenChange, client }: { open: boolean; onOpenChange: (o: boolean) => void; client: ClientInfo }) {
  const { t, locale } = useI18n();
  const { toast, confirm } = useFeedback();
  const backups = useBackups(client.kind, open);
  const restore = useRestoreBackup();
  return (
    <Sheet open={open} onOpenChange={onOpenChange} width={520} title={t('clients.backups')} description={t('clients.backupsDetail')} footer={<Button onClick={() => onOpenChange(false)}>{t('common.done')}</Button>}>
      {backups.isLoading && <Spinner lines={3} />}
      {backups.isError && <Alert tone="danger" title={t('common.error')}>{errorText(backups.error)}</Alert>}
      {backups.data?.length === 0 && <EmptyState icon={<Archive className="size-6" />} title={t('clients.noBackups')} />}
      <div className="card divide-y divide-[var(--border)]">
        {backups.data?.map((b) => (
          <Row
            key={b.id}
            label={
              <span className="flex items-center gap-2">
                {formatDateTime(b.createdAt, locale)}
                {b.firstWrite && <Badge tone="accent">{t('clients.firstWrite')}</Badge>}
              </span>
            }
            detail={b.files.join(', ')}
          >
            <Button
              size="sm"
              icon={<RotateCcw className="size-3" />}
              loading={restore.isPending && restore.variables?.id === b.id}
              onClick={async () => {
                if (!(await confirm({ title: t('clients.restoreConfirm'), detail: b.files.join('\n'), destructive: true, confirmLabel: t('clients.restore') }))) return;
                restore.mutate({ kind: client.kind, id: b.id }, { onSuccess: () => toast(t('clients.restored'), 'success'), onError: (e) => toast(errorText(e), 'error') });
              }}
            >
              {t('clients.restore')}
            </Button>
          </Row>
        ))}
      </div>
    </Sheet>
  );
}
