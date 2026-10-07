import { Archive, Check, Copy, FolderOpen, KeyRound, Plus, RotateCcw } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';

import {
  useBackups,
  useClientModels,
  useClients,
  useDisableClient,
  useEnableClient,
  useForceRestore,
  usePreviewEnable,
  useProviderAccounts,
  useProviders,
  useRestoreBackup,
  useRotateKey,
  useSetBinding,
} from '../api/hooks';
import type { ClientInfo, ClientKind, ConfigPreview, Provider } from '../api/types';
import { Alert } from '../components/arc/alert/alert';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '../components/arc/tabs/tabs';
import { CLIENT_META, CLIENT_ORDER, ClientGlyph, ClientIcon, ProviderIcon } from '../components/icons';
import { Page } from '../components/layout/Page';
import { Badge, Button, EmptyState, Group, Row, Spinner } from '../components/ui/controls';
import { errorText, Select, Sheet, useFeedback } from '../components/ui/overlays';
import { useI18n } from '../i18n';
import { cn } from '../lib/cn';
import { formatDateTime } from '../lib/format';
import { ROLES, roleKey, rolesOf, type RoleMap } from '../lib/roleMap';
import { dirnameOf, systemActions } from '../shell/systemActions';

export function ClientsPage() {
  const { t } = useI18n();
  const navigate = useNavigate();
  const params = useParams<{ kind?: string }>();
  const clients = useClients();
  const byKind = useMemo(() => new Map((clients.data ?? []).map((c) => [c.kind, c])), [clients.data]);
  const kind: ClientKind = CLIENT_ORDER.includes(params.kind as ClientKind) ? (params.kind as ClientKind) : 'codex';
  const client = byKind.get(kind);

  return (
    <Page title={t('nav.clients')} subtitle={t('clients.subtitle')}>
      <Tabs className="mx-auto max-w-[720px]" value={kind} onValueChange={(k) => navigate(`/clients/${k}`, { replace: true })}>
        <ClientTabs clients={byKind} />
        {clients.isLoading && <Spinner lines={4} className="mt-6" />}
        {clients.isError && (
          <div className="mt-6">
            <Alert tone="danger" title={t('common.error')}>{errorText(clients.error)}</Alert>
          </div>
        )}
        {client && (
          <TabsContent value={kind} className="pt-5">
            <ClientPanel client={client} />
          </TabsContent>
        )}
      </Tabs>
    </Page>
  );
}

/** Fixed client tabs (cc-switch style): official logo, name and an enabled mark. */
function ClientTabs({ clients }: { clients: Map<ClientKind, ClientInfo> }) {
  const { t } = useI18n();
  return (
    <TabsList aria-label={t('nav.clients')}>
      {CLIENT_ORDER.map((k) => {
        const c = clients.get(k);
        return (
          <TabsTrigger key={k} value={k} className={cn(c?.availability === 'coming_soon' && 'opacity-60')}>
            <span className="inline-flex items-center gap-2 whitespace-nowrap">
              <ClientGlyph kind={k} size={18} />
              {CLIENT_META[k].name}
              {c?.enabled && <Check className="size-3.5 text-[var(--success)]" strokeWidth={2.5} aria-label={t('clients.enabled')} />}
            </span>
          </TabsTrigger>
        );
      })}
    </TabsList>
  );
}

function statusBadge(c: ClientInfo, t: ReturnType<typeof useI18n>['t']) {
  if (c.availability === 'coming_soon') return <Badge>{t('clients.comingSoon')}</Badge>;
  if (c.status === 'drifted') return <Badge tone="orange">{t('clients.drifted')}</Badge>;
  if (c.enabled) return <Badge tone="green">{t('clients.enabled')}</Badge>;
  return <Badge>{t('clients.disabled')}</Badge>;
}

function ClientPanel({ client }: { client: ClientInfo }) {
  const { t, locale } = useI18n();
  const { toast, confirm } = useFeedback();
  const providers = useProviders();
  const setBinding = useSetBinding();
  const disable = useDisableClient();
  const forceRestore = useForceRestore();
  const rotateKey = useRotateKey();
  const models = useClientModels(client.kind, Boolean(client.providerId));
  const [model, setModel] = useState<string | null>(client.selectedModel ?? null);
  const [account, setAccount] = useState<string>(client.accountId ?? '');
  const [enableOpen, setEnableOpen] = useState(false);
  const [backupsOpen, setBackupsOpen] = useState(false);
  const soon = client.availability === 'coming_soon';
  const name = CLIENT_META[client.kind].name;
  const enabledProviders = (providers.data ?? []).filter((p) => p.enabled);
  const configPath = client.detection.configPaths[0];
  const boundProvider = enabledProviders.find((p) => p.id === client.providerId);
  const isSubscriptionBinding = boundProvider?.authScheme === 'oauth-subscription';
  const accounts = useProviderAccounts(boundProvider?.id, isSubscriptionBinding);
  const isDesktopClient = client.kind === 'claude-desktop';
  const savedRoles = rolesOf(client.extras);
  const [roles, setRoles] = useState<RoleMap>(savedRoles);
  const hasRole = ROLES.some((r) => roles[r]);
  const rolesChanged = roleKey(roles) !== roleKey(savedRoles);
  const extras = isDesktopClient ? { ...client.extras, roleMap: roles } : undefined;

  useEffect(() => setModel(client.selectedModel ?? null), [client.kind, client.selectedModel]);
  useEffect(() => setAccount(client.accountId ?? ''), [client.kind, client.providerId, client.accountId]);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => setRoles(rolesOf(client.extras)), [client.kind, roleKey(savedRoles)]);

  const bind = (p: Provider) => {
    if (p.id === client.providerId) return;
    setBinding.mutate(
      { kind: client.kind, providerId: p.id },
      {
        onSuccess: () =>
          toast(client.kind === 'opencode' && client.enabled ? t('clients.boundModelsSynced', { provider: p.name }) : t('clients.bound', { provider: p.name })),
        onError: (e) => toast(errorText(e), 'error'),
      },
    );
  };

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
              {statusBadge(client, t)}
            </span>
          }
          detail={
            soon
              ? client.availabilityReason ?? t('clients.comingSoonDetail')
              : [
                  client.detection.installed ? t('clients.detected', { version: client.detection.version ?? '' }) : t('clients.notDetected'),
                  configPath,
                ]
                  .filter(Boolean)
                  .join(' · ')
          }
          className="py-3"
        >
          {client.enabled ? (
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
        {client.appliedAt && (
          <Row label={<span className="text-[12px] text-[var(--text-secondary)]">{t('clients.appliedAt', { time: formatDateTime(client.appliedAt, locale) })}</span>} />
        )}
      </Group>
      {client.warnings.length > 0 && (
        <div className="mb-5 flex flex-col gap-2">
          {client.warnings.map((w, i) => (
            <Alert key={i} tone="warning" title={w} />
          ))}
        </div>
      )}

      <Group title={t('clients.providerTitle')} footer={t('clients.providerFooter')}>
        {providers.isLoading && (
          <div className="p-4">
            <Spinner lines={2} />
          </div>
        )}
        {enabledProviders.length === 0 && !providers.isLoading && (
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
        {enabledProviders.map((p) => (
          <Row
            key={p.id}
            icon={<ProviderIcon name={p.name} icon={p.icon} colorKey={p.templateId} size={24} />}
            label={p.name}
            detail={[p.priceKey ?? p.templateId, t('providers.modelCount', { count: p.modelCount }), p.priceMultiplier !== 1 ? `×${p.priceMultiplier}` : null].filter(Boolean).join(' · ')}
            onClick={soon ? undefined : () => bind(p)}
          >
            {(setBinding.isPending ? setBinding.variables?.providerId === p.id : p.id === client.providerId) ? (
              <Check className="size-4 text-[var(--accent)]" strokeWidth={2.5} aria-label={t('clients.current')} />
            ) : null}
          </Row>
        ))}
      </Group>

      {isSubscriptionBinding && (
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

      {!soon && client.providerId && isDesktopClient && (
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

      {!soon && client.providerId && !isDesktopClient && (
        <Group title={t('clients.modelTitle')} footer={client.enabled ? t('clients.modelFooterEnabled') : t('clients.modelFooter')}>
          <Row label={t('clients.defaultModel')}>
            <Select
              value={model ?? ''}
              onChange={(v) => setModel(v || null)}
              placeholder={t('clients.modelPlaceholder')}
              options={[{ value: '', label: t('clients.modelUnset') }, ...(models.data ?? []).map((m) => ({ value: m, label: m }))]}
              className="max-w-[300px]"
            />
            {client.enabled && (model ?? null) !== (client.selectedModel ?? null) && (
              <Button size="sm" variant="primary" onClick={() => setEnableOpen(true)}>
                {t('common.apply')}
              </Button>
            )}
          </Row>
        </Group>
      )}

      {!soon && (
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
          {client.enabled && (
            <Row
              icon={<KeyRound className="size-4 text-[var(--text-secondary)]" />}
              label={t('clients.rotateKey')}
              detail={t('clients.rotateKeyDetail')}
              onClick={async () => {
                if (!(await confirm({ title: t('clients.rotateKey'), detail: t('clients.rotateKeyDetail') }))) return;
                rotateKey.mutate(client.kind, {
                  onSuccess: () => toast(t('clients.rotated'), 'success'),
                  onError: (e) => toast(errorText(e), 'error'),
                });
              }}
            />
          )}
        </Group>
      )}

      <EnableSheet open={enableOpen} onOpenChange={setEnableOpen} client={client} model={model} extras={extras} />
      <BackupsSheet open={backupsOpen} onOpenChange={setBackupsOpen} client={client} />
    </>
  );
}

/** Preview of the config edits (unified diff) before enabling — nothing is written until confirmed. */
function EnableSheet({
  open,
  onOpenChange,
  client,
  model,
  extras,
}: {
  open: boolean;
  onOpenChange: (o: boolean) => void;
  client: ClientInfo;
  model: string | null;
  /** Replaces the client's stored extras (Claude Desktop role map); omitted = keep them. */
  extras?: Record<string, unknown>;
}) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const preview = usePreviewEnable();
  const enable = useEnableClient();
  const name = CLIENT_META[client.kind].name;
  const req = extras ? { providerId: client.providerId, model, extras } : { providerId: client.providerId, model };

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
