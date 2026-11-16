import { Flame, Plus, X } from 'lucide-react';
import { useState } from 'react';

import { usePatchWarmupAccount, useUpdateWarmupSettings, useWarmNow, useWarmupAccounts, useWarmupSettings } from '../api/hooks';
import type { WarmupAccount, WarmupMode } from '../api/types';
import { useI18n, type MessageKey } from '../i18n';
import { formatDateTime } from '../lib/format';
import { Badge, Button, Group, Input, Row, Segmented, Spinner, Switch } from './ui/controls';
import { errorText, useFeedback } from './ui/overlays';

const DAYS = [1, 2, 3, 4, 5, 6, 0];
const TIME = /^([01]\d|2[0-3]):[0-5]\d$/;

/**
 * Settings › General › Window warm-up (linked update 36, magpie's daily warm-up): at the times of day (and days of the week) the user picks, or
 * right after a window resets, each Claude and ChatGPT (Codex) subscription account gets the cheapest request, so its 5-hour window starts then.
 * The global switch is off by default; each account follows it or has its own mode and times.
 */
export function WarmupSettings() {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const settings = useWarmupSettings();
  const update = useUpdateWarmupSettings();
  const [time, setTime] = useState('');
  const w = settings.data;
  if (!w) return settings.isError ? <div className="text-[12px] text-[var(--danger)]">{errorText(settings.error)}</div> : <Spinner className="mx-auto my-8" />;

  const save = (body: Parameters<typeof update.mutate>[0]) =>
    update.mutate(body, { onSuccess: () => toast(t('warmup.saved'), 'success'), onError: (e) => toast(errorText(e), 'error') });
  const addTime = () => {
    if (!TIME.test(time) || w.times.includes(time)) return;
    save({ times: [...w.times, time].sort() });
    setTime('');
  };
  const toggleDay = (d: number) => save({ weekdays: w.weekdays.includes(d) ? w.weekdays.filter((x) => x !== d) : [...w.weekdays, d].sort() });

  return (
    <>
      <Group title={t('warmup.title')} footer={t('warmup.hint')}>
        <Row label={t('warmup.enabled')} icon={<Flame className="size-4" />}>
          <Switch checked={w.enabled} disabled={update.isPending} label={t('warmup.enabled')} onChange={(enabled) => save({ enabled })} />
        </Row>
        <Row label={t('warmup.mode')}>
          <Segmented<WarmupMode>
            ariaLabel={t('warmup.mode')}
            value={w.mode}
            onChange={(mode) => save({ mode })}
            items={[
              { value: 'daily', label: t('warmup.mode.daily') },
              { value: 'auto', label: t('warmup.mode.auto') },
            ]}
          />
        </Row>
        {w.mode === 'daily' && (
          <>
            <Row label={t('warmup.times')} detail={t('warmup.zone', { zone: w.timeZone })}>
              <div className="flex flex-wrap items-center gap-1.5">
                {w.times.map((x) => (
                  <Badge key={x} tone="neutral">
                    <span className="inline-flex items-center gap-1 font-mono">
                      {x}
                      <button type="button" aria-label={t('warmup.removeTime', { time: x })} onClick={() => save({ times: w.times.filter((y) => y !== x) })}>
                        <X className="size-3" />
                      </button>
                    </span>
                  </Badge>
                ))}
                <Input mono className="w-20" placeholder="06:00" value={time} onChange={(e) => setTime(e.target.value)} onKeyDown={(e) => e.key === 'Enter' && addTime()} />
                <Button size="sm" icon={<Plus className="size-3.5" />} disabled={!TIME.test(time)} onClick={addTime}>
                  {t('warmup.addTime')}
                </Button>
              </div>
            </Row>
            <Row label={t('warmup.weekdays')} detail={w.weekdays.length === 0 ? t('warmup.everyDay') : undefined}>
              <div role="group" aria-label={t('warmup.weekdays')} className="flex gap-1">
                {DAYS.map((d) => (
                  <button
                    key={d}
                    type="button"
                    aria-pressed={w.weekdays.length === 0 || w.weekdays.includes(d)}
                    onClick={() => toggleDay(d)}
                    className={`rounded-full px-2.5 py-0.5 text-[11.5px] ${w.weekdays.length === 0 || w.weekdays.includes(d) ? 'bg-[var(--fill)] text-[var(--text)]' : 'text-[var(--text-muted)]'}`}
                  >
                    {t(`warmup.day.${d}` as MessageKey)}
                  </button>
                ))}
              </div>
            </Row>
          </>
        )}
        <Row label={t('warmup.claude')}>
          <Switch checked={w.claude} disabled={update.isPending} label={t('warmup.claude')} onChange={(claude) => save({ claude })} />
        </Row>
        <Row label={t('warmup.codex')}>
          <Switch checked={w.codex} disabled={update.isPending} label={t('warmup.codex')} onChange={(codex) => save({ codex })} />
        </Row>
      </Group>
      <WarmupAccounts />
    </>
  );
}

function WarmupAccounts() {
  const { t, locale } = useI18n();
  const { toast } = useFeedback();
  const accounts = useWarmupAccounts();
  const patch = usePatchWarmupAccount();
  const warm = useWarmNow();
  const list = accounts.data ?? [];

  const change = (a: WarmupAccount, body: { mode?: string; times?: string[] }) =>
    patch.mutate({ id: a.accountId, ...body }, { onError: (e) => toast(errorText(e), 'error') });
  const run = (a: WarmupAccount) =>
    warm.mutate(a.accountId, {
      onSuccess: (r) => toast(t('warmup.ran', { name: a.accountName, result: t(`warmup.result.${r.result}` as MessageKey) }) + (r.message ? ` — ${r.message}` : ''), r.result === 'ok' ? 'success' : 'error'),
      onError: (e) => toast(errorText(e), 'error'),
    });

  return (
    <Group title={t('warmup.accounts')}>
      {list.length === 0 && <div className="px-4 py-3 text-[12px] text-[var(--text-muted)]">{t('warmup.accounts.empty')}</div>}
      {list.map((a) => (
        <Row
          key={a.accountId}
          label={`${a.providerName} · ${a.accountName}`}
          detail={
            <span className="block whitespace-normal">
              {a.lastAt && a.lastResult ? t('warmup.last', { result: t(`warmup.result.${a.lastResult}` as MessageKey), when: formatDateTime(a.lastAt, locale) }) : null}
              {a.lastMessage ? ` — ${a.lastMessage}` : ''}
              {a.nextAt ? ` ${t('warmup.next', { when: formatDateTime(a.nextAt, locale) })}` : ''}
              {!a.warmed ? ` ${t('warmup.notWarmed')}` : ''}
            </span>
          }
        >
          <Segmented<string>
            ariaLabel={t('warmup.mode')}
            value={a.mode}
            onChange={(mode) => change(a, { mode })}
            items={[
              { value: 'inherit', label: t('warmup.mode.inherit') },
              { value: 'daily', label: t('warmup.mode.daily') },
              { value: 'auto', label: t('warmup.mode.auto') },
              { value: 'off', label: t('warmup.mode.off') },
            ]}
          />
          <Button size="sm" icon={<Flame className="size-3.5" />} disabled={warm.isPending} onClick={() => run(a)}>
            {t('warmup.runNow')}
          </Button>
        </Row>
      ))}
    </Group>
  );
}
