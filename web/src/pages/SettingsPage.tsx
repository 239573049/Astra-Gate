import { Copy, FolderOpen, LogOut, PanelTop, RefreshCw, SlidersHorizontal } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useNavigate, useParams } from 'react-router';
import { useQueryClient } from '@tanstack/react-query';

import { keys, useAuthStatus, useCheckUpdate, useLogout, useSettings, useUpdateSettings, useUpdateStatus, useVersion } from '../api/hooks';
import type { ProxyMode, Settings } from '../api/types';
import { Page } from '../components/layout/Page';
import { NumberField } from '../components/arc/number-field/number-field';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '../components/arc/tabs/tabs';
import { TraySettings } from '../components/tray/TraySettings';
import { hasTrayPanel } from '../components/tray/useTrayBridge';
import { Button, Group, Input, Row, Segmented, Spinner, Switch } from '../components/ui/controls';
import { errorText, Select, useFeedback } from '../components/ui/overlays';
import { useI18n, type Locale } from '../i18n';
import { ACCENT_PRESETS, useAppearance } from '../shell/appearance';
import { getBridge, isDesktop } from '../shell/bridge';
import { systemActions } from '../shell/systemActions';

const TABS = ['general', 'tray'] as const;
type SettingsTab = (typeof TABS)[number];

/** Settings: General, plus Tray panel in the desktop app (/settings/:tab). */
export function SettingsPage() {
  const { t } = useI18n();
  const { tab } = useParams();
  const navigate = useNavigate();
  const trayAvailable = hasTrayPanel();
  const active: SettingsTab = trayAvailable && TABS.includes(tab as SettingsTab) ? (tab as SettingsTab) : 'general';

  if (!trayAvailable) {
    return (
      <Page title={t('nav.settings')}>
        <GeneralSettings />
      </Page>
    );
  }
  return (
    <Page title={t('nav.settings')}>
      <Tabs value={active} onValueChange={(v) => navigate(v === 'general' ? '/settings' : `/settings/${v}`, { replace: true })}>
        <TabsList aria-label={t('nav.settings')}>
          <TabsTrigger value="general">
            <span className="inline-flex items-center gap-1.5">
              <SlidersHorizontal className="size-3.5" />
              {t('settings.tab.general')}
            </span>
          </TabsTrigger>
          <TabsTrigger value="tray">
            <span className="inline-flex items-center gap-1.5">
              <PanelTop className="size-3.5" />
              {t('settings.tab.tray')}
            </span>
          </TabsTrigger>
        </TabsList>
        <TabsContent value="general" className="pt-5">
          <GeneralSettings />
        </TabsContent>
        <TabsContent value="tray" className="pt-5">
          <TraySettings />
        </TabsContent>
      </Tabs>
    </Page>
  );
}

function GeneralSettings() {
  const { t, locale, setLocale } = useI18n();
  const appearance = useAppearance();
  const settings = useSettings();
  const update = useUpdateSettings();
  const qc = useQueryClient();
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

          <ProxySettings settings={s} />

          <Group title={t('settings.quota')} footer={t('settings.quota.autoIntervalHint')}>
            <Row label={t('settings.quota.autoInterval')}>
              <DeferredNumber
                label={t('settings.quota.autoInterval')}
                value={s.quotaAutoIntervalMinutes}
                // Providers show their effective interval: refresh them after the global one changes.
                onCommit={(v) => update.mutate({ quotaAutoIntervalMinutes: v ?? 30 }, { onSuccess: () => void qc.invalidateQueries({ queryKey: keys.providers }), onError: (e) => toast(errorText(e), 'error') })}
                suffix={` ${t('settings.minutes')}`}
              />
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

/**
 * Outbound proxy for everything the server fetches (upstreams, logins, quota, updates, client installs).
 * System / direct apply at once; custom needs a URL first, so its fields save together with an explicit button.
 */
function ProxySettings({ settings: s }: { settings: Settings }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const update = useUpdateSettings();
  // Custom is only stored once it has a URL: until then it is a local choice showing the form.
  const [mode, setMode] = useState<ProxyMode>(s.proxyMode);
  const [form, setForm] = useState({ url: s.proxyUrl ?? '', username: s.proxyUsername ?? '', password: '', bypass: s.proxyBypass ?? '' });
  useEffect(() => setMode(s.proxyMode), [s.proxyMode]);
  useEffect(
    () => setForm({ url: s.proxyUrl ?? '', username: s.proxyUsername ?? '', password: '', bypass: s.proxyBypass ?? '' }),
    [s.proxyUrl, s.proxyUsername, s.proxyBypass, s.hasProxyPassword],
  );

  const changeMode = (m: ProxyMode) => {
    setMode(m);
    if (m !== 'custom' || s.proxyUrl) update.mutate({ proxyMode: m }, { onError: (e) => toast(errorText(e), 'error') });
  };

  const save = () =>
    update.mutate(
      {
        proxyMode: 'custom',
        proxyUrl: form.url.trim() || null,
        proxyUsername: form.username.trim() || null,
        proxyBypass: form.bypass.trim() || null,
        // Blank keeps the stored password; clearing the username clears it too.
        ...(form.password ? { proxyPassword: form.password } : {}),
      },
      { onSuccess: () => toast(t('settings.proxy.saved')), onError: (e) => toast(errorText(e), 'error') },
    );

  const dirty =
    mode !== s.proxyMode ||
    form.url !== (s.proxyUrl ?? '') ||
    form.username !== (s.proxyUsername ?? '') ||
    form.password !== '' ||
    form.bypass !== (s.proxyBypass ?? '');

  return (
    <Group title={t('settings.proxy')} footer={t('settings.proxy.footer')}>
      <Row label={t('settings.proxy.mode')} detail={t(`settings.proxy.mode.${mode}.hint`)}>
        <Segmented
          ariaLabel={t('settings.proxy.mode')}
          value={mode}
          onChange={changeMode}
          items={[
            { value: 'system', label: t('settings.proxy.mode.system') },
            { value: 'custom', label: t('settings.proxy.mode.custom') },
            { value: 'direct', label: t('settings.proxy.mode.direct') },
          ]}
        />
      </Row>
      {mode === 'custom' && (
        <>
          <Row label={t('settings.proxy.url')} detail={t('settings.proxy.urlHint')}>
            <Input className="w-64" mono aria-label={t('settings.proxy.url')} placeholder="http://127.0.0.1:7890" value={form.url} onChange={(e) => setForm({ ...form, url: e.target.value })} />
          </Row>
          <Row label={t('settings.proxy.username')} detail={t('settings.proxy.optional')}>
            <Input className="w-64" autoComplete="off" aria-label={t('settings.proxy.username')} value={form.username} onChange={(e) => setForm({ ...form, username: e.target.value })} />
          </Row>
          <Row label={t('settings.proxy.password')} detail={s.hasProxyPassword ? t('settings.proxy.passwordSaved') : t('settings.proxy.optional')}>
            <Input
              className="w-64"
              type="password"
              autoComplete="new-password"
              aria-label={t('settings.proxy.password')}
              placeholder={s.hasProxyPassword ? '••••••••' : undefined}
              value={form.password}
              onChange={(e) => setForm({ ...form, password: e.target.value })}
            />
          </Row>
          <Row label={t('settings.proxy.bypass')} detail={t('settings.proxy.bypassHint')}>
            <Input className="w-64" mono aria-label={t('settings.proxy.bypass')} placeholder="example.com, *.lan" value={form.bypass} onChange={(e) => setForm({ ...form, bypass: e.target.value })} />
          </Row>
          <div className="flex justify-end px-4 py-2">
            <Button size="sm" disabled={!dirty || !form.url.trim() || update.isPending} onClick={save}>
              {t('common.save')}
            </Button>
          </div>
        </>
      )}
    </Group>
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
