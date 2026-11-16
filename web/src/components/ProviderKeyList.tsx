import { ArrowDown, ArrowUp, KeyRound, Pencil, Plus, Trash2 } from 'lucide-react';
import { useState } from 'react';

import {
  useActivateProviderKey,
  useAddProviderKey,
  usePatchProviderKey,
  useProviderKeys,
  useProviderUsage,
  useRemoveProviderKey,
  useReorderProviderKeys,
  useSubscriptionPolicy,
  useUpdateSubscriptionPolicy,
} from '../api/hooks';
import type { ApiProtocol, Provider, ProviderAccount, ProviderAccountUsage } from '../api/types';
import { Alert } from './arc/alert/alert';
import { Badge, Button, Dot, Field, Group, Input, NumberInput, Switch, TextArea } from './ui/controls';
import { errorText, Select, Sheet, useFeedback } from './ui/overlays';
import { SwitchModePicker } from './SubscriptionAccounts';
import { useI18n } from '../i18n';
import { formatDateTime, formatTokens, formatUsd, usdFromNanos } from '../lib/format';

export const MAX_KEY_WEIGHT = 1000;

/** Where a key stands for the gateway right now. */
export type KeyState = 'ok' | 'off' | 'authFailed' | 'cooling';

/** A rejected key (401 / 403) rests with a "401 …" reason (the gateway writes it); any other cooldown is a rate limit. */
export function keyState(k: ProviderAccount, now = Date.now()): KeyState {
  if (!k.enabled) return 'off';
  if (k.cooldownUntilUtc && new Date(k.cooldownUntilUtc).getTime() > now) {
    return /^40[13] /.test(k.lastError ?? '') ? 'authFailed' : 'cooling';
  }
  return 'ok';
}

const STATE_TONE: Record<KeyState, 'green' | 'neutral' | 'red' | 'orange'> = {
  ok: 'green',
  off: 'neutral',
  authFailed: 'red',
  cooling: 'orange',
};

const PROTOCOL_LABEL: Record<ApiProtocol, string> = {
  'openai-chat': 'Chat',
  'openai-responses': 'Responses',
  anthropic: 'Anthropic',
  gemini: 'Gemini',
};

/**
 * 一个提供商的多个 API Key。单 Key 的提供商保持原样（Key 存在提供商行里）；点「添加另一个 Key」后进入列表模式
 * （`settings.key_pool`）：Key 是 `api_key` 账号，沿用订阅账号的切换方式（手动 / 故障转移 / 均衡）、冷却和用量。
 */
export function ProviderKeyList({ provider }: { provider: Provider }) {
  const { t } = useI18n();
  const { toast, confirm } = useFeedback();
  const pool = provider.settings.key_pool === true;
  const keys = useProviderKeys(provider.id, pool);
  const usage = useProviderUsage(provider.id, pool);
  const policy = useSubscriptionPolicy(provider.id, pool);
  const updatePolicy = useUpdateSubscriptionPolicy(provider.id);
  const patch = usePatchProviderKey(provider.id);
  const remove = useRemoveProviderKey(provider.id);
  const reorder = useReorderProviderKeys(provider.id);
  const activate = useActivateProviderKey(provider.id);
  const [editing, setEditing] = useState<ProviderAccount | 'new' | null>(null);

  const list = keys.data ?? [];
  const onError = (e: unknown) => toast(errorText(e), 'error');
  const move = (i: number, delta: -1 | 1) => {
    const ids = list.map((k) => k.id);
    const j = i + delta;
    if (j < 0 || j >= ids.length) return;
    [ids[i], ids[j]] = [ids[j]!, ids[i]!];
    reorder.mutate(ids, { onError });
  };
  const askRemove = async (k: ProviderAccount) => {
    if (!(await confirm({ title: t('providers.keys.removeConfirm', { name: k.displayName }), destructive: true, confirmLabel: t('common.delete') }))) return;
    remove.mutate(k.id, { onError });
  };

  if (!pool) {
    return (
      <>
        <Group footer={t('providers.keys.footer')}>
          <div className="flex items-center justify-between gap-3 px-4 py-3">
            <div className="min-w-0 text-[13px]">{t('providers.keys.addAnother')}</div>
            <Button icon={<Plus className="size-3.5" />} onClick={() => setEditing('new')}>
              {t('providers.keys.add')}
            </Button>
          </div>
        </Group>
        <KeySheet provider={provider} editing={editing} onClose={() => setEditing(null)} />
      </>
    );
  }

  const usageOf = (id: string): ProviderAccountUsage | undefined => usage.data?.accounts.find((u) => u.accountId === id);
  return (
    <>
      <Group
        title={t('providers.keys.title')}
        footer={
          policy.data?.switchMode === 'balanced'
            ? t('providers.keys.modeBalanced')
            : policy.data?.switchMode === 'failover'
              ? t('providers.keys.modeFailover')
              : t('providers.keys.modeManual')
        }
      >
        <div className="flex flex-wrap items-center justify-between gap-3 px-4 py-3">
          {policy.data ? (
            <SwitchModePicker
              policy={policy.data}
              onChange={(switchMode) => updatePolicy.mutate({ switchMode }, { onError })}
            />
          ) : (
            <span />
          )}
          <Button variant="primary" icon={<Plus className="size-3.5" />} onClick={() => setEditing('new')}>
            {t('providers.keys.add')}
          </Button>
        </div>
        {keys.isError && <Alert tone="danger" title={t('providers.keys.error')}>{errorText(keys.error)}</Alert>}
        {list.map((k, i) => (
          <KeyRow
            key={k.id}
            k={k}
            first={i === 0}
            last={i === list.length - 1}
            usage={usageOf(k.id)}
            manual={policy.data?.switchMode === 'manual'}
            onToggle={(enabled) => patch.mutate({ id: k.id, enabled }, { onError })}
            onUp={() => move(i, -1)}
            onDown={() => move(i, 1)}
            onUse={() => activate.mutate(k.id, { onError })}
            onEdit={() => setEditing(k)}
            onDelete={() => void askRemove(k)}
          />
        ))}
      </Group>
      <KeySheet provider={provider} editing={editing} onClose={() => setEditing(null)} />
    </>
  );
}

function KeyRow({
  k, first, last, usage, manual, onToggle, onUp, onDown, onUse, onEdit, onDelete,
}: {
  k: ProviderAccount; first: boolean; last: boolean; usage?: ProviderAccountUsage; manual: boolean;
  onToggle: (enabled: boolean) => void; onUp: () => void; onDown: () => void; onUse: () => void; onEdit: () => void; onDelete: () => void;
}) {
  const { t, locale } = useI18n();
  const state = keyState(k);
  const weight = k.weight ?? 1;
  const stateText =
    state === 'off'
      ? t('providers.keys.state.off')
      : state === 'ok'
        ? t('providers.keys.state.ok')
        : t(state === 'authFailed' ? 'providers.keys.state.authFailed' : 'providers.keys.state.cooling', {
            when: formatDateTime(k.cooldownUntilUtc!, locale),
          });
  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-2 px-4 py-3">
      <div className="min-w-0 flex-1 basis-56">
        <div className="flex min-w-0 flex-wrap items-center gap-2">
          <Dot tone={STATE_TONE[state]} />
          <span className="truncate text-[13px] font-medium">{k.displayName}</span>
          {k.isCurrent && manual && <Badge tone="accent">{t('providers.keys.current')}</Badge>}
          {k.protocol && <Badge>{PROTOCOL_LABEL[k.protocol]}</Badge>}
          {weight > 1 && <Badge title={t('providers.keys.weightHint')}>×{weight}</Badge>}
          {k.httpProxy && <Badge>{t('providers.keys.proxyBadge')}</Badge>}
        </div>
        <div className="mt-0.5 flex flex-wrap items-center gap-x-3 text-[11px] text-[var(--text-muted)]">
          <span className="font-mono">{k.keyMasked ?? '••••'}</span>
          <span>{stateText}</span>
          {k.notes && <span className="truncate">{k.notes}</span>}
        </div>
      </div>
      <div className="w-44 shrink-0 text-[11px] tabular-nums text-[var(--text-secondary)]" aria-label={t('providers.usage.title')}>
        <div>
          {t('providers.usage.today')}: {formatTokens(usage?.today.requests ?? 0, true)} {t('providers.usage.requests')} ·{' '}
          {formatTokens(usage?.today.tokens ?? 0, true)} {t('providers.usage.tokens')}
        </div>
        <div className="text-[var(--text-muted)]">
          {t('providers.usage.total')}: {formatTokens(usage?.total.requests ?? 0, true)} · {formatUsd(usdFromNanos(usage?.total.costNanoUsd ?? 0), { compact: true })}
        </div>
      </div>
      <div className="flex shrink-0 items-center gap-1">
        {manual && !k.isCurrent && (
          <Button size="sm" icon={<KeyRound className="size-3.5" />} onClick={onUse}>
            {t('providers.keys.use')}
          </Button>
        )}
        <Button size="sm" aria-label={t('providers.keys.moveUp')} icon={<ArrowUp className="size-3.5" />} disabled={first} onClick={onUp} />
        <Button size="sm" aria-label={t('providers.keys.moveDown')} icon={<ArrowDown className="size-3.5" />} disabled={last} onClick={onDown} />
        <Button size="sm" aria-label={t('common.edit')} icon={<Pencil className="size-3.5" />} onClick={onEdit} />
        <Button size="sm" variant="destructive" aria-label={t('common.delete')} icon={<Trash2 className="size-3.5" />} onClick={onDelete} />
        <Switch checked={k.enabled} onChange={onToggle} label={t('providers.keys.enabled')} />
      </div>
    </div>
  );
}

/** Add (editing === 'new') or change one key: name, secret, protocol, weight, proxy, note. */
function KeySheet({ provider, editing, onClose }: { provider: Provider; editing: ProviderAccount | 'new' | null; onClose: () => void }) {
  const { t } = useI18n();
  return (
    <Sheet
      open={editing !== null}
      onOpenChange={(open) => !open && onClose()}
      title={t(editing === 'new' ? 'providers.keys.addTitle' : 'providers.keys.editTitle')}
    >
      {editing !== null && <KeyForm key={editing === 'new' ? 'new' : editing.id} provider={provider} editing={editing} onClose={onClose} />}
    </Sheet>
  );
}

function KeyForm({ provider, editing, onClose }: { provider: Provider; editing: ProviderAccount | 'new'; onClose: () => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const add = useAddProviderKey(provider.id);
  const patch = usePatchProviderKey(provider.id);
  const existing = editing === 'new' ? null : editing;
  const [name, setName] = useState(existing?.displayName ?? '');
  const [secret, setSecret] = useState('');
  const [protocol, setProtocol] = useState<ApiProtocol | ''>(existing?.protocol ?? '');
  const [weight, setWeight] = useState<number | null>(existing?.weight ?? 1);
  const [proxy, setProxy] = useState(existing?.httpProxy ?? '');
  const [notes, setNotes] = useState(existing?.notes ?? '');
  const weightOk = weight === null || (Number.isInteger(weight) && weight >= 1 && weight <= MAX_KEY_WEIGHT);
  const canSave = weightOk && (existing !== null || secret.trim().length > 0);
  const pending = add.isPending || patch.isPending;
  const protocols = provider.endpoints.map((e) => e.protocol);

  const save = () => {
    const done = { onSuccess: onClose, onError: (e: unknown) => toast(errorText(e), 'error') };
    if (existing === null) {
      add.mutate(
        {
          key: secret.trim(),
          ...(name.trim() ? { name: name.trim() } : {}),
          ...(protocol ? { protocol } : {}),
          ...(weight && weight > 1 ? { weight } : {}),
          ...(proxy.trim() ? { httpProxy: proxy.trim() } : {}),
          ...(notes.trim() ? { notes: notes.trim() } : {}),
        },
        done,
      );
    } else {
      patch.mutate(
        {
          id: existing.id,
          name: name.trim() || existing.displayName,
          ...(secret.trim() ? { key: secret.trim() } : {}),
          protocol,
          weight: weight ?? 1,
          httpProxy: proxy.trim(),
          notes: notes.trim(),
        },
        done,
      );
    }
  };

  return (
    <div className="flex flex-col gap-4">
      <Field label={t('providers.keys.name')}>
        <Input value={name} placeholder={t('providers.keys.namePlaceholder')} onChange={(e) => setName(e.target.value)} />
      </Field>
      <Field label={t('providers.apiKey')} hint={existing ? t('providers.keys.replaceHint', { mask: existing.keyMasked ?? '••••' }) : undefined}>
        <Input
          type="password"
          autoComplete="off"
          mono
          placeholder={existing ? t('providers.replaceKey') : 'sk-…'}
          value={secret}
          onChange={(e) => setSecret(e.target.value)}
        />
      </Field>
      <Field label={t('providers.keys.protocol')} hint={t('providers.keys.protocolHint')}>
        <Select<ApiProtocol | ''>
          value={protocol}
          onChange={setProtocol}
          options={[
            { value: '', label: t('providers.keys.protocolAll') },
            ...protocols.map((p) => ({ value: p, label: PROTOCOL_LABEL[p] })),
          ]}
        />
      </Field>
      <Field label={t('providers.keys.weight')} hint={t('providers.keys.weightHint')} error={weightOk ? undefined : t('providers.keys.weightInvalid')}>
        <NumberInput value={weight} min={1} step={1} onChange={setWeight} className="w-28" />
      </Field>
      <Field label={t('providers.keys.proxy')} hint={t('providers.keys.proxyHint')}>
        <Input mono value={proxy} placeholder="http://127.0.0.1:7890" spellCheck={false} onChange={(e) => setProxy(e.target.value)} />
      </Field>
      <Field label={t('providers.keys.notes')}>
        <TextArea rows={2} value={notes} onChange={(e) => setNotes(e.target.value)} />
      </Field>
      <div className="flex justify-end gap-2">
        <Button onClick={onClose}>{t('common.cancel')}</Button>
        <Button variant="primary" disabled={!canSave} loading={pending} onClick={save}>
          {t('common.save')}
        </Button>
      </div>
    </div>
  );
}
