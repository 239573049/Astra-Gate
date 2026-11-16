// Routing groups (magpie parity): named virtual models `group/<slug>` clients pick like any model. A group's members are provider/model
// targets or other groups; the gateway orders them per request by the group's mode (smart / order / rotate / usage / pace / weight) and
// the next member takes over while an attempt fails before anything reached the client. A conversation stays on the member that
// served it while the provider's prompt cache is worth keeping ("stays"). Each member row shows its live state: cooling, quota left,
// when it was last used.

import { closestCenter, DndContext, KeyboardSensor, PointerSensor, useSensor, useSensors, type DragEndEvent } from '@dnd-kit/core';
import { arrayMove, SortableContext, sortableKeyboardCoordinates, useSortable, verticalListSortingStrategy } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { GripVertical, Layers, Pencil, Plus, Trash2 } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';

import { ApiError } from '../api/client';
import { useCreateRoutingGroup, useDeleteRoutingGroup, useRoutingGroups, useRoutingGroupStatus, useRoutingModels, useUpdateRoutingGroup } from '../api/hooks';
import type { RoutingGroup, RoutingGroupMember, RoutingGroupMemberStatus, RoutingGroupMode, RoutingGroupStays, RoutingGroupStatus, RoutingModel } from '../api/types';
import { useI18n, type MessageKey, type TFunction } from '../i18n';
import { zh } from '../i18n/messages';
import { cn } from '../lib/cn';
import { formatDateTime } from '../lib/format';
import { Alert } from './arc/alert/alert';
import { Badge, Button, EmptyState, Field, Input, NumberInput, Spinner, Switch } from './ui/controls';
import { errorText, Select, Sheet, useFeedback } from './ui/overlays';

export const GROUP_MODES: RoutingGroupMode[] = ['smart', 'order', 'rotate', 'usage', 'pace', 'weight'];
export const GROUP_STAYS: RoutingGroupStays[] = ['auto', 'session', 'turn', 'off', 'strict'];

/** A member as the editor holds it. */
export type DraftMember = RoutingGroupMember;

export interface GroupDraft {
  name: string;
  slug: string;
  mode: RoutingGroupMode;
  stays: RoutingGroupStays;
  enabled: boolean;
  listed: boolean;
  members: DraftMember[];
}

/** The key of a member inside its group: `provider/model` or `group/slug` (the server keys it the same way). */
export const memberKey = (m: Pick<RoutingGroupMember, 'provider' | 'model' | 'group'>): string => (m.group ? `group/${m.group}` : `${m.provider}/${m.model}`);

/** A slug from a display name: lower-case letters and digits joined by single dashes (the server's rule: up to 48 characters). */
export function slugFrom(name: string): string {
  const slug = name
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
  return slug.length > 48 ? slug.slice(0, 48).replace(/-+$/, '') : slug;
}

export const emptyDraft = (): GroupDraft => ({ name: '', slug: '', mode: 'smart', stays: 'auto', enabled: true, listed: true, members: [] });

export const draftOf = (g: RoutingGroup): GroupDraft => ({
  name: g.name,
  slug: g.slug,
  mode: g.mode,
  stays: g.stays,
  enabled: g.enabled,
  listed: g.listed,
  members: g.members.map((m) => ({ provider: m.provider ?? null, model: m.model ?? null, group: m.group ?? null, weight: m.weight })),
});

/** Whether the draft can be saved: a name, a slug the server accepts, and at least one member. */
export function draftValid(d: GroupDraft): boolean {
  return d.name.trim() !== '' && /^[a-z0-9][a-z0-9-]{0,47}$/.test(d.slug) && d.members.length > 0;
}

/** The PATCH / POST body: weights clamped to 1–1000 (only the weight mode reads them). */
export function writeOf(d: GroupDraft, creating: boolean) {
  return {
    name: d.name.trim(),
    ...(creating ? { slug: d.slug } : {}),
    mode: d.mode,
    stays: d.stays,
    enabled: d.enabled,
    listed: d.listed,
    members: d.members.map((m) => ({ ...m, weight: Math.min(1000, Math.max(1, Math.floor(m.weight || 1))) })),
  };
}

/** The reasons a refused save carries (error.details.problems), in the console's words. */
export function problemMessages(err: unknown, t: TFunction): string[] {
  if (err instanceof ApiError) {
    const problems = (err.details as { problems?: { code: string; message: string }[] } | undefined)?.problems;
    if (problems?.length)
      return problems.map((p) => {
        const key = `routingGroups.problem.${p.code}`;
        return key in zh ? t(key as MessageKey) : p.message;
      });
  }
  return [errorText(err)];
}

const HEALTH_TONE: Record<string, 'green' | 'orange' | 'red' | 'neutral'> = {
  healthy: 'green',
  cooling: 'orange',
  open: 'orange',
  half_open: 'orange',
  no_account: 'red',
  off: 'red',
  missing: 'red',
  group: 'neutral',
};

/** Why a request went where it did, in words (a RankReasons code); the raw code when this build does not know it. */
export function reasonText(reason: string | null | undefined, t: TFunction): string {
  if (!reason) return '—';
  const key = `routingGroups.reason.${reason}`;
  return key in zh ? t(key as MessageKey) : reason;
}

function useHealthText() {
  const { t, locale } = useI18n();
  return (s: RoutingGroupMemberStatus): string =>
    s.health === 'cooling' || s.health === 'open'
      ? t(`routingGroups.health.${s.health}`, { time: s.coolingUntil ? formatDateTime(s.coolingUntil, locale) : '—' })
      : t(`routingGroups.health.${s.health}` as MessageKey);
}

/** The routing groups page body (/routing-groups): the list with each group's live members, and the editor sheet. */
export function RoutingGroups() {
  const { t } = useI18n();
  const { toast, confirm } = useFeedback();
  const groups = useRoutingGroups();
  const status = useRoutingGroupStatus();
  const update = useUpdateRoutingGroup();
  const remove = useDeleteRoutingGroup();
  const [editing, setEditing] = useState<GroupDraft | { id: string; draft: GroupDraft } | null>(null);

  const statusOf = (id: string): RoutingGroupStatus | undefined => status.data?.find((s) => s.id === id);

  const askDelete = async (g: RoutingGroup) => {
    const ok = await confirm({ title: t('routingGroups.deleteTitle', { name: g.name }), detail: t('routingGroups.deleteDetail', { model: g.modelId }), confirmLabel: t('common.delete'), destructive: true });
    if (ok) remove.mutate(g.id, { onError: (e) => toast(problemMessages(e, t).join('\n'), 'error') });
  };

  return (
    <>
      <div className="mb-3 flex items-center justify-between gap-3">
        <p className="min-w-0 flex-1 text-[12px] text-[var(--text-secondary)]">{t('routingGroups.intro')}</p>
        <Button variant="primary" icon={<Plus className="size-3.5" />} onClick={() => setEditing(emptyDraft())}>
          {t('routingGroups.add')}
        </Button>
      </div>

      {groups.isLoading && <Spinner lines={3} />}
      {groups.data?.length === 0 && (
        <div className="card">
          <EmptyState icon={<Layers className="size-6" />} title={t('routingGroups.empty')} detail={t('routingGroups.emptyDetail')} />
        </div>
      )}
      <div className="grid gap-3">
        {groups.data?.map((g) => (
          <GroupCard
            key={g.id}
            group={g}
            status={statusOf(g.id)}
            onEdit={() => setEditing({ id: g.id, draft: draftOf(g) })}
            onDelete={() => void askDelete(g)}
            onToggle={(enabled) => update.mutate({ id: g.id, body: { enabled } }, { onError: (e) => toast(problemMessages(e, t).join('\n'), 'error') })}
          />
        ))}
      </div>

      <GroupSheet
        target={editing}
        groups={groups.data ?? []}
        status={editing && 'id' in editing ? statusOf(editing.id) : undefined}
        onClose={() => setEditing(null)}
      />
    </>
  );
}

function GroupCard({
  group,
  status,
  onEdit,
  onDelete,
  onToggle,
}: {
  group: RoutingGroup;
  status?: RoutingGroupStatus;
  onEdit: () => void;
  onDelete: () => void;
  onToggle: (enabled: boolean) => void;
}) {
  const { t, locale } = useI18n();
  const healthText = useHealthText();
  const byKey = new Map(status?.members.map((m) => [m.key, m]) ?? []);
  return (
    <div className={cn('card p-4', !group.enabled && 'opacity-70')}>
      <div className="flex flex-wrap items-start gap-3">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-[14px] font-medium">{group.name}</span>
            <Badge tone="accent">{t(`routingGroups.mode.${group.mode}` as MessageKey)}</Badge>
            {!group.enabled && <Badge tone="neutral">{t('routingGroups.disabled')}</Badge>}
            {!group.listed && <Badge tone="neutral">{t('routingGroups.unlisted')}</Badge>}
          </div>
          <code className="selectable text-[12px] text-[var(--text-secondary)]">{group.modelId}</code>
        </div>
        <div className="flex items-center gap-2">
          <Switch label={t('routingGroups.enabled')} checked={group.enabled} onChange={onToggle} />
          <Button size="sm" variant="plain" icon={<Pencil className="size-3.5" />} aria-label={t('common.edit')} title={t('common.edit')} onClick={onEdit} />
          <Button size="sm" variant="plain" icon={<Trash2 className="size-3.5" />} aria-label={t('common.delete')} title={t('common.delete')} onClick={onDelete} />
        </div>
      </div>
      <div className="mt-3 divide-y divide-[var(--border-subtle)] overflow-hidden rounded-[var(--radius-control)] border border-[var(--border-subtle)]">
        {group.members.map((m, i) => (
          <MemberLine key={memberKey(m)} member={m} index={i} group={group} live={byKey.get(memberKey(m))} healthText={healthText} />
        ))}
      </div>
      {status?.lastReason && status.lastDecisionAt && (
        <p className="mt-2 text-[11px] text-[var(--text-muted)]">
          {t('routingGroups.lastDecision', { reason: reasonText(status.lastReason, t), time: formatDateTime(status.lastDecisionAt, locale) })}
        </p>
      )}
    </div>
  );
}

/** One member's line: who it is, and (live) its health, quota left and last use. */
function MemberLine({
  member,
  index,
  group,
  live,
  healthText,
}: {
  member: RoutingGroupMember;
  index: number;
  group: RoutingGroup;
  live?: RoutingGroupMemberStatus;
  healthText: (s: RoutingGroupMemberStatus) => string;
}) {
  const { t, locale } = useI18n();
  const name = member.group ? `group/${member.group}` : `${live?.providerName ?? member.provider} · ${member.model}`;
  const quota = live?.usedPercent != null
    ? t('routingGroups.quotaUsed', { percent: Math.round(live.usedPercent) }) + (live.resetsAt ? ' · ' + t('routingGroups.resets', { time: formatDateTime(live.resetsAt, locale) }) : '')
    : null;
  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-1 px-3 py-2 text-[12px]">
      <span className="w-5 shrink-0 text-center tabular-nums text-[var(--text-muted)]">{index + 1}</span>
      <span className="min-w-0 flex-1 basis-44 truncate text-[13px]">{name}</span>
      {group.mode === 'weight' && <Badge tone="neutral">{t('routingGroups.weightN', { n: member.weight })}</Badge>}
      {live && <Badge tone={HEALTH_TONE[live.health] ?? 'neutral'}>{healthText(live)}</Badge>}
      {live?.problem && <Badge tone="red">{t(`routingGroups.memberProblem.${live.problem}` as MessageKey)}</Badge>}
      {quota && <span className="text-[var(--text-secondary)]">{quota}</span>}
      {live && !quota && live.health !== 'group' && <span className="text-[var(--text-muted)]">{t('routingGroups.quotaUnknown')}</span>}
      {live?.lastUsed && <span className="text-[var(--text-muted)]">{t('routingGroups.lastUsed', { time: formatDateTime(live.lastUsed, locale) })}</span>}
    </div>
  );
}

// ---------------------------------------------------------------- the editor

function GroupSheet({
  target,
  groups,
  status,
  onClose,
}: {
  target: GroupDraft | { id: string; draft: GroupDraft } | null;
  groups: RoutingGroup[];
  status?: RoutingGroupStatus;
  onClose: () => void;
}) {
  const { t } = useI18n();
  const healthText = useHealthText();
  const create = useCreateRoutingGroup();
  const update = useUpdateRoutingGroup();
  const models = useRoutingModels();
  const id = target && 'id' in target ? target.id : null;
  const [draft, setDraft] = useState<GroupDraft>(emptyDraft());
  const [slugTouched, setSlugTouched] = useState(false);
  const [problems, setProblems] = useState<string[]>([]);

  useEffect(() => {
    if (!target) return;
    setDraft('id' in target ? target.draft : target);
    setSlugTouched('id' in target);
    setProblems([]);
  }, [target]);

  const existing = id ? groups.find((g) => g.id === id) : undefined;
  const pending = create.isPending || update.isPending;
  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 4 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );
  const patch = (p: Partial<GroupDraft>) => setDraft((d) => ({ ...d, ...p }));
  const setMembers = (members: DraftMember[]) => patch({ members });
  const taken = new Set(draft.members.map(memberKey));

  const modelOptions = useMemo(
    () =>
      (models.data ?? [])
        .filter((m) => !taken.has(`${m.providerId}/${m.id}`))
        .map((m) => ({ value: `${m.providerId}/${m.id}`, label: `${m.providerName} · ${m.id}`, m })),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [models.data, draft.members],
  );
  const groupOptions = groups.filter((g) => g.id !== id && !taken.has(`group/${g.slug}`)).map((g) => ({ value: g.slug, label: `group/${g.slug} · ${g.name}` }));

  const onDragEnd = ({ active, over }: DragEndEvent) => {
    if (!over || active.id === over.id) return;
    const from = draft.members.findIndex((m) => memberKey(m) === active.id);
    const to = draft.members.findIndex((m) => memberKey(m) === over.id);
    if (from >= 0 && to >= 0) setMembers(arrayMove(draft.members, from, to));
  };

  const save = () => {
    const body = writeOf(draft, id === null);
    const done = { onSuccess: () => onClose(), onError: (e: unknown) => setProblems(problemMessages(e, t)) };
    if (id) update.mutate({ id, body }, done);
    else create.mutate(body, done);
  };

  const liveByKey = new Map(status?.members.map((m) => [m.key, m]) ?? []);
  const nameOf = (m: DraftMember, option?: RoutingModel) =>
    m.group ? `group/${m.group}` : `${option?.providerName ?? liveByKey.get(memberKey(m))?.providerName ?? m.provider} · ${m.model}`;
  const modelByKey = new Map((models.data ?? []).map((m) => [`${m.providerId}/${m.id}`, m]));

  return (
    <Sheet
      open={target !== null}
      onOpenChange={(o) => !o && onClose()}
      width={600}
      title={existing ? t('routingGroups.editTitle', { name: existing.name }) : t('routingGroups.addTitle')}
      footer={
        <>
          <Button onClick={onClose}>{t('common.cancel')}</Button>
          <Button variant="primary" disabled={!draftValid(draft)} loading={pending} onClick={save}>
            {t('common.save')}
          </Button>
        </>
      }
    >
      <div className="grid gap-4">
        <Field label={t('routingGroups.name')}>
          <Input
            value={draft.name}
            maxLength={80}
            placeholder="Opus anywhere"
            onChange={(e) => patch({ name: e.target.value, ...(!slugTouched && id === null ? { slug: slugFrom(e.target.value) } : {}) })}
          />
        </Field>
        <Field label={t('routingGroups.slug')} hint={id ? t('routingGroups.slugLocked') : t('routingGroups.slugHint', { model: `group/${draft.slug || '…'}` })}>
          <Input
            mono
            value={draft.slug}
            maxLength={48}
            disabled={id !== null}
            placeholder="opus-anywhere"
            onChange={(e) => {
              setSlugTouched(true);
              patch({ slug: e.target.value });
            }}
          />
        </Field>

        <Field label={t('routingGroups.modeLabel')} hint={t(`routingGroups.mode.${draft.mode}.hint` as MessageKey)}>
          <Select value={draft.mode} onChange={(mode) => patch({ mode })} options={GROUP_MODES.map((m) => ({ value: m, label: t(`routingGroups.mode.${m}` as MessageKey) }))} />
        </Field>
        <Field label={t('routingGroups.staysLabel')} hint={t(`routingGroups.stays.${draft.stays}.hint` as MessageKey)}>
          <Select value={draft.stays} onChange={(stays) => patch({ stays })} options={GROUP_STAYS.map((s) => ({ value: s, label: t(`routingGroups.stays.${s}` as MessageKey) }))} />
        </Field>

        <div className="grid gap-2">
          <div className="text-[12px] font-medium text-[var(--text-secondary)]">{t('routingGroups.membersTitle')}</div>
          <p className="text-[12px] text-[var(--text-muted)]">{t('routingGroups.membersHint')}</p>
          {draft.members.length === 0 ? (
            <div className="text-[12px] text-[var(--text-muted)]">{t('routingGroups.noMembers')}</div>
          ) : (
            <div className="overflow-hidden rounded-[var(--radius-control)] border border-[var(--border-subtle)]">
              <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={onDragEnd}>
                <SortableContext items={draft.members.map(memberKey)} strategy={verticalListSortingStrategy}>
                  {draft.members.map((m, i) => (
                    <SortableMember
                      key={memberKey(m)}
                      member={m}
                      name={nameOf(m, modelByKey.get(memberKey(m)))}
                      weighted={draft.mode === 'weight'}
                      live={liveByKey.get(memberKey(m))}
                      healthText={healthText}
                      onWeight={(weight) => setMembers(draft.members.map((x, j) => (j === i ? { ...x, weight } : x)))}
                      onRemove={() => setMembers(draft.members.filter((_, j) => j !== i))}
                    />
                  ))}
                </SortableContext>
              </DndContext>
            </div>
          )}
          <div className="grid gap-2 sm:grid-cols-2">
            <Select
              ariaLabel={t('routingGroups.addModel')}
              placeholder={t('routingGroups.addModel')}
              value={null}
              disabled={modelOptions.length === 0}
              onChange={(key) => {
                const option = modelOptions.find((o) => o.value === key)?.m;
                if (option) setMembers([...draft.members, { provider: option.providerId, model: option.id, group: null, weight: 1 }]);
              }}
              options={modelOptions.map(({ value, label }) => ({ value, label }))}
            />
            <Select
              ariaLabel={t('routingGroups.addGroup')}
              placeholder={t('routingGroups.addGroup')}
              value={null}
              disabled={groupOptions.length === 0}
              onChange={(slug) => setMembers([...draft.members, { provider: null, model: null, group: slug, weight: 1 }])}
              options={groupOptions}
            />
          </div>
        </div>

        <div className="grid gap-2 text-[13px]">
          <label className="flex items-start justify-between gap-3">
            <span className="grid gap-0.5">
              <span>{t('routingGroups.listed')}</span>
              <span className="text-[12px] text-[var(--text-muted)]">{t('routingGroups.listedHint')}</span>
            </span>
            <Switch label={t('routingGroups.listed')} checked={draft.listed} onChange={(listed) => patch({ listed })} />
          </label>
          <label className="flex items-center justify-between gap-3">
            <span>{t('routingGroups.enabled')}</span>
            <Switch label={t('routingGroups.enabled')} checked={draft.enabled} onChange={(enabled) => patch({ enabled })} />
          </label>
        </div>

        {problems.length > 0 && (
          <Alert tone="danger" title={t('routingGroups.refused')}>
            <ul className="list-disc pl-4">
              {problems.map((p) => (
                <li key={p}>{p}</li>
              ))}
            </ul>
          </Alert>
        )}
      </div>
    </Sheet>
  );
}

function SortableMember({
  member,
  name,
  weighted,
  live,
  healthText,
  onWeight,
  onRemove,
}: {
  member: DraftMember;
  name: string;
  weighted: boolean;
  live?: RoutingGroupMemberStatus;
  healthText: (s: RoutingGroupMemberStatus) => string;
  onWeight: (weight: number) => void;
  onRemove: () => void;
}) {
  const { t } = useI18n();
  const { attributes, listeners, setNodeRef, setActivatorNodeRef, transform, transition, isDragging } = useSortable({ id: memberKey(member) });
  return (
    <div
      ref={setNodeRef}
      style={{ transform: CSS.Transform.toString(transform), transition }}
      className={cn(
        'flex flex-wrap items-center gap-x-3 gap-y-2 border-b border-[var(--border-subtle)] px-2.5 py-2 last:border-b-0',
        isDragging && 'relative z-10 bg-[var(--surface-solid)] shadow-lg',
      )}
    >
      <div className="flex min-w-0 flex-1 basis-48 items-center gap-2">
        <button
          ref={setActivatorNodeRef}
          type="button"
          aria-label={t('clients.dragToReorder', { provider: name })}
          title={t('clients.dragToReorder', { provider: name })}
          className="inline-flex cursor-grab touch-none text-[var(--text-muted)] hover:text-[var(--text-secondary)] active:cursor-grabbing"
          {...attributes}
          {...listeners}
        >
          <GripVertical className="size-4" />
        </button>
        <span className="truncate text-[13px] font-medium">{name}</span>
        {live && <Badge tone={HEALTH_TONE[live.health] ?? 'neutral'}>{healthText(live)}</Badge>}
        {live?.problem && <Badge tone="red">{t(`routingGroups.memberProblem.${live.problem}` as MessageKey)}</Badge>}
      </div>
      <div className="flex items-center gap-2">
        {weighted && (
          <div className="w-20">
            <NumberInput label={t('routingGroups.weight')} placeholder={t('routingGroups.weight')} value={member.weight} min={1} step={1} onChange={(v) => onWeight(v ?? 1)} />
          </div>
        )}
        <Button size="sm" variant="plain" icon={<Trash2 className="size-3.5" />} aria-label={t('routingGroups.remove', { name })} title={t('routingGroups.remove', { name })} onClick={onRemove} />
      </div>
    </div>
  );
}
