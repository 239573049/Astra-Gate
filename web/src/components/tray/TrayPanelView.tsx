import {
  ArrowDownRight,
  ArrowUpRight,
  Ban,
  ChevronsUpDown,
  Copy,
  Ellipsis,
  Play,
  RefreshCw,
  RotateCcw,
  Settings as SettingsIcon,
  TriangleAlert,
  X,
} from 'lucide-react';
import { forwardRef, useEffect, useState, type ReactNode } from 'react';

import type { ClientInfo, ClientKind } from '../../api/types';
import { useI18n } from '../../i18n';
import { cn } from '../../lib/cn';
import { formatPercent, formatTokens } from '../../lib/format';
import {
  changeRatio,
  failedRequests,
  formatCostShort,
  hourlyRequests,
  quotaTone,
  resetsIn,
  topModelShare,
  updatedAgo,
  windowLength,
  yesterdayTokens,
  type QuotaPlan,
  type QuotaRow,
} from '../../lib/tray';
import { formatAmount } from '../../lib/quota';
import { cacheHitRatio } from '../../lib/usage';
import type { TrayPanelCommand, TrayPanelState, TrayPrefs } from '../../shell/bridge';
import { CLIENT_META, ClientGlyph, ProviderIcon } from '../icons';
import { Dot, Spinner } from '../ui/controls';
import type { TrayData } from './useTrayData';

export interface TrayPanelViewProps {
  prefs: TrayPrefs;
  state: TrayPanelState;
  data: TrayData;
  /** Header / footer actions; omitted in the Settings preview, where the buttons are inert. */
  onCommand?: (command: TrayPanelCommand) => void;
  onRefresh: () => void;
  onCopyAddress?: () => void;
  onSwitchClient?: (client: ClientInfo) => void;
  /** Settings preview: no window-close button semantics, no fixed height. */
  preview?: boolean;
  className?: string;
}

const TONE_COLOR = { green: 'var(--success)', orange: 'var(--warning)', red: 'var(--danger)' } as const;

/**
 * The tray panel (plan §9.1 menu-bar surface): service status, today's usage, subscription quota windows and
 * service controls. The same component renders in the tray popover window (/tray) and as the live preview on
 * Settings › Tray panel; `prefs.sections` decides which blocks appear.
 */
export const TrayPanelView = forwardRef<HTMLDivElement, TrayPanelViewProps>(function TrayPanelView(
  { prefs, state, data, onCommand, onRefresh, onCopyAddress, onSwitchClient, preview, className },
  ref,
) {
  const { t } = useI18n();
  const busy = state.activity !== null;
  const up = state.running && !busy;
  const command = (c: TrayPanelCommand) => onCommand?.(c);
  const address = data.settings?.gatewayBaseUrl.replace(/^https?:\/\//, '') ?? (state.port ? `127.0.0.1:${state.port}` : '');
  const statusLabel = state.activity ? t(`tray.status.${state.activity}`) : state.running ? t('tray.status.running') : t('tray.status.stopped');

  return (
    <div
      ref={ref}
      className={cn(
        'chrome flex max-h-full flex-col overflow-hidden rounded-[14px] border border-[var(--border-strong)] bg-[var(--surface)] text-[13px] text-[var(--foreground)]',
        className,
      )}
    >
      {/* Header */}
      <header data-tray-part="header" className="shrink-0 border-b border-[var(--border)] px-4 pt-3.5 pb-3">
        <div className="flex items-center gap-2">
          <Dot tone={busy ? 'orange' : state.running ? 'green' : 'red'} />
          <span className="min-w-0 flex-1 truncate text-[14px] font-semibold">{statusLabel}</span>
          <IconButton label={t('tray.copyAddress')} onClick={onCopyAddress} disabled={!address}>
            <Copy className="size-4" />
          </IconButton>
          <IconButton label={t('tray.settings')} onClick={() => command('open-settings')}>
            <SettingsIcon className="size-4" />
          </IconButton>
          <IconButton label={t('tray.more')} onClick={() => command('menu')}>
            <Ellipsis className="size-4" />
          </IconButton>
          {!preview && (
            <IconButton label={t('common.close')} onClick={() => command('hide')}>
              <X className="size-4" />
            </IconButton>
          )}
        </div>
        {address && <div className="selectable mt-0.5 truncate pl-4 font-mono text-[12px] text-[var(--text-secondary)]">{address}</div>}
        {(state.apiVersionMismatch || state.updateAvailable) && (
          <div className="mt-2 flex flex-wrap gap-1.5 pl-4">
            {state.apiVersionMismatch && (
              <Pill tone="orange">
                <TriangleAlert className="size-3" />
                {t('tray.mismatch')}
              </Pill>
            )}
            {state.updateAvailable && (
              <button type="button" onClick={() => command('check-updates')} className="cursor-default">
                <Pill tone="accent">{t('tray.update')}</Pill>
              </button>
            )}
          </div>
        )}
      </header>

      {/* Body */}
      <div className="scroll min-h-0 flex-1 overflow-y-auto">
        <div data-tray-part="body" className="flex flex-col gap-3 px-4 py-3.5">
          {!state.running && !busy ? (
            <Stopped onStart={() => command('start')} />
          ) : (
            <>
              <UsageBlock prefs={prefs} data={data} onRefresh={onRefresh} />
              {prefs.sections.quota && <QuotaBlock data={data} />}
              {prefs.sections.clients && <ClientsBlock data={data} disabled={!up} onSwitch={onSwitchClient} />}
            </>
          )}
        </div>
      </div>

      {/* Footer */}
      <footer data-tray-part="footer" className="flex shrink-0 items-center gap-1 border-t border-[var(--border)] px-3 py-2.5">
        {state.running ? (
          <>
            <IconButton label={t('tray.restart')} onClick={() => command('restart')} disabled={busy}>
              <RotateCcw className="size-4" />
            </IconButton>
            <IconButton label={t('tray.stop')} onClick={() => command('stop')} disabled={busy}>
              <Ban className="size-4" />
            </IconButton>
          </>
        ) : (
          <IconButton label={t('shell.startService')} onClick={() => command('start')} disabled={busy}>
            <Play className="size-4" />
          </IconButton>
        )}
        <div className="flex-1" />
        <button
          type="button"
          onClick={() => command('quit')}
          className="h-8 cursor-default rounded-[8px] px-2.5 text-[13px] text-[var(--text-secondary)] transition-colors hover:bg-[var(--surface-muted)] hover:text-[var(--foreground)]"
        >
          {t('tray.quit')}
        </button>
        <button
          type="button"
          onClick={() => command('open')}
          className="inline-flex h-8 cursor-default items-center gap-1.5 rounded-[8px] border border-[var(--border-strong)] px-3 text-[13px] font-medium transition-colors hover:bg-[var(--surface-muted)]"
        >
          {t('tray.open')}
          <ArrowUpRight className="size-3.5" />
        </button>
      </footer>
    </div>
  );
});

function IconButton({ label, onClick, disabled, children }: { label: string; onClick?: () => void; disabled?: boolean; children: ReactNode }) {
  return (
    <button
      type="button"
      title={label}
      aria-label={label}
      disabled={disabled}
      onClick={onClick}
      className="inline-flex size-8 shrink-0 cursor-default items-center justify-center rounded-[8px] text-[var(--text-secondary)] transition-colors hover:bg-[var(--surface-muted)] hover:text-[var(--foreground)] disabled:opacity-40 disabled:hover:bg-transparent"
    >
      {children}
    </button>
  );
}

function Pill({ tone, children, title }: { tone: 'red' | 'orange' | 'accent' | 'neutral'; children: ReactNode; title?: string }) {
  const color = { red: 'var(--danger)', orange: 'var(--warning)', accent: 'var(--accent)', neutral: 'var(--text-secondary)' }[tone];
  return (
    <span
      title={title}
      className="inline-flex max-w-full items-center gap-1 rounded-[6px] border px-1.5 py-px text-[11.5px] leading-[18px] whitespace-nowrap"
      style={{ color, borderColor: `color-mix(in oklab, ${color} 45%, transparent)`, background: `color-mix(in oklab, ${color} 10%, transparent)` }}
    >
      {children}
    </span>
  );
}

function Stopped({ onStart }: { onStart: () => void }) {
  const { t } = useI18n();
  return (
    <div className="flex flex-col items-center gap-2 py-6 text-center">
      <div className="text-[13px] font-medium">{t('tray.status.stopped')}</div>
      <div className="max-w-[260px] text-[12px] text-[var(--text-secondary)]">{t('tray.stoppedHint')}</div>
      <button
        type="button"
        onClick={onStart}
        className="mt-1 inline-flex h-8 cursor-default items-center gap-1.5 rounded-[8px] bg-[var(--accent)] px-3 text-[13px] font-medium text-[var(--on-accent)]"
      >
        <Play className="size-3.5" />
        {t('shell.startService')}
      </button>
    </div>
  );
}

/** Re-renders every 30s so "updated just now" / reset times stay current while the panel is open. */
function useTick(ms = 30_000): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), ms);
    return () => clearInterval(timer);
  }, [ms]);
  return now;
}

function UsageBlock({ prefs, data, onRefresh }: { prefs: TrayPrefs; data: TrayData; onRefresh: () => void }) {
  const { t, locale } = useI18n();
  const now = useTick();
  const s = data.summary;
  const sec = prefs.sections;
  const showTiles = sec.overview || sec.cost;
  const tokens = s ? s.inputTokens + s.outputTokens : 0;
  const failed = s ? failedRequests(s) : 0;
  const ago = updatedAgo(data.updatedAt, locale, t('tray.justNow'), now);
  const top = sec.topModel ? topModelShare(data.top, tokens) : null;
  const hit = sec.cacheHitRate && s ? cacheHitRatio(s.cacheReadTokens, s.inputTokens) : null;
  const delta = sec.compareYesterday && s ? changeRatio(tokens, yesterdayTokens(data.daily, new Date(now))) : null;
  const chips = sec.topModel || sec.cacheHitRate || sec.compareYesterday;
  if (!showTiles && !sec.chart && !chips) return null;

  return (
    <section className="flex flex-col gap-3">
      <div className="flex items-center gap-2">
        <span className="text-[13px] text-[var(--text-secondary)]">{t('tray.today')}</span>
        {sec.overview && failed > 0 && <Pill tone="red">{t('tray.failures', { count: failed })}</Pill>}
        <div className="flex-1" />
        {ago && <span className="text-[12px] text-[var(--text-muted)]">{t('tray.updated', { time: ago })}</span>}
        <IconButton label={t('common.refresh')} onClick={onRefresh}>
          <RefreshCw className="size-3.5" />
        </IconButton>
      </div>

      {!s ? (
        data.error ? <div className="text-[12px] text-[var(--danger)]">{t('tray.loadFailed')}</div> : <Spinner lines={2} />
      ) : (
        <>
          {showTiles && (
            <div className="grid auto-cols-fr grid-flow-col gap-px overflow-hidden rounded-[10px] bg-[var(--border)]">
              {sec.overview && <Tile label={t('tray.requests')} value={formatTokens(s.requests, true)} />}
              {sec.overview && <Tile label={t('tray.tokens')} value={formatTokens(tokens, true)} />}
              {sec.cost && <Tile label={t('tray.cost')} value={formatCostShort(s.costUsd)} />}
            </div>
          )}
          {sec.chart && <HourlyChart data={data} now={now} />}
          {chips && (
            <div className="flex flex-wrap gap-1.5">
              {top && (
                <Chip label={t('tray.topModel')} title={top.model}>
                  <span className="truncate">{top.model}</span>
                  <span className="shrink-0">· {formatPercent(top.share)}</span>
                </Chip>
              )}
              {sec.cacheHitRate && (
                <Chip label={t('tray.cacheHit')}>
                  <span>{hit == null ? '—' : formatPercent(hit)}</span>
                </Chip>
              )}
              {sec.compareYesterday && (
                <Chip label={t('tray.vsYesterday')}>
                  {delta == null ? (
                    <span>—</span>
                  ) : (
                    <span className="inline-flex items-center gap-0.5">
                      {delta >= 0 ? <ArrowUpRight className="size-3" /> : <ArrowDownRight className="size-3" />}
                      {formatPercent(Math.abs(delta))}
                    </span>
                  )}
                </Chip>
              )}
            </div>
          )}
        </>
      )}
    </section>
  );
}

function Tile({ label, value }: { label: string; value: string }) {
  return (
    <div className="min-w-0 bg-[var(--surface-muted)] px-3 py-2.5">
      <div className="truncate text-[12px] text-[var(--text-secondary)]">{label}</div>
      <div className="num mt-0.5 truncate text-[20px] leading-tight font-semibold" title={value}>
        {value}
      </div>
    </div>
  );
}

function Chip({ label, title, children }: { label: string; title?: string; children: ReactNode }) {
  return (
    <span
      title={title}
      className="inline-flex max-w-full min-w-0 items-center gap-1.5 rounded-[7px] border border-[var(--border)] px-2 py-0.5 text-[12px]"
    >
      <span className="shrink-0 text-[var(--text-secondary)]">{label}</span>
      <span className="flex min-w-0 items-center gap-1 font-medium">{children}</span>
    </span>
  );
}

function HourlyChart({ data, now }: { data: TrayData; now: number }) {
  const { t } = useI18n();
  const bars = hourlyRequests(data.hourly, new Date(now));
  const peak = Math.max(1, ...bars.map((b) => b.value));
  const currentHour = new Date(now).getHours();
  return (
    <div role="img" aria-label={t('tray.chartLabel')} className="flex h-16 items-end gap-[3px]">
      {bars.map((b, hour) => {
        const height = b.value > 0 ? Math.max(4, Math.round((b.value / peak) * 64)) : 2;
        return (
          <span
            key={b.bucket}
            title={t('tray.hourBar', { hour: `${String(hour).padStart(2, '0')}:00`, count: b.value })}
            className="flex-1 rounded-[2px]"
            style={{
              height,
              background:
                hour === currentHour
                  ? 'var(--accent)'
                  : b.value > 0
                    ? 'color-mix(in oklab, var(--foreground) 32%, transparent)'
                    : 'var(--border)',
              opacity: hour > currentHour ? 0.5 : 1,
            }}
          />
        );
      })}
    </div>
  );
}

function QuotaBlock({ data }: { data: TrayData }) {
  const { t } = useI18n();
  if (data.plans.length === 0 && !data.hasSubscriptions) return null;
  // Several accounts of one provider: tell them apart by account name.
  const perProvider = new Map<string, number>();
  for (const p of data.plans) perProvider.set(p.providerId, (perProvider.get(p.providerId) ?? 0) + 1);
  return (
    <section className="flex flex-col gap-2 border-t border-[var(--border)] pt-3">
      <div className="grid grid-cols-[1fr_48px_72px] items-center gap-2 text-[12px] text-[var(--text-secondary)]">
        <span>{t('tray.quota')}</span>
        <span className="text-right">{t('tray.quotaRemaining')}</span>
        <span className="text-right">{t('tray.quotaReset')}</span>
      </div>
      {data.plans.length === 0 ? (
        <div className="text-[12px] text-[var(--text-muted)]">{t('tray.quotaPending')}</div>
      ) : (
        data.plans.map((plan) => <QuotaPlanRows key={plan.accountId} plan={plan} showAccount={(perProvider.get(plan.providerId) ?? 0) > 1} />)
      )}
    </section>
  );
}

function QuotaPlanRows({ plan, showAccount }: { plan: QuotaPlan; showAccount: boolean }) {
  const { t, locale } = useI18n();
  const name = (row: QuotaRow) =>
    row.kind === 'balance'
      ? (row.label ?? t('tray.quotaBalance'))
      : row.kind === 'credits' || row.windowMinutes == null
        ? t('tray.quotaCredits')
        : windowLength(row.windowMinutes, locale);
  return (
    <div className="flex flex-col gap-1">
      <div className="flex min-w-0 items-center gap-2">
        <ProviderIcon name={plan.providerName} icon={plan.providerIcon} size={16} />
        <span className="truncate text-[13px] font-medium">{plan.providerName}</span>
        {showAccount && <span className="truncate text-[12px] text-[var(--text-muted)]">{plan.accountName}</span>}
      </div>
      {plan.rows.map((row, i) => {
        const remaining = row.remaining;
        const color = remaining === null ? undefined : TONE_COLOR[quotaTone(remaining)];
        return (
          <div key={`${row.kind}-${i}`} className="grid grid-cols-[52px_1fr_48px_72px] items-center gap-2 pl-6 text-[12.5px]">
            <span className="truncate text-[var(--text-secondary)]" title={name(row)}>{name(row)}</span>
            {remaining === null ? (
              <span />
            ) : (
              <span className="h-1.5 overflow-hidden rounded-full bg-[var(--surface-muted)]">
                <span className="block h-full rounded-full" style={{ width: `${remaining}%`, background: color }} />
              </span>
            )}
            {row.amount ? (
              // A balance has no reset time: its amount takes both right-hand columns.
              <span className="num col-span-2 truncate text-right font-medium">{formatAmount(row.amount.value, row.amount.unit, locale)}</span>
            ) : (
              <>
                <span className="num text-right font-medium">{remaining === null ? '—' : `${Math.round(remaining)}%`}</span>
                <span className="truncate text-right text-[var(--text-secondary)]">{resetsIn(row.resetsAt, locale) ?? '—'}</span>
              </>
            )}
          </div>
        );
      })}
    </div>
  );
}

function ClientsBlock({ data, disabled, onSwitch }: { data: TrayData; disabled: boolean; onSwitch?: (client: ClientInfo) => void }) {
  const { t } = useI18n();
  const clients = data.clients.filter((c) => c.enabled);
  if (clients.length === 0) return null;
  const providerName = (id?: string | null) => data.providers.find((p) => p.id === id)?.name ?? t('tray.unbound');
  return (
    <section className="flex flex-col gap-1 border-t border-[var(--border)] pt-3">
      <div className="mb-0.5 text-[12px] text-[var(--text-secondary)]">{t('tray.clients')}</div>
      {clients.map((c) => (
        <button
          key={c.kind}
          type="button"
          disabled={disabled || !onSwitch}
          onClick={() => onSwitch?.(c)}
          aria-label={t('tray.switchProvider', { client: c.name })}
          className="-mx-2 flex h-8 cursor-default items-center gap-2 rounded-[8px] px-2 text-left transition-colors enabled:hover:bg-[var(--surface-muted)]"
        >
          {c.kind in CLIENT_META && <ClientGlyph kind={c.kind as ClientKind} size={16} />}
          <span className="min-w-0 flex-1 truncate">{c.name}</span>
          <span className="max-w-[45%] truncate text-[12px] text-[var(--text-secondary)]">{providerName(c.providerId)}</span>
          {onSwitch && <ChevronsUpDown className="size-3.5 shrink-0 text-[var(--text-muted)]" />}
        </button>
      ))}
    </section>
  );
}
