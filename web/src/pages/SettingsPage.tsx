import { Copy, FolderOpen, LogOut, RefreshCw } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';

import { useAuthStatus, useCheckUpdate, useLogout, useSettings, useUpdateSettings, useUpdateStatus, useVersion } from '../api/hooks';
import type { Settings } from '../api/types';
import { Page } from '../components/layout/Page';
import { NumberField } from '../components/arc/number-field/number-field';
import { Button, Group, Row, Segmented, Spinner, Switch } from '../components/ui/controls';
import { errorText, Select, useFeedback } from '../components/ui/overlays';
import { useI18n, type Locale } from '../i18n';
import { ACCENT_PRESETS, useAppearance } from '../shell/appearance';
import { getBridge, isDesktop } from '../shell/bridge';
import { systemActions } from '../shell/systemActions';

export function SettingsPage() {
  const { t, locale, setLocale } = useI18n();
  const appearance = useAppearance();
  const settings = useSettings();
  const update = useUpdateSettings();
  const version = useVersion();
  const auth = useAuthStatus();
  const logout = useLogout();
  const { toast } = useFeedback();
  const s = settings.data;

  const patch = (p: Partial<Settings>) => update.mutate(p, { onError: (e) => toast(errorText(e), 'error') });

  const changeLocale = (l: Locale) => {
    setLocale(l);
    patch({ locale: l }); // also the language of stored billing descriptions
  };

  const copy = async (text: string) => {
    if (await systemActions.copy(text)) toast(t('common.copied'));
  };

  return (
    <Page title={t('nav.settings')}>
      <div className="mx-auto max-w-[680px]">
        <Group title={t('settings.appearance')}>
          <Row label={t('settings.theme')}>
            <Segmented
              ariaLabel={t('settings.theme')}
              value={appearance.theme}
              onChange={appearance.setTheme}
              items={[
                { value: 'system', label: t('settings.theme.system') },
                { value: 'light', label: t('settings.theme.light') },
                { value: 'dark', label: t('settings.theme.dark') },
              ]}
            />
          </Row>
          <Row
            label={t('settings.accent')}
            detail={
              appearance.accentChoice === 'system'
                ? appearance.systemAccent
                  ? t('settings.accent.followingSystem')
                  : t('settings.accent.systemUnavailable')
                : undefined
            }
          >
            <Select
              className="w-40"
              ariaLabel={t('settings.accent')}
              value={appearance.accentChoice}
              onChange={appearance.setAccentChoice}
              options={[
                { value: 'system', label: t('settings.accent.system') },
                ...ACCENT_PRESETS.map((p) => ({ value: p.color, label: t(`settings.accent.${p.id}`) })),
              ]}
            />
          </Row>
          <Row label={t('settings.language')} detail={t('settings.languageHint')}>
            <Segmented
              ariaLabel={t('settings.language')}
              value={locale}
              onChange={changeLocale}
              items={[
                { value: 'zh', label: '中文' },
                { value: 'en', label: 'English' },
              ]}
            />
          </Row>
        </Group>

        {!s ? (
          settings.isError ? <div className="text-[12px] text-[var(--danger)]">{errorText(settings.error)}</div> : <Spinner className="mx-auto my-8" />
        ) : (
          <>
            <Group title={t('settings.gateway')} footer={t('settings.gatewayFooter')}>
              <Row label={t('settings.gatewayUrl')} detail={`${s.gatewayBaseUrl}/v1`}>
                <Button size="sm" variant="plain" icon={<Copy className="size-3.5" />} aria-label={t('common.copy')} onClick={() => void copy(`${s.gatewayBaseUrl}/v1`)} />
              </Row>
              <Row label={t('settings.listen')} detail={`${s.host}:${s.port}`} />
              <Row label={t('settings.dataDir')} detail={s.dataDir}>
                {systemActions.canOpenPaths ? (
                  <Button size="sm" variant="plain" icon={<FolderOpen className="size-3.5" />} aria-label={t('settings.openFolder')} onClick={() => void systemActions.openPath(s.dataDir)} />
                ) : (
                  <Button size="sm" variant="plain" icon={<Copy className="size-3.5" />} aria-label={t('common.copy')} onClick={() => void copy(s.dataDir)} />
                )}
              </Row>
              <Row label={t('settings.streamIdle')} detail={t('settings.streamIdleHint')}>
                <DeferredNumber label={t('settings.streamIdle')} value={s.streamIdleTimeoutSec} onCommit={(v) => patch({ streamIdleTimeoutSec: v ?? 300 })} suffix=" s" />
              </Row>
            </Group>

            <Group title={t('settings.logging')} footer={t('settings.debugBodiesHint')}>
              <Row label={t('settings.debugBodies')}>
                <Switch checked={s.debugBodies} label={t('settings.debugBodies')} onChange={(debugBodies) => patch({ debugBodies })} />
              </Row>
              <Row label={t('settings.bodyRetention')}>
                <DeferredNumber label={t('settings.bodyRetention')} value={s.bodyRetentionDays} onCommit={(v) => patch({ bodyRetentionDays: v ?? 7 })} suffix={` ${t('settings.days')}`} />
              </Row>
              <Row label={t('settings.requestRetention')} detail={s.requestRetentionDays == null ? t('settings.keepForever') : undefined}>
                <Switch
                  checked={s.requestRetentionDays != null}
                  label={t('settings.requestRetention')}
                  onChange={(on) => patch({ requestRetentionDays: on ? 90 : null })}
                />
                {s.requestRetentionDays != null && (
                  <DeferredNumber label={t('settings.requestRetention')} value={s.requestRetentionDays} onCommit={(v) => patch({ requestRetentionDays: v ?? 90 })} suffix={` ${t('settings.days')}`} />
                )}
              </Row>
            </Group>

            <Group title={t('settings.effort')} footer={t('settings.effortHint')}>
              {(['low', 'medium', 'high'] as const).map((k) => (
                <Row key={k} label={t(`settings.effort.${k}`)}>
                  <DeferredNumber
                    label={t(`settings.effort.${k}`)}
                    value={s.effortBudgets[k]}
                    onCommit={(v) => patch({ effortBudgets: { ...s.effortBudgets, [k]: v ?? s.effortBudgets[k] } })}
                    suffix=" tokens"
                    step={1024}
                  />
                </Row>
              ))}
            </Group>
          </>
        )}

        <Group title={t('settings.about')}>
          <Row label={t('settings.serverVersion')} detail={version.data ? `${version.data.version} · API ${version.data.apiVersion}` : '—'} />
          {isDesktop && <Row label={t('settings.desktopVersion')} detail={getBridge()?.version || '—'} />}
          <UpdateRows />
          {isDesktop && (
            <Row label={t('settings.logs')} onClick={() => void systemActions.revealLogs()}>
              <FolderOpen className="size-4 text-[var(--text-secondary)]" />
            </Row>
          )}
          {auth.data?.required && (
            <Row label={t('settings.logout')} onClick={() => logout.mutate()}>
              <LogOut className="size-4 text-[var(--text-secondary)]" />
            </Row>
          )}
        </Group>
      </div>
    </Page>
  );
}

/**
 * Update feed rows: auto-check toggle plus a manual check button. The web UI
 * never installs anything itself — applies happen in the desktop app (tray)
 * or via `astra update`, so the hint adapts to where the UI is running.
 */
function UpdateRows() {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const update = useUpdateSettings();
  const status = useUpdateStatus();
  const check = useCheckUpdate();
  const s = status.data;

  const runCheck = () =>
    check.mutate(undefined, {
      onSuccess: (r) => toast(r.available ? t('settings.updateAvailable', { version: r.available }) : t('settings.upToDate')),
      onError: (e) => toast(errorText(e), 'error'),
    });

  const lastChecked = s?.lastCheckAt ? new Date(s.lastCheckAt).toLocaleString() : undefined;

  return (
    <>
      <Row
        label={t('settings.autoCheck')}
        detail={s && !s.feedConfigured ? t('settings.updateCheckFailed', { error: 'feed-not-configured' }) : undefined}
      >
        <Switch
          checked={s?.autoCheck ?? true}
          label={t('settings.autoCheck')}
          onChange={(autoCheck) => update.mutate({ updateAutoCheck: autoCheck }, { onError: (e) => toast(errorText(e), 'error') })}
        />
      </Row>
      <Row
        label={t('settings.checkUpdates')}
        detail={
          check.isError
            ? t('settings.updateCheckFailed', { error: errorText(check.error) })
            : s?.available
              ? t('settings.updateAvailable', { version: s.available })
              : lastChecked
                ? t('settings.lastChecked', { time: lastChecked })
                : undefined
        }
      >
        <Button size="sm" variant="plain" disabled={check.isPending} onClick={runCheck}>
          {check.isPending ? <Spinner className="size-3.5" /> : <RefreshCw className="size-3.5" />}
        </Button>
      </Row>
    </>
  );
}

/** Arc number field that saves shortly after the value settles (avoids a PATCH per keystroke or step). */
function DeferredNumber({ label, value, onCommit, suffix, step = 1 }: { label: string; value: number; onCommit: (v: number) => void; suffix?: string; step?: number }) {
  const [v, setV] = useState(value);
  useEffect(() => setV(value), [value]);
  const commit = useRef(onCommit);
  useEffect(() => {
    commit.current = onCommit;
  });
  useEffect(() => {
    if (v === value) return;
    const timer = setTimeout(() => commit.current(v), 600);
    return () => clearTimeout(timer);
  }, [v, value]);
  return (
    <div className="astra-label-hidden w-36">
      <NumberField label={label} size="sm" value={v} onValueChange={setV} min={0} step={step} suffix={suffix} />
    </div>
  );
}
