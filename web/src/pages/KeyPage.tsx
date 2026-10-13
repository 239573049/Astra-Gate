import { useCallback, useEffect, useState, type FormEvent } from 'react';

import { fetchKeyInfo, KeyInfoError } from '../api/hooks';
import type { KeyInfo, KeyUsage } from '../api/types';
import { Alert } from '../components/arc/alert/alert';
import { PasswordField } from '../components/arc/password-field/password-field';
import { AstraLogo } from '../components/AstraLogo';
import { CLIENT_META } from '../components/icons';
import { Badge, Button } from '../components/ui/controls';
import { useI18n } from '../i18n';
import { formatDateTime, formatTokens, formatUsd } from '../lib/format';

const STORAGE_KEY = 'astra.keyLookup';

function remembered(): string {
  try {
    return sessionStorage.getItem(STORAGE_KEY) ?? '';
  } catch {
    return '';
  }
}

function remember(key: string | null) {
  try {
    if (key) sessionStorage.setItem(STORAGE_KEY, key);
    else sessionStorage.removeItem(STORAGE_KEY);
  } catch {
    // storage unavailable (private mode): the page still works, it just forgets
  }
}

/**
 * /key — token self-service (plan: alerts-webhooks-token-limits §2.2): whoever holds a gateway token looks up its own
 * limits and usage with the token itself. Outside the admin login on purpose; it only calls /v1/astra/key, which shows
 * nothing upstream. The token stays in this tab's sessionStorage; a ?key= link fills it in and is cleared from the
 * address bar right away.
 */
export function KeyPage() {
  const { t } = useI18n();
  const [key, setKey] = useState('');
  const [info, setInfo] = useState<KeyInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  const lookUp = useCallback(
    async (value: string) => {
      const trimmed = value.trim();
      if (!trimmed) return;
      setLoading(true);
      setError(null);
      try {
        setInfo(await fetchKeyInfo(trimmed));
        remember(trimmed);
      } catch (e) {
        setInfo(null);
        remember(null);
        setError(e instanceof KeyInfoError && e.status === 401 ? t('key.invalid') : t('key.error', { error: (e as Error).message }));
      } finally {
        setLoading(false);
      }
    },
    [t],
  );

  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    const fromLink = params.get('key');
    if (fromLink) window.history.replaceState(null, '', window.location.pathname);
    const initial = fromLink ?? remembered();
    if (initial) {
      setKey(initial);
      void lookUp(initial);
    }
    // Once, on arrival.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    void lookUp(key);
  };

  return (
    <div className="wallpaper flex min-h-full justify-center overflow-y-auto p-4 sm:items-start sm:py-12">
      <div className="flex w-full max-w-[720px] flex-col gap-4">
        <div className="flex items-center gap-3">
          <AstraLogo className="size-10 rounded-[12px]" />
          <div>
            <h1 className="text-[17px] font-medium">{t('key.title')}</h1>
            <p className="text-[13px] text-[var(--text-secondary)]">{t('key.subtitle')}</p>
          </div>
        </div>

        {!info && (
          <form onSubmit={submit} className="card flex flex-col gap-4 p-5">
            <PasswordField label={t('key.title')} placeholder={t('key.placeholder')} autoFocus autoComplete="off" value={key}
              aria-invalid={error ? true : undefined} onChange={(e) => setKey(e.target.value)} />
            {error && <Alert tone="danger" title={error} />}
            <Button type="submit" variant="primary" loading={loading} disabled={!key.trim()}>
              {t('key.submit')}
            </Button>
          </form>
        )}

        {info && (
          <KeyReport
            info={info}
            onForget={() => {
              remember(null);
              setInfo(null);
              setKey('');
            }}
          />
        )}
      </div>
    </div>
  );
}

function KeyReport({ info, onForget }: { info: KeyInfo; onForget: () => void }) {
  const { t, locale } = useI18n();
  const clients =
    info.allowedClients == null
      ? t('key.clientsAny')
      : info.allowedClients.length === 0
        ? t('key.directOnly')
        : info.allowedClients.map((k) => CLIENT_META[k as keyof typeof CLIENT_META]?.name ?? k).join(', ');
  const usageRows: [string, KeyUsage][] = [
    [t('key.today'), info.usage.today],
    [t('key.week'), info.usage.week],
    [t('key.month'), info.usage.month],
    [t('key.total'), info.usage.total],
  ];
  const maxDaily = Math.max(...info.daily.map((d) => d.costUsd), 0);

  return (
    <>
      <section className="card flex flex-col gap-3 p-5">
        <div className="flex flex-wrap items-center gap-2">
          <h2 className="text-[15px] font-medium">{info.name}</h2>
          <code className="font-mono text-[12px] text-[var(--text-secondary)]">{info.keyPrefix}</code>
          {!info.enabled && <Badge tone="orange">{t('key.disabled')}</Badge>}
          <Button size="sm" variant="plain" className="ml-auto" onClick={onForget}>
            {t('key.forget')}
          </Button>
        </div>
        <div className="text-[12px] text-[var(--text-secondary)]">
          {t('key.clients')}: {clients} · {t('key.timeZone', { zone: info.timeZone })}
        </div>
      </section>

      <section className="card flex flex-col gap-3 p-5">
        <h3 className="text-[13px] font-medium">{t('key.limits')}</h3>
        {info.limits.length === 0 && info.rpm == null && <p className="text-[12px] text-[var(--text-muted)]">{t('key.unlimited')}</p>}
        <div className="grid gap-4 sm:grid-cols-2">
          {info.limits.map((l) => {
            const amount = (v: number) => (l.metric === 'cost' ? formatUsd(v) : formatTokens(v, true));
            const percent = Math.max(0, Math.min(100, Math.round(l.usedPercent)));
            const tone = percent >= 90 ? 'var(--danger)' : percent >= 70 ? 'var(--warning)' : 'var(--accent)';
            const label = `${t(`tokens.period.${l.period}`)} · ${t(`tokens.metric.${l.metric}`)}`;
            return (
              <div key={`${l.metric}:${l.period}`}>
                <div className="mb-1 flex items-baseline justify-between gap-2 text-[12px]">
                  <span className="text-[var(--text-secondary)]">{label}</span>
                  <span className="num">{amount(l.used)} / {amount(l.limit)}</span>
                </div>
                <div role="progressbar" aria-label={label} aria-valuenow={percent} aria-valuemin={0} aria-valuemax={100}
                  className="h-1.5 w-full overflow-hidden rounded-full bg-[var(--surface-muted)]">
                  <div className="h-full rounded-full" style={{ width: `${percent}%`, background: tone }} />
                </div>
                <div className="mt-1 text-[11px] text-[var(--text-muted)]">
                  {l.exhausted ? t('key.exhausted') : ''}
                  {l.exhausted && l.resetsAt ? ' · ' : ''}
                  {l.resetsAt ? t('key.resets', { time: formatDateTime(l.resetsAt, locale) }) : ''}
                </div>
              </div>
            );
          })}
        </div>
        {info.rpm && <p className="num text-[12px] text-[var(--text-secondary)]">{t('key.rpm', { used: info.rpm.used, limit: info.rpm.limit })}</p>}
      </section>

      <section className="card flex flex-col gap-3 p-5">
        <h3 className="text-[13px] font-medium">{t('key.usage')}</h3>
        <div className="overflow-x-auto">
          <table className="w-full text-[12px]">
            <thead>
              <tr className="text-[var(--text-muted)]">
                <th className="py-1 text-left font-normal" />
                <th className="py-1 text-right font-normal">{t('key.cost')}</th>
                <th className="py-1 text-right font-normal">{t('key.requests')}</th>
                <th className="py-1 text-right font-normal">{t('key.tokens')}</th>
              </tr>
            </thead>
            <tbody>
              {usageRows.map(([label, u]) => (
                <tr key={label}>
                  <th scope="row" className="py-1 text-left font-medium text-[var(--text-secondary)]">{label}</th>
                  <td className="num py-1 text-right font-semibold">{formatUsd(u.costUsd)}</td>
                  <td className="num py-1 text-right">{formatTokens(u.requests)}</td>
                  <td className="num py-1 text-right">{formatTokens(u.inputTokens + u.outputTokens, true)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <h4 className="mt-2 text-[12px] text-[var(--text-muted)]">{t('key.daily')}</h4>
        <div className="flex h-24 items-end gap-[2px]" role="list" aria-label={t('key.daily')}>
          {info.daily.map((d) => (
            <div key={d.day} role="listitem" title={`${d.day} · ${formatUsd(d.costUsd)} · ${formatTokens(d.requests)} ${t('key.requests')}`}
              className="flex-1 rounded-t-[2px] bg-[var(--accent)]"
              style={{ height: `${maxDaily > 0 ? Math.max(2, (d.costUsd / maxDaily) * 100) : 2}%`, opacity: d.costUsd > 0 ? 1 : 0.25 }} />
          ))}
        </div>
      </section>
    </>
  );
}
