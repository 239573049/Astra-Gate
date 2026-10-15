import { closestCenter, DndContext, KeyboardSensor, PointerSensor, useSensor, useSensors, type DragEndEvent } from '@dnd-kit/core';
import { arrayMove, SortableContext, sortableKeyboardCoordinates, useSortable, verticalListSortingStrategy } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { GripVertical, Trash2 } from 'lucide-react';

import { useProviderAccounts } from '../api/hooks';
import type { PoolMemberHealth, Provider, Token, TokenPoolInput, TokenPoolSticky, TokenPoolStrategy } from '../api/types';
import { useI18n } from '../i18n';
import { formatTime } from '../lib/format';
import { cn } from '../lib/cn';
import { ProviderIcon } from './icons';
import { Badge, Button, NumberInput, Segmented, Switch } from './ui/controls';
import { Select } from './ui/overlays';

/** A pool member being edited. */
export interface PoolDraftMember {
  providerId: string;
  accountId: string | null;
  weight: number;
  tier: number;
}

/** The provider pool of a token's direct calls as the settings sheet edits it (issue #2). */
export interface PoolDraft {
  strategy: TokenPoolStrategy;
  sticky: TokenPoolSticky;
  failover: boolean;
  maxAttempts: number;
  members: PoolDraftMember[];
}

export const POOL_STRATEGIES: TokenPoolStrategy[] = ['failover', 'round_robin', 'weighted', 'tiered', 'least_busy'];

/** The token's pool, or its single direct provider as a pool of one (new pools start as failover with sticky sessions). */
export function poolDraftOf(token: Token): PoolDraft {
  if (token.pool)
    return {
      strategy: token.pool.strategy,
      sticky: token.pool.sticky,
      failover: token.pool.failover,
      maxAttempts: token.pool.maxAttempts,
      members: token.pool.members.map((m) => ({ providerId: m.providerId, accountId: m.accountId ?? null, weight: m.weight, tier: m.tier })),
    };
  return {
    strategy: 'failover',
    sticky: 'session',
    failover: true,
    maxAttempts: 3,
    members: token.providerId ? [{ providerId: token.providerId, accountId: token.accountId ?? null, weight: 1, tier: 1 }] : [],
  };
}

/** What the API takes: members whose provider no longer exists are dropped. */
export function poolInputOf(draft: PoolDraft, providers: Provider[]): TokenPoolInput {
  const known = new Set(providers.map((p) => p.id));
  return {
    strategy: draft.strategy,
    sticky: draft.sticky,
    failover: draft.failover,
    maxAttempts: Math.min(5, Math.max(1, Math.floor(draft.maxAttempts || 1))),
    members: draft.members.filter((m) => known.has(m.providerId)).map((m) => ({ ...m, weight: Math.max(1, Math.floor(m.weight || 1)) })),
  };
}

/** Share of new requests each member gets when all are healthy and serve the model (mirrors the server's ProviderPoolScheduler). */
export function expectedShares(draft: PoolDraft): number[] {
  const { members, strategy } = draft;
  if (members.length === 0) return [];
  const weights = (list: PoolDraftMember[]) => list.reduce((s, m) => s + Math.max(1, m.weight), 0);
  switch (strategy) {
    case 'failover':
      return members.map((_, i) => (i === 0 ? 1 : 0));
    case 'weighted': {
      const total = weights(members);
      return members.map((m) => Math.max(1, m.weight) / total);
    }
    case 'tiered': {
      const top = Math.min(...members.map((m) => m.tier));
      const total = weights(members.filter((m) => m.tier === top));
      return members.map((m) => (m.tier === top ? Math.max(1, m.weight) / total : 0));
    }
    default:
      return members.map(() => 1 / members.length);
  }
}

const SHARE_COLORS = ['var(--accent)', 'var(--success)', 'var(--warning)', '#af52de', '#5ac8fa', '#ff2d55'];
const ACCOUNT_SCHEMES = new Set(['oauth-subscription', 'plugin']);

export function healthTone(state: PoolMemberHealth['state']): 'green' | 'orange' | 'red' {
  return state === 'healthy' ? 'green' : state === 'cooling' || state === 'open' || state === 'half_open' ? 'orange' : 'red';
}

export function useHealthLabel() {
  const { t, locale } = useI18n();
  return (h: PoolMemberHealth) =>
    h.state === 'cooling' || h.state === 'open'
      ? t(`tokens.health.${h.state}`, { time: h.until ? formatTime(h.until, locale) : '—' })
      : t(`tokens.health.${h.state}`);
}

/**
 * The provider pool of a token's direct calls: strategy, members in order (drag to reorder), their weight / tier /
 * account and live health, sticky sessions and failover. One member is a plain direct provider.
 */
export function TokenPoolEditor({
  draft,
  onChange,
  providers,
  health,
}: {
  draft: PoolDraft;
  onChange: (d: PoolDraft) => void;
  /** Every provider (disabled ones included, so a disabled member still shows). */
  providers: Provider[];
  /** Live health of the saved pool's members, by provider id. */
  health?: Map<string, PoolMemberHealth>;
}) {
  const { t } = useI18n();
  const byId = new Map(providers.map((p) => [p.id, p]));
  const pooled = draft.members.length > 1;
  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 4 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );
  const setMembers = (members: PoolDraftMember[]) => onChange({ ...draft, members });
  const setMember = (i: number, patch: Partial<PoolDraftMember>) => setMembers(draft.members.map((m, j) => (j === i ? { ...m, ...patch } : m)));
  const onDragEnd = ({ active, over }: DragEndEvent) => {
    if (!over || active.id === over.id) return;
    const from = draft.members.findIndex((m) => m.providerId === active.id);
    const to = draft.members.findIndex((m) => m.providerId === over.id);
    if (from >= 0 && to >= 0) setMembers(arrayMove(draft.members, from, to));
  };
  const addable = providers.filter((p) => p.enabled && !draft.members.some((m) => m.providerId === p.id));
  const shares = expectedShares(draft);
  const noModels = draft.members.flatMap((m) => {
    const p = byId.get(m.providerId);
    return p && p.modelCount === 0 ? [p.name] : [];
  });

  return (
    <div className="grid gap-3">
      {pooled && (
        <div className="grid gap-1.5">
          <Segmented
            ariaLabel={t('tokens.pool.strategy')}
            value={draft.strategy}
            onChange={(strategy) => onChange({ ...draft, strategy })}
            items={POOL_STRATEGIES.map((s) => ({ value: s, label: t(`tokens.strategy.${s}`) }))}
          />
          <p className="text-[12px] text-[var(--text-secondary)]">{t(`tokens.strategy.${draft.strategy}.hint`)}</p>
        </div>
      )}

      {draft.members.length === 0 ? (
        <div className="text-[12px] text-[var(--text-muted)]">{t('tokens.pool.empty')}</div>
      ) : (
        <div className="overflow-hidden rounded-[var(--radius-control)] border border-[var(--border-subtle)]">
          <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={onDragEnd}>
            <SortableContext items={draft.members.map((m) => m.providerId)} strategy={verticalListSortingStrategy}>
              {draft.members.map((m, i) => (
                <PoolMemberRow
                  key={m.providerId}
                  member={m}
                  index={i}
                  draft={draft}
                  provider={byId.get(m.providerId)}
                  health={health?.get(m.providerId)}
                  onChange={(patch) => setMember(i, patch)}
                  onRemove={() => setMembers(draft.members.filter((_, j) => j !== i))}
                />
              ))}
            </SortableContext>
          </DndContext>
        </div>
      )}

      {addable.length > 0 && (
        <Select
          ariaLabel={t('tokens.pool.add')}
          placeholder={t('tokens.pool.add')}
          value={null}
          onChange={(id) => setMembers([...draft.members, { providerId: id, accountId: null, weight: 1, tier: 1 }])}
          options={addable.map((p) => ({ value: p.id, label: p.name }))}
        />
      )}

      {noModels.length > 0 && <p className="text-[12px] text-[var(--warning)]">{t('tokens.pool.noModels', { providers: noModels.join(', ') })}</p>}

      {pooled && (
        <div className="grid gap-1.5">
          <div className="flex flex-wrap items-baseline justify-between gap-2 text-[12px]">
            <span className="font-medium text-[var(--text-secondary)]">{t('tokens.pool.share')}</span>
            <span className="text-[var(--text-muted)]">{t('tokens.pool.shareHint')}</span>
          </div>
          <div className="flex h-2 gap-0.5 overflow-hidden rounded-full bg-[var(--fill-2)]" aria-hidden="true">
            {draft.members.map((m, i) =>
              shares[i] > 0 ? <span key={m.providerId} style={{ flex: shares[i], background: SHARE_COLORS[i % SHARE_COLORS.length] }} /> : null,
            )}
          </div>
          <div className="flex flex-wrap gap-x-3 gap-y-1 text-[12px] text-[var(--text-secondary)]">
            {draft.members.map((m, i) => (
              <span key={m.providerId} className="inline-flex items-center gap-1.5">
                <span className="inline-block size-2 rounded-sm" style={{ background: SHARE_COLORS[i % SHARE_COLORS.length] }} />
                {byId.get(m.providerId)?.name ?? m.providerId}
                <b className="tabular-nums text-[var(--text-primary)]">{Math.round(shares[i] * 100)}%</b>
                {shares[i] === 0 && <span className="text-[var(--text-muted)]">{t('tokens.pool.standby')}</span>}
              </span>
            ))}
          </div>
        </div>
      )}

      {pooled && (
        <div className="grid gap-2 text-[13px]">
          <label className="flex items-start justify-between gap-3">
            <span className="grid gap-0.5">
              <span>{t('tokens.pool.sticky')}</span>
              <span className="text-[12px] text-[var(--text-muted)]">{t('tokens.pool.stickyHint')}</span>
            </span>
            <Switch label={t('tokens.pool.sticky')} checked={draft.sticky === 'session'} onChange={(on) => onChange({ ...draft, sticky: on ? 'session' : 'off' })} />
          </label>
          <label className="flex items-start justify-between gap-3">
            <span className="grid gap-0.5">
              <span>{t('tokens.pool.failover')}</span>
              <span className="text-[12px] text-[var(--text-muted)]">{t('tokens.pool.failoverHint')}</span>
            </span>
            <Switch label={t('tokens.pool.failover')} checked={draft.failover} onChange={(failover) => onChange({ ...draft, failover })} />
          </label>
          {draft.failover && (
            <div className="flex items-center justify-between gap-3">
              <span>{t('tokens.pool.maxAttempts')}</span>
              <div className="w-20">
                <NumberInput label={t('tokens.pool.maxAttempts')} value={draft.maxAttempts} min={1} step={1} onChange={(v) => onChange({ ...draft, maxAttempts: v ?? 1 })} />
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function PoolMemberRow({
  member,
  index,
  draft,
  provider,
  health,
  onChange,
  onRemove,
}: {
  member: PoolDraftMember;
  index: number;
  draft: PoolDraft;
  provider?: Provider;
  health?: PoolMemberHealth;
  onChange: (patch: Partial<PoolDraftMember>) => void;
  onRemove: () => void;
}) {
  const { t } = useI18n();
  const healthLabel = useHealthLabel();
  const { attributes, listeners, setNodeRef, setActivatorNodeRef, transform, transition, isDragging } = useSortable({ id: member.providerId });
  const accountBased = !!provider && ACCOUNT_SCHEMES.has(provider.authScheme);
  const accounts = useProviderAccounts(provider?.id, accountBased);
  const name = provider?.name ?? member.providerId;
  const pooled = draft.members.length > 1;
  const state = !provider ? ({ state: 'missing' } as PoolMemberHealth) : !provider.enabled ? ({ state: 'disabled' } as PoolMemberHealth) : health;

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
        {provider && <ProviderIcon name={provider.name} icon={provider.icon} colorKey={provider.templateId} size={20} />}
        <span className="truncate text-[13px] font-medium">{name}</span>
        {pooled && draft.strategy === 'failover' && (
          <Badge tone={index === 0 ? 'accent' : 'neutral'}>{index === 0 ? t('tokens.pool.primary') : t('tokens.pool.backup', { n: index })}</Badge>
        )}
        {state && <Badge tone={healthTone(state.state)}>{healthLabel(state)}</Badge>}
      </div>
      <div className="flex flex-wrap items-center gap-2">
        {pooled && (draft.strategy === 'weighted' || draft.strategy === 'tiered') && (
          <div className="w-20">
            <NumberInput label={t('tokens.pool.weight')} placeholder={t('tokens.pool.weight')} value={member.weight} min={1} step={1} onChange={(v) => onChange({ weight: v ?? 1 })} />
          </div>
        )}
        {pooled && draft.strategy === 'tiered' && (
          <Select
            ariaLabel={t('tokens.pool.tier')}
            value={String(member.tier) as '1' | '2' | '3'}
            onChange={(v) => onChange({ tier: Number(v) })}
            options={(['1', '2', '3'] as const).map((n) => ({ value: n, label: t('tokens.pool.tierN', { n }) }))}
          />
        )}
        {accountBased && (
          <Select
            ariaLabel={t('tokens.directAccount')}
            value={member.accountId ?? ''}
            onChange={(v) => onChange({ accountId: v || null })}
            options={[{ value: '', label: t('clients.accountDefault') }, ...(accounts.data ?? []).map((a) => ({ value: a.id, label: a.displayName || a.id }))]}
          />
        )}
        <Button
          size="sm"
          variant="plain"
          icon={<Trash2 className="size-3.5" />}
          aria-label={t('tokens.pool.remove', { provider: name })}
          title={t('tokens.pool.remove', { provider: name })}
          onClick={onRemove}
        />
      </div>
    </div>
  );
}
