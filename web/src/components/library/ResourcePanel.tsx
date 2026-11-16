// One kind of the resource library (instructions, sub-agents, commands, hooks, environment variables): the items Astra keeps and where each is written
// (a client's own file or folder, user-wide or inside a project). Astra only ever takes back what it wrote and what is still as it wrote it: a changed
// target shows "changed" and is rewritten only on request (plan resources-library; magpie's library, MIT, is the model).

import { Pencil, Plus, RefreshCw, Trash2 } from 'lucide-react';
import { useEffect, useState } from 'react';

import { useApplyResource, useDeleteResource, useResourceLibrary, useSaveResource } from '../../api/hooks';
import type { ClientKind, ResourceInput, ResourceItem, ResourceLibrary, ResourceSegment, ResourceState, ResourceTarget, ResourceTargetState } from '../../api/types';
import { useI18n, type MessageKey } from '../../i18n';
import { Alert } from '../arc/alert/alert';
import { Checkbox } from '../arc/checkbox/checkbox';
import { clientName } from '../skills/clientNames';
import { Badge, Button, EmptyState, Field, Input, NumberInput, Row, Spinner, Switch, TextArea } from '../ui/controls';
import { errorText, Select, Sheet, useFeedback } from '../ui/overlays';

const KIND_OF: Record<ResourceSegment, ResourceItem['kind']> = { instructions: 'instruction', agents: 'agent', commands: 'command', hooks: 'hook', env: 'env' };
const HOOK_EVENTS = ['turn-end', 'after-tool', 'before-tool', 'prompt', 'session-start'] as const;
const TONE: Record<ResourceState, 'neutral' | 'green' | 'orange' | 'red'> = {
  applied: 'green',
  drifted: 'orange',
  missing: 'orange',
  conflict: 'red',
  'project-missing': 'orange',
  unsupported: 'neutral',
  pending: 'neutral',
  off: 'neutral',
};

const projectName = (path: string) => path.split(/[\\/]/).filter(Boolean).pop() ?? path;

export function ResourcePanel({ segment }: { segment: ResourceSegment }) {
  const { t } = useI18n();
  const { toast, confirm } = useFeedback();
  const kind = KIND_OF[segment];
  const query = useResourceLibrary(segment);
  const apply = useApplyResource(segment);
  const remove = useDeleteResource(segment);
  const [editing, setEditing] = useState<ResourceItem | 'new' | null>(null);
  const data = query.data;

  const attempt = async (run: () => Promise<unknown>, success?: MessageKey) => {
    try {
      await run();
      if (success) toast(t(success), 'success');
    } catch (err) {
      toast(errorText(err), 'error');
    }
  };

  const rewrite = async (item: ResourceItem, force: boolean) => {
    if (force && !(await confirm({ title: t('library.confirm.force.title', { name: item.name }), detail: t('library.confirm.force.detail'), confirmLabel: t('library.rewriteForce'), destructive: true }))) return;
    await attempt(() => apply.mutateAsync({ id: item.id, force }), 'library.toast.applied');
  };

  const del = async (item: ResourceItem) => {
    if (!(await confirm({ title: t('library.confirm.delete.title', { name: item.name }), detail: t('library.confirm.delete.detail'), confirmLabel: t('common.delete'), destructive: true }))) return;
    await attempt(async () => {
      try {
        await remove.mutateAsync({ id: item.id });
      } catch (err) {
        // what the user changed is theirs and stays; removing it too is the user's call
        if (!(await confirm({ title: t('library.confirm.deleteForce.title', { name: item.name }), detail: `${errorText(err)}\n\n${t('library.confirm.deleteForce.detail')}`, confirmLabel: t('common.delete'), destructive: true }))) return;
        await remove.mutateAsync({ id: item.id, force: true });
      }
    }, 'library.toast.deleted');
  };

  return (
    <>
      <div className="mb-3 flex items-center gap-3">
        <p className="min-w-0 flex-1 text-[12px] text-[var(--text-secondary)]">{t(`library.hint.${kind}` as MessageKey)}</p>
        <Button variant="primary" icon={<Plus className="size-4" />} onClick={() => setEditing('new')} disabled={data?.canWrite === false}>
          {t('library.add')}
        </Button>
      </div>
      {query.isError && <Alert tone="danger" title={`${t('library.error.load')}: ${errorText(query.error)}`} />}
      {query.isLoading && <Spinner lines={3} />}
      {data && data.canWrite === false && <Alert tone="info" title={t('library.localOnly')} />}
      {data && data.items.length === 0 && (
        <EmptyState
          title={t('library.empty.title')}
          detail={t(`library.empty.${kind}` as MessageKey)}
          action={
            data.canWrite ? (
              <Button variant="primary" onClick={() => setEditing('new')}>
                {t('library.add')}
              </Button>
            ) : undefined
          }
        />
      )}
      {data && data.items.length > 0 && (
        <div className="card divide-y divide-[var(--border)]">
          {data.items.map((item) => (
            <ItemRow key={item.id} item={item} canWrite={data.canWrite} onEdit={() => setEditing(item)} onDelete={() => void del(item)} onRewrite={(force) => void rewrite(item, force)} />
          ))}
        </div>
      )}
      {data && <ResourceSheet segment={segment} library={data} item={editing} onClose={() => setEditing(null)} />}
    </>
  );
}

function ItemRow({ item, canWrite, onEdit, onDelete, onRewrite }: { item: ResourceItem; canWrite: boolean; onEdit: () => void; onDelete: () => void; onRewrite: (force: boolean) => void }) {
  const { t } = useI18n();
  const attention = item.targets.some((x) => x.state === 'drifted' || x.state === 'conflict' || x.state === 'missing');
  const drifted = item.targets.some((x) => x.state === 'drifted' || x.state === 'conflict');
  const preview = (item.kind === 'hook' ? `${item.event ?? ''}${item.matcher ? ` · ${item.matcher}` : ''} — ` : '') + (item.description || item.body).split('\n')[0];
  return (
    <div>
      <Row
        label={
          <span className="flex items-center gap-2">
            <span className="font-medium">{item.name}</span>
            {!item.enabled && <Badge>{t('library.state.off')}</Badge>}
          </span>
        }
        detail={<span className="block truncate font-mono text-[11px]" title={preview}>{preview}</span>}
      >
        {attention && canWrite && (
          <Button size="sm" icon={<RefreshCw className="size-3.5" />} onClick={() => onRewrite(false)}>
            {t('library.rewrite')}
          </Button>
        )}
        {drifted && canWrite && (
          <Button size="sm" onClick={() => onRewrite(true)}>
            {t('library.rewriteForce')}
          </Button>
        )}
        <Button size="sm" icon={<Pencil className="size-3.5" />} aria-label={t('common.edit')} onClick={onEdit} disabled={!canWrite} />
        <Button size="sm" icon={<Trash2 className="size-3.5" />} aria-label={t('common.delete')} onClick={onDelete} disabled={!canWrite} />
      </Row>
      <div className="flex flex-wrap gap-x-4 gap-y-1 px-4 pb-3">
        {item.targets.map((x) => (
          <TargetChip key={x.client + x.project} target={x} error={item.lastError?.[x.project ? `${x.client}@${x.project}` : x.client]} />
        ))}
        {item.targets.length === 0 && <span className="text-[11px] text-[var(--text-muted)]">{t('library.noTargets')}</span>}
      </div>
    </div>
  );
}

function TargetChip({ target: x, error }: { target: ResourceTargetState; error?: string }) {
  const { t } = useI18n();
  const why = error ?? x.message ?? undefined;
  return (
    <span className="flex flex-col gap-0.5">
      <span className="flex items-center gap-1.5 text-[12px]" title={x.path ?? undefined}>
        {clientName(x.client)}
        {x.project && <span className="font-mono text-[11px] text-[var(--text-muted)]" title={x.project}>{projectName(x.project)}</span>}
        <Badge tone={TONE[x.state]}>{t(`library.state.${x.state}` as MessageKey)}</Badge>
      </span>
      {why && <span className="max-w-md whitespace-normal text-[11px] text-[var(--danger)]">{why}</span>}
    </span>
  );
}

function ResourceSheet({ segment, library, item, onClose }: { segment: ResourceSegment; library: ResourceLibrary; item: ResourceItem | 'new' | null; onClose: () => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const save = useSaveResource(segment);
  const kind = KIND_OF[segment];
  const existing = item && item !== 'new' ? item : null;
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [body, setBody] = useState('');
  const [event, setEvent] = useState<string>('turn-end');
  const [matcher, setMatcher] = useState('');
  const [timeout, setTimeoutValue] = useState<number | null>(null);
  const [model, setModel] = useState('');
  const [enabled, setEnabled] = useState(true);
  const [targets, setTargets] = useState<ResourceTarget[]>([]);

  useEffect(() => {
    if (item === null) return;
    const e = item === 'new' ? null : item;
    setName(e?.name ?? '');
    setDescription(e?.description ?? '');
    setBody(e?.body ?? '');
    setEvent(e?.event ?? 'turn-end');
    setMatcher(e?.matcher ?? '');
    setTimeoutValue(e && e.timeout > 0 ? e.timeout : null);
    setModel(e?.model ?? '');
    setEnabled(e?.enabled ?? true);
    setTargets(e ? e.targets.map((x) => ({ client: x.client, project: x.project })) : []);
  }, [item]);

  const has = (client: string, project: string) => targets.some((x) => x.client === client && x.project === project);
  const toggle = (client: ClientKind, project: string, on: boolean) =>
    setTargets((cur) => (on ? [...cur.filter((x) => !(x.client === client && x.project === project)), { client, project }] : cur.filter((x) => !(x.client === client && x.project === project))));

  const needsDescription = kind === 'agent';
  const valid = name.trim() !== '' && (kind === 'env' || body.trim() !== '') && (!needsDescription || description.trim() !== '');
  const ownEvent = event.includes(':');
  const showMatcher = kind === 'hook' && !ownEvent && event !== 'turn-end' && event !== 'prompt';

  const submit = () => {
    const input: ResourceInput = { name: name.trim(), body, enabled, targets };
    if (kind === 'agent' || kind === 'command' || kind === 'hook') input.description = description.trim();
    if (kind === 'agent' && model.trim() !== '') input.model = model.trim();
    if (kind === 'hook') {
      input.event = event;
      input.matcher = showMatcher ? matcher.trim() : '';
      input.timeout = timeout ?? 0;
    }
    save.mutate(
      { id: existing?.id, body: input },
      {
        onSuccess: (saved) => {
          onClose();
          const failed = saved.targets.filter((x) => x.state === 'conflict' || x.state === 'drifted').length;
          if (failed > 0 || (saved.lastError && Object.keys(saved.lastError).length > 0)) toast(t('library.toast.savedWithProblems'), 'error');
          else toast(t('library.toast.saved', { name: saved.name }), 'success');
        },
        onError: (e) => toast(errorText(e), 'error'),
      },
    );
  };

  return (
    <Sheet
      open={item !== null}
      onOpenChange={(o) => !o && onClose()}
      width={580}
      title={existing ? t('library.editTitle', { name: existing.name }) : t(`library.addTitle.${kind}` as MessageKey)}
      footer={
        <>
          <Button onClick={onClose}>{t('common.cancel')}</Button>
          <Button variant="primary" disabled={!valid} loading={save.isPending} onClick={submit}>
            {t('common.save')}
          </Button>
        </>
      }
    >
      <div className="grid gap-4">
        <Field label={t('library.field.name')} hint={t(`library.nameHint.${kind}` as MessageKey)}>
          <Input mono value={name} maxLength={64} onChange={(e) => setName(e.target.value)} />
        </Field>
        {(kind === 'agent' || kind === 'command' || kind === 'hook') && (
          <Field label={t('library.field.description')}>
            <Input value={description} maxLength={200} onChange={(e) => setDescription(e.target.value)} />
          </Field>
        )}
        {kind === 'hook' && (
          <>
            <Field label={t('library.field.event')} hint={t('library.field.eventHint')}>
              <Select
                value={ownEvent ? '__own__' : event}
                onChange={(v) => setEvent(v === '__own__' ? 'claude-code:PermissionRequest' : v)}
                options={[
                  ...HOOK_EVENTS.map((e) => ({ value: e as string, label: t(`library.event.${e}` as MessageKey) })),
                  { value: '__own__', label: t('library.event.own') },
                ]}
              />
              {ownEvent && <Input mono className="mt-2" value={event} onChange={(e) => setEvent(e.target.value)} placeholder="claude-code:PermissionRequest" />}
            </Field>
            {showMatcher && (
              <Field label={t('library.field.matcher')} hint={event === 'session-start' ? t('library.field.matcherStarts') : t('library.field.matcherTools')}>
                {event === 'session-start' ? (
                  <Input mono value={matcher} placeholder="startup|resume" onChange={(e) => setMatcher(e.target.value)} />
                ) : (
                  <Select
                    value={matcher}
                    onChange={setMatcher}
                    options={[
                      { value: '', label: t('library.matcher.all') },
                      { value: 'shell', label: t('library.matcher.shell') },
                      { value: 'edit', label: t('library.matcher.edit') },
                    ]}
                  />
                )}
              </Field>
            )}
            <Field label={t('library.field.timeout')}>
              <NumberInput value={timeout} onChange={setTimeoutValue} min={0} step={1} />
            </Field>
          </>
        )}
        <Field label={t(`library.field.body.${kind}` as MessageKey)} hint={kind === 'command' ? t('library.field.commandArgsHint') : undefined}>
          {kind === 'hook' || kind === 'env' ? (
            <Input mono value={body} onChange={(e) => setBody(e.target.value)} />
          ) : (
            <TextArea mono rows={kind === 'instruction' ? 8 : 6} value={body} onChange={(e) => setBody(e.target.value)} />
          )}
        </Field>
        {kind === 'agent' && (
          <Field label={t('library.field.model')} hint={t('library.field.modelHint')}>
            <Input mono value={model} onChange={(e) => setModel(e.target.value)} placeholder="gpt-5" />
          </Field>
        )}
        <Field label={t('library.targets')} hint={t(library.projects.length > 0 ? 'library.targetsHint' : 'library.targetsHintNoProjects')}>
          <div className="grid gap-1">
            {library.clients.map((c) => (
              <Row
                key={c.kind}
                label={
                  <span className="flex items-center gap-2">
                    {clientName(c.kind)}
                    {!c.detected && <Badge>{t('library.undetected')}</Badge>}
                  </span>
                }
                detail={c.path ? <span className="font-mono text-[11px]" title={c.path}>{c.path}</span> : undefined}
              >
                <div className="flex flex-wrap items-center justify-end gap-x-3 gap-y-1">
                  <Checkbox label={t('library.userWide')} checked={has(c.kind, '')} onCheckedChange={(s) => toggle(c.kind, '', s === true)} />
                  {c.project &&
                    library.projects.map((p) => (
                      <Checkbox key={p.path} label={p.name} checked={has(c.kind, p.path)} disabled={!p.exists} onCheckedChange={(s) => toggle(c.kind, p.path, s === true)} />
                    ))}
                </div>
              </Row>
            ))}
          </div>
        </Field>
        <Row label={t('library.enabled')}>
          <Switch checked={enabled} label={t('library.enabled')} onChange={setEnabled} />
        </Row>
      </div>
    </Sheet>
  );
}
