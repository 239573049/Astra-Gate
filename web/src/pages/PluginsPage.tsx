import { AppWindow, ArrowDown, ArrowUp, Check, Download, Ellipsis, ExternalLink, Plus, Puzzle, RefreshCw, Settings2, ShieldCheck, Trash2 } from 'lucide-react';
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router';

import {
  useAddPlugin,
  useImportMagpie,
  useInstallBun,
  useMagpieImportPreview,
  usePatchPlugin,
  usePluginHost,
  usePluginJob,
  usePluginMarket,
  usePlugins,
  usePluginSearch,
  usePreferPlugin,
  useRemovePlugin,
  useReorderPlugins,
  useUpdatePlugin,
} from '../api/hooks';
import type { MarketListing, Plugin, PluginHostStatus, PluginJob, PluginKind } from '../api/types';
import { Alert } from '../components/arc/alert/alert';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '../components/arc/tabs/tabs';
import { ProviderIcon } from '../components/icons';
import { Page } from '../components/layout/Page';
import { Badge, Button, CodeBlock, EmptyState, Field, Input, KV, SearchField, Segmented, Spinner, Switch, TextArea } from '../components/ui/controls';
import { errorText, Menu, Sheet, useFeedback } from '../components/ui/overlays';
import { useI18n, type MessageKey } from '../i18n';
import { cn } from '../lib/cn';
import { formatDateTime } from '../lib/format';

type Tab = 'installed' | 'market' | 'runtime';
type KindFilter = 'all' | 'provider' | 'middleware' | 'agent';

/** What a website link may ask this page to install: an npm package name (scoped or not) or github:owner/repo. */
const LINK_SPEC = /^(?:(?:@[a-z0-9][a-z0-9._~-]*\/)?[a-z0-9][a-z0-9._~-]*|github:[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+)$/;

const KIND_LABEL: Record<PluginKind, MessageKey> = {
  provider: 'plugins.kind.provider',
  middleware: 'plugins.kind.middleware',
  agent: 'plugins.kind.agent',
  pi: 'plugins.kind.pi',
};

const KIND_TONE: Record<PluginKind, 'neutral' | 'accent' | 'green' | 'orange'> = {
  provider: 'neutral',
  middleware: 'accent',
  agent: 'green',
  pi: 'orange',
};

/** A plugin's picture: the one it gives (an https: / data:image URL), a brand Astra knows, else its kind's glyph. */
function PluginIcon({ name, icon, kind, size = 32 }: { name: string; icon?: string | null; kind: PluginKind; size?: number }) {
  const radius = Math.round(size * 0.28);
  if (icon && /^(https:|data:image\/)/i.test(icon))
    return <img src={icon} alt="" width={size} height={size} className="shrink-0 object-contain" style={{ borderRadius: radius }} draggable={false} />;
  if (kind === 'middleware' || kind === 'agent') {
    const Glyph = kind === 'middleware' ? ShieldCheck : AppWindow;
    return (
      <span
        aria-hidden
        className="inline-flex shrink-0 items-center justify-center text-white"
        style={{ width: size, height: size, borderRadius: radius, background: kind === 'middleware' ? '#0a84ff' : '#30b0a0' }}
      >
        <Glyph className="size-[55%]" strokeWidth={1.75} />
      </span>
    );
  }
  return <ProviderIcon name={name} icon={icon} size={size} />;
}

/**
 * Plugins (magpie-compatible; .plan/plugins.md): what is installed — provider plugins run in the Bun plugin host,
 * gateway middleware and agents in Astra's own process — the market (magpie-community's registry and searches),
 * and the runtime (Bun and the plugin host).
 */
export function PluginsPage() {
  const { t } = useI18n();
  const navigate = useNavigate();
  const { tab: tabParam } = useParams();
  const tab: Tab = tabParam === 'market' || tabParam === 'runtime' ? tabParam : 'installed';
  const plugins = usePlugins();
  const update = useUpdatePlugin();
  const { toast } = useFeedback();
  const [adding, setAdding] = useState(false);
  const [importing, setImporting] = useState(false);
  const [job, setJob] = useState<string | null>(null);

  const updateAll = () =>
    update.mutate(undefined, { onSuccess: (j) => setJob(j.id), onError: (e) => toast(errorText(e), 'error') });

  return (
    <Page
      title={t('nav.plugins')}
      subtitle={t('plugins.subtitle')}
      actions={
        <>
          <Button icon={<RefreshCw className="size-3.5" />} onClick={updateAll} disabled={!plugins.data?.plugins.length}>
            {t('plugins.updateAll')}
          </Button>
          <Button variant="primary" icon={<Plus className="size-3.5" />} onClick={() => setAdding(true)}>
            {t('plugins.add')}
          </Button>
        </>
      }
    >
      <div className="mx-auto flex max-w-[1100px] flex-col">
        {plugins.data?.off && <Alert className="mb-4" tone="warning" title={t('plugins.off')}>{t('plugins.offDetail')}</Alert>}
        <Tabs value={tab} onValueChange={(v) => navigate(v === 'installed' ? '/plugins' : `/plugins/${v}`, { replace: true })}>
          <TabsList aria-label={t('nav.plugins')}>
            <TabsTrigger value="installed">
              {t('plugins.tab.installed')}
              {plugins.data && plugins.data.plugins.length > 0 && <span className="ml-1.5 tabular-nums opacity-60">{plugins.data.plugins.length}</span>}
            </TabsTrigger>
            <TabsTrigger value="market">{t('plugins.tab.market')}</TabsTrigger>
            <TabsTrigger value="runtime">{t('plugins.tab.runtime')}</TabsTrigger>
          </TabsList>
          <TabsContent value="installed" className="pt-1">
            {plugins.isLoading && <Spinner lines={4} />}
            {plugins.isError && <Alert tone="danger" title={t('common.error')}>{errorText(plugins.error)}</Alert>}
            {plugins.data && (
              <InstalledList
                plugins={plugins.data.plugins}
                preferences={plugins.data.preferences}
                onJob={setJob}
                onBrowse={() => navigate('/plugins/market')}
                onImport={() => setImporting(true)}
              />
            )}
          </TabsContent>
          <TabsContent value="market" className="pt-1">
            <MarketTab installed={plugins.data?.plugins ?? []} onJob={setJob} />
          </TabsContent>
          <TabsContent value="runtime" className="pt-1">
            <RuntimeTab onJob={setJob} />
          </TabsContent>
        </Tabs>
      </div>
      <AddPluginSheet open={adding} onOpenChange={setAdding} onJob={setJob} />
      <ImportMagpieSheet open={importing} onOpenChange={setImporting} onJob={setJob} />
      <JobSheet id={job} onClose={() => setJob(null)} />
    </Page>
  );
}

function InstalledList({
  plugins,
  preferences,
  onJob,
  onBrowse,
  onImport,
}: {
  plugins: Plugin[];
  preferences: Record<string, string>;
  onJob: (id: string) => void;
  onBrowse: () => void;
  onImport: () => void;
}) {
  const { t } = useI18n();
  const host = usePluginHost(plugins.some((p) => p.kinds.includes('provider') || p.kinds.includes('pi')));
  const magpie = useMagpieImportPreview();
  const reorder = useReorderPlugins();
  const { toast } = useFeedback();
  const canImport = Boolean(magpie.data?.found && magpie.data.plugins.some((p) => !p.installed));

  const importBanner = canImport && (
    <div className="card flex flex-wrap items-center gap-3 p-4">
      <span className="min-w-0 flex-1 text-[13px]">{t('plugins.magpie.banner', { count: magpie.data!.plugins.filter((p) => !p.installed).length })}</span>
      <Button size="sm" icon={<Download className="size-3.5" />} onClick={onImport}>
        {t('plugins.magpie.import')}
      </Button>
    </div>
  );

  if (plugins.length === 0)
    return (
      <div className="flex flex-col gap-4">
        {importBanner}
        <div className="card p-6">
          <EmptyState
            icon={<Puzzle className="size-6" />}
            title={t('plugins.empty')}
            detail={t('plugins.emptyDetail')}
            action={
              <Button variant="primary" onClick={onBrowse}>
                {t('plugins.browseMarket')}
              </Button>
            }
          />
        </div>
      </div>
    );

  const move = (index: number, by: number) => {
    const specs = plugins.map((p) => p.spec);
    const [item] = specs.splice(index, 1);
    specs.splice(index + by, 0, item!);
    reorder.mutate(specs, { onError: (e) => toast(errorText(e), 'error') });
  };
  return (
    <div className="flex flex-col gap-4">
      {importBanner}
      <div className="card divide-y divide-[var(--border-subtle)] p-0">
        {plugins.map((p, i) => (
          <PluginRow
            key={p.spec}
            plugin={p}
            host={host.data}
            preferences={preferences}
            onJob={onJob}
            onUp={i > 0 ? () => move(i, -1) : undefined}
            onDown={i < plugins.length - 1 ? () => move(i, 1) : undefined}
          />
        ))}
      </div>
      {plugins.some((p) => p.middleware) && <p className="px-1 text-[12px] text-[var(--text-muted)]">{t('plugins.orderHint')}</p>}
    </div>
  );
}

function PluginRow({
  plugin: p,
  host,
  preferences,
  onJob,
  onUp,
  onDown,
}: {
  plugin: Plugin;
  host?: PluginHostStatus;
  preferences: Record<string, string>;
  onJob: (id: string) => void;
  onUp?: () => void;
  onDown?: () => void;
}) {
  const { t } = useI18n();
  const navigate = useNavigate();
  const { toast, confirm } = useFeedback();
  const patch = usePatchPlugin();
  const update = useUpdatePlugin();
  const remove = useRemovePlugin();
  const prefer = usePreferPlugin();
  const [editing, setEditing] = useState(false);
  const loaded = host?.plugins.find((x) => x.spec === p.spec);
  const mw = p.middleware;
  const kind: PluginKind = p.kinds[0] ?? 'provider';

  const doRemove = async () => {
    if (!(await confirm({ title: t('plugins.removeConfirm', { name: p.shortName }), detail: t('plugins.removeDetail'), confirmLabel: t('common.delete'), destructive: true })))
      return;
    remove.mutate(p.spec, { onSuccess: (j) => onJob(j.id), onError: (e) => toast(errorText(e), 'error') });
  };

  const details: ReactNode[] = [];
  if (mw && !mw.error)
    details.push(
      <Detail key="mw" label={t('plugins.kind.middleware')}>
        {t('plugins.mw.stats', { hooks: mw.hooks.join(', '), calls: mw.calls, micros: mw.avgMicros.toFixed(1), failures: mw.failures })}
        {mw.lastError && <span className="block text-[var(--danger)]">{t('plugins.mw.lastError', { error: mw.lastError })}</span>}
      </Detail>,
    );
  if (p.agent?.kind)
    details.push(
      <Detail key="agent" label={t('plugins.kind.agent')}>
        <button type="button" className="text-[var(--accent)] underline-offset-2 hover:underline" onClick={() => navigate(`/clients/${p.agent!.kind}`)}>
          {t('plugins.agent.adds', { name: p.agent.name ?? p.agent.id ?? '' })}
        </button>
      </Detail>,
    );
  if (loaded && loaded.provides.length > 0)
    details.push(
      <Detail key="provides" label={t('plugins.kind.provider')}>
        <span>{t('plugins.provides', { providers: loaded.provides.join(', ') })}</span>
        <button type="button" className="ml-2 text-[var(--accent)] underline-offset-2 hover:underline" onClick={() => navigate('/providers')}>
          {t('plugins.openProviders')}
        </button>
        {Object.entries(loaded.servedBy).map(([id, other]) => (
          <span key={id} className="mt-1 flex flex-wrap items-center gap-2 text-[var(--warning)]">
            {t('plugins.servedBy', { provider: id, other })}
            <Button size="sm" onClick={() => prefer.mutate({ spec: p.spec, provider: id }, { onError: (e) => toast(errorText(e), 'error') })}>
              {t('plugins.useFor', { provider: id })}
            </Button>
          </span>
        ))}
        {loaded.provides.some((id) => preferences[id] === p.spec) && <span className="block text-[var(--text-muted)]">{t('plugins.picked')}</span>}
      </Detail>,
    );

  return (
    <article className={cn('flex gap-3 px-4 py-3.5', !p.enabled && 'opacity-70')}>
      <div className={cn('pt-0.5', !p.enabled && 'grayscale')}>
        <PluginIcon name={p.shortName} icon={p.icon} kind={kind} />
      </div>
      <div className="flex min-w-0 flex-1 flex-col gap-2">
        <div className="flex flex-wrap items-start gap-x-3 gap-y-1">
          <div className="flex min-w-0 flex-1 basis-56 flex-col">
            <div className="flex flex-wrap items-center gap-1.5">
              <span className="truncate text-[14px] font-medium">{p.shortName}</span>
              {p.version && <span className="font-mono text-[11px] text-[var(--text-muted)]">v{p.version}</span>}
              {p.kinds.map((k) => (
                <Badge key={k} tone={KIND_TONE[k]}>
                  {t(KIND_LABEL[k])}
                </Badge>
              ))}
              {p.isPath && <Badge tone="orange">{t('plugins.local')}</Badge>}
            </div>
            <span className="truncate font-mono text-[11px] text-[var(--text-muted)]" title={p.spec}>
              {p.spec}
            </span>
          </div>
          <div className="flex shrink-0 items-center gap-1">
            {mw && (
              <>
                <Button size="sm" variant="plain" aria-label={t('plugins.moveUp')} title={t('plugins.moveUp')} disabled={!onUp} onClick={onUp} icon={<ArrowUp className="size-3.5" />} />
                <Button size="sm" variant="plain" aria-label={t('plugins.moveDown')} title={t('plugins.moveDown')} disabled={!onDown} onClick={onDown} icon={<ArrowDown className="size-3.5" />} />
              </>
            )}
            <Switch
              checked={p.enabled}
              label={t('plugins.enabled')}
              onChange={(enabled) => patch.mutate({ spec: p.spec, enabled }, { onError: (e) => toast(errorText(e), 'error') })}
            />
            <Menu
              label={t('common.more')}
              iconOnly
              icon={<Ellipsis className="size-4" />}
              items={[
                { id: 'options', label: t('plugins.options'), icon: <Settings2 className="size-3.5" />, onSelect: () => setEditing(true) },
                ...(p.isPath
                  ? []
                  : [{ id: 'update', label: t('plugins.update'), icon: <RefreshCw className="size-3.5" />, onSelect: () => update.mutate(p.spec, { onSuccess: (j) => onJob(j.id), onError: (e) => toast(errorText(e), 'error') }) }]),
                { id: 'remove', label: t('common.delete'), icon: <Trash2 className="size-3.5" />, destructive: true, separatorBefore: true, onSelect: () => void doRemove() },
              ]}
            />
          </div>
        </div>
        {p.error && <Alert tone="danger" title={t('plugins.cantLoad')}>{p.error}</Alert>}
        {loaded?.error && <Alert tone="danger" title={t('plugins.hostCantLoad')}>{loaded.error}</Alert>}
        {mw?.error && <Alert tone="danger" title={t('plugins.cantLoad')}>{t('plugins.mw.error', { error: mw.error })}</Alert>}
        {p.agent?.error && <Alert tone="danger" title={t('plugins.cantLoad')}>{t('plugins.agent.error', { error: p.agent.error })}</Alert>}
        {details.length > 0 && <div className="flex flex-col gap-1.5 rounded-lg bg-[var(--surface-muted)] px-3 py-2">{details}</div>}
      </div>
      <OptionsSheet plugin={p} open={editing} onOpenChange={setEditing} />
    </article>
  );
}

function Detail({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex gap-3 text-[12px]">
      <span className="w-14 shrink-0 text-[var(--text-muted)]">{label}</span>
      <div className="min-w-0 flex-1 text-[var(--text-secondary)]">{children}</div>
    </div>
  );
}

/** A plugin's options as JSON, started from its package's example (magpie.options) when none are set. */
function OptionsSheet({ plugin: p, open, onOpenChange }: { plugin: Plugin; open: boolean; onOpenChange: (open: boolean) => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const patch = usePatchPlugin();
  const [text, setText] = useState('');
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    if (open) {
      setText(JSON.stringify(p.options ?? p.optionsExample ?? {}, null, 2));
      setError(null);
    }
  }, [open, p.options, p.optionsExample]);

  const save = (options: Record<string, unknown> | null) =>
    patch.mutate(
      { spec: p.spec, options },
      { onSuccess: () => (toast(t('plugins.optionsSaved')), onOpenChange(false)), onError: (e) => toast(errorText(e), 'error') },
    );
  const submit = () => {
    try {
      const parsed: unknown = JSON.parse(text || '{}');
      if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) throw new Error(t('plugins.optionsObject'));
      save(parsed as Record<string, unknown>);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  return (
    <Sheet
      open={open}
      onOpenChange={onOpenChange}
      title={t('plugins.optionsTitle', { name: p.shortName })}
      description={p.options ? undefined : p.optionsExample ? t('plugins.optionsExample') : t('plugins.optionsNone')}
      width={560}
      footer={
        <>
          {p.options && <Button onClick={() => save(null)}>{t('plugins.optionsClear')}</Button>}
          <Button variant="primary" onClick={submit} disabled={patch.isPending}>
            {t('common.save')}
          </Button>
        </>
      }
    >
      <TextArea mono rows={14} value={text} invalid={Boolean(error)} onChange={(e) => (setText(e.target.value), setError(null))} label={t('plugins.options')} />
      {error && <p className="mt-2 text-[12px] text-[var(--danger)]">{error}</p>}
    </Sheet>
  );
}

function AddPluginSheet({ open, onOpenChange, onJob }: { open: boolean; onOpenChange: (open: boolean) => void; onJob: (id: string) => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const add = useAddPlugin();
  const [spec, setSpec] = useState('');
  useEffect(() => {
    if (open) setSpec('');
  }, [open]);
  const submit = () =>
    add.mutate(
      { spec: spec.trim() },
      { onSuccess: (j) => (onJob(j.id), onOpenChange(false)), onError: (e) => toast(errorText(e), 'error') },
    );
  return (
    <Sheet
      open={open}
      onOpenChange={onOpenChange}
      title={t('plugins.addTitle')}
      description={t('plugins.addDetail')}
      footer={
        <Button variant="primary" disabled={!spec.trim() || add.isPending} onClick={submit}>
          {t('plugins.install')}
        </Button>
      }
    >
      <Field label={t('plugins.spec')} hint={t('plugins.specHint')}>
        <Input mono value={spec} onChange={(e) => setSpec(e.target.value)} placeholder="@magpie-community/middleware-word-guard" autoFocus />
      </Field>
      <Alert tone="warning" title={t('plugins.trustTitle')} className="mt-4">
        {t('plugins.trustDetail')}
      </Alert>
    </Sheet>
  );
}

/** Brings a magpie install's plugins over — and, only when asked, its sign-ins. */
function ImportMagpieSheet({ open, onOpenChange, onJob }: { open: boolean; onOpenChange: (open: boolean) => void; onJob: (id: string) => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const preview = useMagpieImportPreview(open);
  const run = useImportMagpie();
  const [signIns, setSignIns] = useState(false);
  const p = preview.data;
  return (
    <Sheet
      open={open}
      onOpenChange={onOpenChange}
      title={t('plugins.magpie.title')}
      description={p?.path}
      width={560}
      footer={
        <Button
          variant="primary"
          disabled={!p?.found || run.isPending}
          onClick={() => run.mutate(signIns, { onSuccess: (j) => (onJob(j.id), onOpenChange(false)), onError: (e) => toast(errorText(e), 'error') })}
        >
          {t('plugins.magpie.import')}
        </Button>
      }
    >
      {preview.isLoading && <Spinner lines={3} />}
      {p?.error && <Alert tone="danger" title={t('common.error')}>{p.error}</Alert>}
      {p && (
        <div className="flex flex-col gap-3">
          <div className="card divide-y divide-[var(--border-subtle)] p-0">
            {p.plugins.map((x) => (
              <div key={x.spec} className="flex items-center gap-2 px-3 py-2 text-[12px]">
                <span className="min-w-0 flex-1 truncate font-mono">{x.spec}</span>
                {x.off && <Badge>{t('plugins.magpie.off')}</Badge>}
                {x.hasOptions && <Badge tone="accent">{t('plugins.options')}</Badge>}
                {x.installed && <Badge tone="green">{t('plugins.installed')}</Badge>}
              </div>
            ))}
          </div>
          {p.signIns > 0 && (
            <div className="flex flex-col gap-2">
              <Switch checked={signIns} onChange={setSignIns} label={t('plugins.magpie.signIns', { count: p.signIns })} />
              <p className="text-[12px] text-[var(--text-secondary)]">{t('plugins.magpie.signInsDetail')}</p>
            </div>
          )}
        </div>
      )}
    </Sheet>
  );
}

/** An install, update or removal as it runs: its log, then how it ended. */
function JobSheet({ id, onClose }: { id: string | null; onClose: () => void }) {
  const { t } = useI18n();
  const job = usePluginJob(id);
  const j: PluginJob | undefined = job.data;
  return (
    <Sheet
      open={Boolean(id)}
      onOpenChange={(o) => !o && onClose()}
      title={t('plugins.job.title')}
      description={j ? t(j.state === 'running' ? 'plugins.job.running' : j.state === 'succeeded' ? 'plugins.job.done' : 'plugins.job.failed', { target: j.target }) : undefined}
      width={620}
      footer={<Button onClick={onClose}>{t('common.close')}</Button>}
    >
      {!j && <Spinner lines={3} />}
      {j && <CodeBlock language="text">{j.log.length ? j.log.join('\n') : '…'}</CodeBlock>}
      {j?.state === 'failed' && j.error && <Alert tone="danger" title={t('plugins.job.failedTitle')} className="mt-3">{j.error}</Alert>}
    </Sheet>
  );
}

function MarketTab({ installed, onJob }: { installed: Plugin[]; onJob: (id: string) => void }) {
  const { t, locale } = useI18n();
  const { toast, confirm } = useFeedback();
  const market = usePluginMarket();
  const add = useAddPlugin();
  const [query, setQuery] = useState('');
  const [debounced, setDebounced] = useState('');
  const [kind, setKind] = useState<KindFilter>('all');
  useEffect(() => {
    const timer = setTimeout(() => setDebounced(query.trim()), 400);
    return () => clearTimeout(timer);
  }, [query]);
  const search = usePluginSearch(debounced);
  const names = useMemo(() => new Set(installed.map((p) => p.name)), [installed]);
  const isInstalled = (spec: string, name: string) => names.has(name) || installed.some((p) => p.spec === spec);

  const install = async (spec: string, source: string) => {
    if (!(await confirm({ title: t('plugins.installConfirm', { name: spec }), detail: t('plugins.trustDetail'), confirmLabel: t('plugins.install') }))) return;
    add.mutate({ spec, source }, { onSuccess: (j) => onJob(j.id), onError: (e) => toast(errorText(e), 'error') });
  };

  // A link from the website (…/plugins/market?install=<package>) asks the same question as the Install button;
  // nothing is installed without the answer. Only a package or a GitHub repository can be asked for this way, never a path.
  const [params, setParams] = useSearchParams();
  const linked = params.get('install');
  const asked = useRef<string | null>(null);
  useEffect(() => {
    if (!linked || asked.current === linked) return;
    asked.current = linked;
    setParams({}, { replace: true });
    if (LINK_SPEC.test(linked)) void install(linked, 'market');
    else toast(t('plugins.linkInvalid'), 'error');
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [linked]);

  const listings = (market.data ?? []).filter((l) => kind === 'all' || l.kind === kind);
  const counts = (k: KindFilter) => (market.data ?? []).filter((l) => k === 'all' || l.kind === k).length;
  const searching = debounced.length >= 2;

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-3">
        <SearchField className="min-w-[220px] flex-1" value={query} onChange={setQuery} placeholder={t('plugins.searchPlaceholder')} />
        {!searching && (
          <Segmented<KindFilter>
            ariaLabel={t('plugins.kindFilter')}
            value={kind}
            onChange={setKind}
            items={(['all', 'provider', 'middleware', 'agent'] as KindFilter[]).map((k) => ({
              value: k,
              label: `${k === 'all' ? t('plugins.kind.all') : t(KIND_LABEL[k])} ${counts(k)}`,
            }))}
          />
        )}
      </div>

      {searching ? (
        <>
          {search.isLoading && <Spinner lines={3} />}
          {search.data?.length === 0 && (
            <div className="card p-6">
              <EmptyState icon={<Puzzle className="size-6" />} title={t('plugins.noResults')} />
            </div>
          )}
          {search.data && search.data.length > 0 && (
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
              {search.data.map((r) => (
                <ListingCard
                  key={r.spec}
                  title={r.name}
                  subtitle={r.spec}
                  icon={<PluginIcon name={r.name} kind={r.kind} />}
                  detail={r.description ?? undefined}
                  tags={[t(KIND_LABEL[r.kind]), r.source === 'github' ? `★ ${r.stars}` : r.version ? `v${r.version}` : '', r.source === 'astra' ? t('plugins.origin.astra') : '']}
                  installed={isInstalled(r.spec, r.name)}
                  onInstall={() => void install(r.spec, r.source === 'github' ? 'github' : r.source === 'astra' ? 'market' : 'npm')}
                />
              ))}
            </div>
          )}
          <p className="px-1 text-[12px] text-[var(--text-muted)]">{t('plugins.searchNote')}</p>
        </>
      ) : (
        <>
          {market.isLoading && <Spinner lines={4} />}
          {market.isError && <Alert tone="danger" title={t('common.error')}>{errorText(market.error)}</Alert>}
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
            {listings.map((l: MarketListing) => (
              <ListingCard
                key={l.package}
                title={l.name}
                subtitle={l.package}
                icon={<PluginIcon name={l.name} icon={l.icon} kind={l.kind} />}
                detail={l.summary[locale] ?? l.summary.en ?? l.description ?? undefined}
                tags={[
                  t(KIND_LABEL[l.kind]),
                  l.latest ? `v${l.latest}` : '',
                  l.origin === 'astra' ? t('plugins.origin.astra') : '',
                  l.minAstraVersion ? t('plugins.minVersion', { version: l.minAstraVersion }) : '',
                ].filter(Boolean)}
                installed={isInstalled(l.package, l.package)}
                onInstall={() => void install(l.package, 'market')}
              />
            ))}
          </div>
          <p className="px-1 text-[12px] text-[var(--text-muted)]">{t('plugins.marketNote')}</p>
        </>
      )}
    </div>
  );
}

function ListingCard({
  title,
  subtitle,
  icon,
  detail,
  tags,
  installed,
  onInstall,
}: {
  title: string;
  subtitle: string;
  icon: ReactNode;
  detail?: string;
  tags: string[];
  installed: boolean;
  onInstall: () => void;
}) {
  const { t } = useI18n();
  return (
    <div className="card flex flex-col gap-3 p-4 transition-colors hover:border-[var(--border)]">
      <div className="flex items-start gap-3">
        {icon}
        <div className="flex min-w-0 flex-1 flex-col">
          <span className="truncate text-[14px] font-medium" title={title}>
            {title}
          </span>
          <span className="truncate font-mono text-[11px] text-[var(--text-muted)]" title={subtitle}>
            {subtitle}
          </span>
        </div>
      </div>
      <p className="line-clamp-2 min-h-[2lh] text-[12px] leading-relaxed text-[var(--text-secondary)]">{detail ?? ''}</p>
      <div className="mt-auto flex items-center gap-1.5">
        {tags.filter(Boolean).map((tag) => (
          <Badge key={tag}>{tag}</Badge>
        ))}
        <span className="flex-1" />
        {installed ? (
          <span className="inline-flex items-center gap-1 text-[12px] text-[var(--success)]">
            <Check className="size-3.5" />
            {t('plugins.installed')}
          </span>
        ) : (
          <Button size="sm" variant="primary" onClick={onInstall}>
            {t('plugins.install')}
          </Button>
        )}
      </div>
    </div>
  );
}

function RuntimeTab({ onJob }: { onJob: (id: string) => void }) {
  const { t, locale } = useI18n();
  const { toast } = useFeedback();
  const plugins = usePlugins();
  const host = usePluginHost();
  const installBun = useInstallBun();
  const bun = plugins.data?.runtime;
  return (
    <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
      <section className="card flex flex-col gap-3 p-4">
        <div className="flex items-center gap-2">
          <h3 className="flex-1 text-[14px] font-medium">Bun</h3>
          {bun && <Badge tone={bun.path ? 'green' : 'neutral'}>{bun.path ? t('plugins.bunReady') : t('plugins.bunMissing')}</Badge>}
        </div>
        <p className="text-[12px] leading-relaxed text-[var(--text-secondary)]">{t('plugins.bunDetail')}</p>
        {bun && (
          <div className="flex flex-col gap-1">
            {bun.path && <KV label={t('plugins.bunPath')} mono>{bun.path}</KV>}
            {bun.version && <KV label={t('plugins.bunVersion')} mono>{bun.version}</KV>}
            <KV label={t('plugins.bunTarget')} mono>{bun.target ?? t('plugins.bunNoBuild')}</KV>
          </div>
        )}
        {bun?.error && <Alert tone="danger" title={t('plugins.bunFailed')}>{bun.error}</Alert>}
        {bun && !bun.path && (
          <div>
            <Button
              variant="primary"
              disabled={bun.downloading || installBun.isPending || !bun.target}
              onClick={() => installBun.mutate(undefined, { onSuccess: (j) => onJob(j.id), onError: (e) => toast(errorText(e), 'error') })}
            >
              {bun.downloading ? t('plugins.bunDownloading') : t('plugins.bunDownload', { version: bun.version ?? '' })}
            </Button>
          </div>
        )}
      </section>
      <section className="card flex flex-col gap-3 p-4">
        <div className="flex items-center gap-2">
          <h3 className="flex-1 text-[14px] font-medium">{t('plugins.host')}</h3>
          {host.data && <Badge tone={host.data.running ? 'green' : 'neutral'}>{host.data.running ? t('plugins.hostRunning') : t('plugins.hostStopped')}</Badge>}
        </div>
        <p className="text-[12px] leading-relaxed text-[var(--text-secondary)]">{t('plugins.hostDetail')}</p>
        {host.data?.startError && <Alert tone="danger" title={t('plugins.hostStartFailed')}>{host.data.startError}</Alert>}
        {host.data?.goneReason && <Alert tone="warning" title={t('plugins.hostGone')}>{host.data.goneReason}</Alert>}
        {host.data && host.data.plugins.length > 0 && (
          <div className="flex flex-col gap-1">
            {host.data.plugins.map((p) => (
              <KV key={p.spec} label={p.spec} mono>
                {p.error ? t('plugins.hostPluginError', { error: p.error }) : p.provides.join(', ') || '—'}
              </KV>
            ))}
          </div>
        )}
        {host.data?.recent && host.data.recent.length > 0 && (
          <div className="flex flex-col gap-1.5 border-t border-[var(--border-subtle)] pt-3">
            <h4 className="text-[12px] font-medium text-[var(--text-secondary)]">{t('plugins.hostRecent')}</h4>
            {host.data.recent.map((m, i) => (
              <div key={i} className="flex items-start gap-2 text-[12px]">
                <Badge tone={m.variant === 'error' ? 'red' : m.variant === 'warning' ? 'orange' : m.variant === 'success' ? 'green' : 'neutral'}>
                  {formatDateTime(m.at, locale)}
                </Badge>
                <span className="min-w-0 flex-1 break-words">
                  {m.title && <span className="font-medium">{m.title} </span>}
                  {m.message}
                </span>
              </div>
            ))}
          </div>
        )}
      </section>
      <a
        className="flex items-center gap-1 px-1 text-[12px] text-[var(--text-muted)] hover:text-[var(--accent)] lg:col-span-2"
        href="https://github.com/magpie-community/plugins"
        target="_blank"
        rel="noreferrer"
      >
        <ExternalLink className="size-3" /> magpie-community/plugins
      </a>
    </div>
  );
}
