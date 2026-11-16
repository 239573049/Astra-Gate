import { ArrowDown, ArrowUp, Copy, ExternalLink, GripVertical, Plus, RefreshCw, Trash2, Zap } from 'lucide-react';
import { useEffect, useState, type DragEvent } from 'react';

import {
  useAddSearchApi,
  useAddSearchKey,
  useDeleteSearchApi,
  useDeleteSearchKey,
  useReorderSearchApis,
  useReorderSearchKeys,
  useSearchQuota,
  useTestSearchApi,
  useTools,
  useToolsSettings,
  useUpdateSearchApi,
  useUpdateSearchKey,
  useUpdateToolsSettings,
} from '../api/hooks';
import type { SearchApi, SearchKeyQuota, ToolsSettingsUpdate } from '../api/types';
import { useI18n, type MessageKey } from '../i18n';
import { formatDateTime } from '../lib/format';
import { systemActions } from '../shell/systemActions';
import { Badge, Button, Group, Input, NumberInput, Row, Segmented, Spinner, Switch } from './ui/controls';
import { errorText, Menu, useFeedback } from './ui/overlays';

/** The tools page (/tools): search APIs, search for models that cannot search, web fetch, the image model, the tool list. */
export function ToolsSettings() {
  return (
    <>
      <OnBehalf />
      <SearchApis />
      <Fetch />
      <Image />
      <ToolList />
    </>
  );
}

function useSaveTools() {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const update = useUpdateToolsSettings();
  const save = (body: ToolsSettingsUpdate) =>
    update.mutate(body, { onSuccess: () => toast(t('common.saved'), 'success'), onError: (e) => toast(errorText(e), 'error') });
  return { save, pending: update.isPending };
}

// ---------------------------------------------------------------- search on behalf of models

function OnBehalf() {
  const { t } = useI18n();
  const settings = useToolsSettings();
  const { save, pending } = useSaveTools();
  if (!settings.data) return <Spinner />;
  const s = settings.data.search;
  return (
    <Group title={t('tools.onBehalf.title')} footer={t('tools.onBehalf.footer')}>
      <Row label={t('tools.onBehalf.enable')} detail={t('tools.onBehalf.enableHint')}>
        <Switch checked={s.onBehalf} disabled={pending} label={t('tools.onBehalf.enable')} onChange={(onBehalf) => save({ search: { onBehalf } })} />
      </Row>
      <Row label={t('tools.onBehalf.mode')} detail={t('tools.onBehalf.modeHint')}>
        <Segmented
          ariaLabel={t('tools.onBehalf.mode')}
          value={s.mode}
          items={[
            { value: 'auto', label: t('tools.onBehalf.mode.auto') },
            { value: 'always', label: t('tools.onBehalf.mode.always') },
          ]}
          onChange={(mode) => save({ search: { mode } })}
        />
      </Row>
      <Row label={t('tools.onBehalf.count')} detail={t('tools.onBehalf.countHint')}>
        <NumberInput className="w-20" label={t('tools.onBehalf.count')} min={1} value={s.resultCount} onChange={(v) => v !== null && save({ search: { resultCount: v } })} />
      </Row>
    </Group>
  );
}

// ---------------------------------------------------------------- search APIs

/** A list reordered by dragging a row onto another: the new order of ids, or null when nothing moved. */
export function reordered(ids: string[], from: string, to: string): string[] | null {
  if (from === to) return null;
  const without = ids.filter((id) => id !== from);
  const at = without.indexOf(to);
  if (at < 0 || !ids.includes(from)) return null;
  const adjusted = ids.indexOf(from) < ids.indexOf(to) ? at + 1 : at;
  return [...without.slice(0, adjusted), from, ...without.slice(adjusted)];
}

/** One step up (-1) or down (1). */
export function moved(ids: string[], id: string, by: -1 | 1): string[] | null {
  const i = ids.indexOf(id);
  const j = i + by;
  if (i < 0 || j < 0 || j >= ids.length) return null;
  const next = [...ids];
  [next[i], next[j]] = [next[j]!, next[i]!];
  return next;
}

function SearchApis() {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const settings = useToolsSettings();
  const add = useAddSearchApi();
  const reorder = useReorderSearchApis();
  const [dragging, setDragging] = useState<string | null>(null);
  const [asking, setAsking] = useState<string | null>(null);
  const [address, setAddress] = useState('');
  if (!settings.data) return <Spinner />;
  const { apis, available } = settings.data.search;
  const ids = apis.map((a) => a.vendor);
  const fail = (e: unknown) => toast(errorText(e), 'error');
  const apply = (order: string[] | null) => order && reorder.mutate(order, { onError: fail });
  const needsAddress = available.find((v) => v.vendor === asking)?.needsUrl === true;
  const addVendor = (vendor: string, url?: string) =>
    add.mutate({ vendor, ...(url ? { url } : {}) }, { onSuccess: () => { setAsking(null); setAddress(''); }, onError: fail });

  return (
    <section className="mb-5">
      <div className="mb-2 flex items-center justify-between px-1">
        <h3 className="text-[12px] font-medium text-[var(--text-secondary)]">{t('tools.search.title')}</h3>
        <Menu
          label={t('tools.search.add')}
          icon={<Plus className="size-3.5" />}
          items={available.map((v) => ({
            id: v.vendor,
            label: v.plugin ? `${v.name} (${t('tools.search.fromPlugin')})` : v.name,
            onSelect: () => (v.needsUrl ? setAsking(v.vendor) : addVendor(v.vendor)),
          }))}
        />
      </div>
      {needsAddress && (
        <div className="card mb-2 flex items-center gap-2 px-4 py-3">
          <Input label={t('tools.search.url')} placeholder="https://searx.example.com" value={address} onChange={(e) => setAddress(e.target.value)} />
          <Button size="sm" variant="primary" disabled={address.trim().length === 0} onClick={() => addVendor(asking!, address.trim())}>{t('tools.search.add')}</Button>
          <Button size="sm" variant="plain" onClick={() => setAsking(null)}>{t('common.cancel')}</Button>
        </div>
      )}
      <p className="mb-2 px-1 text-[11px] text-[var(--text-muted)]">{t('tools.search.hint')}</p>
      {apis.length === 0 ? (
        <div className="card px-4 py-6 text-center text-[13px] text-[var(--text-muted)]">{t('tools.search.empty')}</div>
      ) : (
        <div className="card divide-y divide-[var(--border)]">
          {apis.map((api) => (
            <div
              key={api.vendor}
              draggable
              onDragStart={(e: DragEvent) => { setDragging(api.vendor); e.dataTransfer.effectAllowed = 'move'; }}
              onDragOver={(e: DragEvent) => e.preventDefault()}
              onDrop={(e: DragEvent) => { e.preventDefault(); if (dragging) apply(reordered(ids, dragging, api.vendor)); setDragging(null); }}
              onDragEnd={() => setDragging(null)}
            >
              <ApiBlock api={api} first={api.vendor === ids[0]} last={api.vendor === ids[ids.length - 1]} onMove={(by) => apply(moved(ids, api.vendor, by))} />
            </div>
          ))}
        </div>
      )}
    </section>
  );
}

function ApiBlock({ api, first, last, onMove }: { api: SearchApi; first: boolean; last: boolean; onMove: (by: -1 | 1) => void }) {
  const { t, locale } = useI18n();
  const { toast, confirm } = useFeedback();
  const update = useUpdateSearchApi();
  const remove = useDeleteSearchApi();
  const addKey = useAddSearchKey();
  const test = useTestSearchApi();
  const [key, setKey] = useState('');
  const [url, setUrl] = useState(api.url ?? '');
  const quota = useSearchQuota(api.vendor, api.canReadQuota && api.keys.length > 0);
  useEffect(() => setUrl(api.url ?? ''), [api.url]);
  const fail = (e: unknown) => toast(errorText(e), 'error');

  const runTest = () =>
    test.mutate(api.vendor, {
      onSuccess: (r) =>
        r.ok
          ? toast(t('tools.search.testOk', { source: r.source ?? api.name, pages: r.pages, ms: r.elapsedMs }), 'success')
          : toast(t('tools.search.testFailed', { message: r.error ?? '' }), 'error'),
      onError: fail,
    });
  const del = async () => {
    if (!(await confirm({ title: t('tools.search.removeConfirm', { name: api.name }), detail: t('tools.search.removeDetail'), confirmLabel: t('common.delete'), destructive: true }))) return;
    remove.mutate(api.vendor, { onError: fail });
  };

  return (
    <div className="px-4 py-3">
      <div className="flex items-center gap-2">
        <GripVertical className="size-4 shrink-0 cursor-grab text-[var(--text-muted)]" aria-hidden />
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2 text-[13px] font-medium">
            <span className="truncate">{api.name}</span>
            {api.plugin && <Badge>{t('tools.search.fromPlugin')}</Badge>}
            {!api.available && <Badge tone="orange">{t('tools.search.pluginOff')}</Badge>}
          </div>
          <div className="text-[11px] text-[var(--text-muted)]">
            {api.keyOptional ? t('tools.search.keyOptional') : t('tools.search.keysCount', { count: api.keys.length })}
            {api.keysUrl && (
              <a className="ml-2 inline-flex items-center gap-0.5 underline" href={api.keysUrl} target="_blank" rel="noreferrer">
                {t('tools.search.getKey')}
                <ExternalLink className="size-3" />
              </a>
            )}
          </div>
        </div>
        <Button size="sm" variant="plain" icon={<ArrowUp className="size-3.5" />} aria-label={t('tools.search.moveUp')} disabled={first} onClick={() => onMove(-1)} />
        <Button size="sm" variant="plain" icon={<ArrowDown className="size-3.5" />} aria-label={t('tools.search.moveDown')} disabled={last} onClick={() => onMove(1)} />
        <Button size="sm" icon={<Zap className="size-3.5" />} loading={test.isPending} onClick={runTest}>{t('tools.search.test')}</Button>
        <Switch checked={api.enabled} label={`${api.name}: ${t('tools.search.enabled')}`} onChange={(enabled) => update.mutate({ vendor: api.vendor, body: { enabled } }, { onError: fail })} />
        <Button size="sm" variant="plain" icon={<Trash2 className="size-3.5" />} aria-label={t('common.delete')} onClick={() => void del()} />
      </div>
      <div className="mt-2 grid gap-2 pl-6">
        {(api.needsUrl || api.vendor === 'firecrawl' || api.vendor === 'you') && (
          <div className="flex items-center gap-2">
            <Input label={t('tools.search.url')} placeholder={api.needsUrl ? 'https://searx.example.com' : t('tools.search.urlDefault')} value={url} onChange={(e) => setUrl(e.target.value)} />
            <Button size="sm" disabled={url.trim() === (api.url ?? '')} onClick={() => update.mutate({ vendor: api.vendor, body: { url: url.trim() } }, { onError: fail })}>{t('common.save')}</Button>
          </div>
        )}
        {api.keys.length > 1 && (
          <Row label={t('tools.search.pick')} detail={t('tools.search.pickHint')}>
            <Segmented
              ariaLabel={t('tools.search.pick')}
              value={api.pick}
              items={[
                { value: 'turns', label: t('tools.search.pick.turns') },
                { value: 'order', label: t('tools.search.pick.order') },
              ]}
              onChange={(pick) => update.mutate({ vendor: api.vendor, body: { pick } }, { onError: fail })}
            />
          </Row>
        )}
        <KeyList api={api} quota={quota.data} locale={locale} />
        <div className="flex items-center gap-2">
          <Input label={t('tools.search.key')} mono type="password" autoComplete="off" placeholder={t('tools.search.keyPlaceholder')} value={key} onChange={(e) => setKey(e.target.value)} />
          <Button
            size="sm"
            variant="primary"
            icon={<Plus className="size-3.5" />}
            disabled={key.trim().length === 0}
            loading={addKey.isPending}
            onClick={() => addKey.mutate({ vendor: api.vendor, key: key.trim() }, { onSuccess: () => setKey(''), onError: fail })}
          >
            {t('tools.search.addKey')}
          </Button>
          {api.canReadQuota && api.keys.length > 0 && (
            <Button size="sm" variant="plain" icon={<RefreshCw className="size-3.5" />} aria-label={t('tools.search.refreshQuota')} onClick={() => void quota.refetch()} />
          )}
        </div>
      </div>
    </div>
  );
}

function quotaText(q: SearchKeyQuota, t: ReturnType<typeof useI18n>['t']): string {
  if (!q.known) return t('tools.search.quotaUnknown', { message: q.error ?? '' });
  if (q.unlimited) return t('tools.search.quotaUnlimited', { used: q.used });
  const of = q.of === 'plan' ? t('tools.search.quotaOfPlan', { plan: q.plan ?? '' }) : t('tools.search.quotaOfKey');
  return `${t('tools.search.quotaUsed', { used: q.used, limit: q.limit })} ${of}`.trim();
}

function KeyList({ api, quota, locale }: { api: SearchApi; quota?: Record<string, SearchKeyQuota>; locale: string }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const update = useUpdateSearchKey();
  const remove = useDeleteSearchKey();
  const reorder = useReorderSearchKeys();
  const [dragging, setDragging] = useState<string | null>(null);
  const ids = api.keys.map((k) => k.id);
  const fail = (e: unknown) => toast(errorText(e), 'error');
  const apply = (order: string[] | null) => order && reorder.mutate({ vendor: api.vendor, order }, { onError: fail });
  if (api.keys.length === 0) return null;
  return (
    <ul className="grid gap-1">
      {api.keys.map((k) => (
        <li
          key={k.id}
          draggable
          onDragStart={(e: DragEvent) => { setDragging(k.id); e.stopPropagation(); }}
          onDragOver={(e: DragEvent) => e.preventDefault()}
          onDrop={(e: DragEvent) => { e.preventDefault(); e.stopPropagation(); if (dragging) apply(reordered(ids, dragging, k.id)); setDragging(null); }}
          onDragEnd={() => setDragging(null)}
          className="flex items-center gap-2 rounded px-1 py-0.5 text-[12px]"
        >
          <GripVertical className="size-3.5 shrink-0 cursor-grab text-[var(--text-muted)]" aria-hidden />
          <code className="font-mono">{k.masked}</code>
          {k.resting && (
            <Badge tone="orange" title={k.restUntil ? formatDateTime(k.restUntil, locale) : undefined}>{t(`tools.search.rest.${k.resting.replace(/ /g, '_')}` as MessageKey)}</Badge>
          )}
          {quota?.[k.id] && <span className="truncate text-[var(--text-muted)]">{quotaText(quota[k.id]!, t)}</span>}
          <span className="flex-1" />
          <Button size="sm" variant="plain" icon={<ArrowUp className="size-3" />} aria-label={t('tools.search.moveUp')} disabled={k.id === ids[0]} onClick={() => apply(moved(ids, k.id, -1))} />
          <Button size="sm" variant="plain" icon={<ArrowDown className="size-3" />} aria-label={t('tools.search.moveDown')} disabled={k.id === ids[ids.length - 1]} onClick={() => apply(moved(ids, k.id, 1))} />
          <Switch checked={k.enabled} label={`${k.masked}: ${t('tools.search.enabled')}`} onChange={(enabled) => update.mutate({ vendor: api.vendor, id: k.id, enabled }, { onError: fail })} />
          <Button size="sm" variant="plain" icon={<Trash2 className="size-3" />} aria-label={t('common.delete')} onClick={() => remove.mutate({ vendor: api.vendor, id: k.id }, { onError: fail })} />
        </li>
      ))}
    </ul>
  );
}

// ---------------------------------------------------------------- fetch and images

function Fetch() {
  const { t } = useI18n();
  const settings = useToolsSettings();
  const { save, pending } = useSaveTools();
  if (!settings.data) return <Spinner />;
  const f = settings.data.fetch;
  return (
    <Group title={t('tools.fetch.title')} footer={t('tools.fetch.footer')}>
      <Row label={t('tools.fetch.chars')} detail={t('tools.fetch.charsHint')}>
        <NumberInput className="w-28" label={t('tools.fetch.chars')} min={1000} step={1000} value={f.maxChars} onChange={(v) => v !== null && save({ fetch: { maxChars: v } })} />
      </Row>
      <Row label={t('tools.fetch.timeout')} detail={t('tools.fetch.timeoutHint')}>
        <NumberInput className="w-24" label={t('tools.fetch.timeout')} min={3} value={f.timeoutSeconds} onChange={(v) => v !== null && save({ fetch: { timeoutSeconds: v } })} />
      </Row>
      <Row label={t('tools.fetch.bytes')} detail={t('tools.fetch.bytesHint')}>
        <NumberInput className="w-28" label={t('tools.fetch.bytes')} min={1} value={Math.round(f.maxBytes / 1024 / 1024)} onChange={(v) => v !== null && save({ fetch: { maxBytes: v * 1024 * 1024 } })} />
      </Row>
      <Row label={t('tools.fetch.private')} detail={t('tools.fetch.privateHint')}>
        <Switch checked={f.allowPrivate} disabled={pending} label={t('tools.fetch.private')} onChange={(allowPrivate) => save({ fetch: { allowPrivate } })} />
      </Row>
    </Group>
  );
}

function Image() {
  const { t } = useI18n();
  const settings = useToolsSettings();
  const { save, pending } = useSaveTools();
  const [model, setModel] = useState('');
  const [size, setSize] = useState('');
  const saved = settings.data?.image;
  useEffect(() => {
    setModel(saved?.model ?? '');
    setSize(saved?.size ?? '');
  }, [saved?.model, saved?.size]);
  if (!settings.data) return <Spinner />;
  return (
    <Group title={t('tools.image.title')} footer={t('tools.image.footer')}>
      <Row label={t('tools.image.model')} detail={t('tools.image.modelHint')}>
        <Input className="w-56" label={t('tools.image.model')} mono placeholder="gpt-image-1" value={model} onChange={(e) => setModel(e.target.value)} />
      </Row>
      <Row label={t('tools.image.size')} detail={t('tools.image.sizeHint')}>
        <Input className="w-40" label={t('tools.image.size')} mono placeholder="1024x1024" value={size} onChange={(e) => setSize(e.target.value)} />
      </Row>
      <div className="px-4 py-3">
        <Button size="sm" disabled={pending || (model === saved?.model && size === saved?.size)} onClick={() => save({ image: { model, size } })}>{t('common.save')}</Button>
      </div>
    </Group>
  );
}

// ---------------------------------------------------------------- the tool list

function ToolList() {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const tools = useTools();
  const settings = useToolsSettings();
  return (
    <Group title={t('tools.list.title')} footer={t('tools.list.footer')}>
      {settings.data && (
        <Row label={t('tools.list.endpoint')} detail={t('tools.list.endpointHint')}>
          <code className="max-w-[22rem] truncate font-mono text-[12px]" title={settings.data.mcpEndpoint}>{settings.data.mcpEndpoint}</code>
          <Button
            size="sm"
            variant="plain"
            icon={<Copy className="size-3.5" />}
            aria-label={t('mcp.gateway.copyEndpoint')}
            onClick={() => void systemActions.copy(settings.data!.mcpEndpoint).then((ok) => ok && toast(t('mcp.gateway.copied'), 'success'))}
          />
        </Row>
      )}
      {tools.data === undefined ? (
        <div className="px-4 py-3"><Spinner /></div>
      ) : (
        tools.data.map((tool) => (
          <Row
            key={tool.name}
            label={
              <span className="flex min-w-0 items-center gap-2">
                <span className="truncate font-mono">{tool.name}</span>
                <Badge>{tool.source === 'plugin' ? t('tools.list.plugin') : t('tools.list.builtin')}</Badge>
                {!tool.available && <Badge tone="orange">{t('tools.list.unavailable')}</Badge>}
              </span>
            }
            detail={tool.available ? tool.description : tool.name === 'web_search' ? t('tools.reason.web_search') : tool.name === 'generate_image' ? t('tools.reason.generate_image') : tool.reason}
          />
        ))
      )}
    </Group>
  );
}
