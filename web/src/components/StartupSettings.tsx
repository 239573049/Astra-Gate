import { useEffect, useState } from 'react';

import { useAutostart, useSetAutostart } from '../api/hooks';
import { useI18n } from '../i18n';
import { getBridge, isMac, type DesktopBridge, type LoginItemState } from '../shell/bridge';
import { Group, Row, Spinner, Switch } from './ui/controls';
import { errorText, useFeedback } from './ui/overlays';

/**
 * Settings › General › Start at login. The service row adds or removes the login entry `astra autostart` manages —
 * a LaunchAgent, a systemd user unit or a Run key value, written by the server — so the gateway comes up at login
 * with no window; only the computer running Astra may change it. The desktop app adds a row for itself (the tray
 * menu's "Launch at Login"): opened at login in the tray, it brings the service up too.
 */
export function StartupSettings() {
  const { t } = useI18n();
  const autostart = useAutostart();
  const loginItem = getBridge()?.loginItem;
  const macOS = autostart.data?.mechanism === 'launchd' || (!!loginItem && isMac);

  return (
    <Group title={t('settings.startup')} footer={macOS ? t('settings.startup.macFooter') : undefined}>
      {loginItem && <AppLoginRow loginItem={loginItem} />}
      <ServiceRow />
    </Group>
  );
}

function AppLoginRow({ loginItem }: { loginItem: DesktopBridge['loginItem'] }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const [state, setState] = useState<LoginItemState | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let live = true;
    void loginItem.get().then((s) => {
      if (live) setState(s);
    });
    // The tray menu switches the same login item.
    const off = loginItem.onChanged(setState);
    return () => {
      live = false;
      off();
    };
  }, [loginItem]);

  const change = (enabled: boolean) => {
    setBusy(true);
    loginItem
      .set(enabled)
      .then((s) => {
        setState(s);
        if (s.enabled !== enabled) toast(t('settings.startup.failed'), 'error');
      })
      .catch((e) => toast(errorText(e), 'error'))
      .finally(() => setBusy(false));
  };

  return (
    <Row label={t('settings.startup.app')} detail={state && !state.supported ? t('settings.startup.appUnsupported') : t('settings.startup.appHint')}>
      {state ? (
        <Switch checked={state.enabled} disabled={!state.supported || busy} label={t('settings.startup.app')} onChange={change} />
      ) : (
        <Spinner className="size-3.5" />
      )}
    </Row>
  );
}

function ServiceRow() {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const autostart = useAutostart();
  const update = useSetAutostart();
  const s = autostart.data;

  if (!s) {
    return (
      <Row label={t('settings.startup.service')} detail={autostart.isError ? errorText(autostart.error) : undefined}>
        {autostart.isPending && <Spinner className="size-3.5" />}
      </Row>
    );
  }

  const detail =
    !s.supported || s.mechanism === 'none'
      ? t(`settings.startup.unsupported.${s.unsupported ?? 'platform'}`)
      : !s.canChange
        ? t('settings.startup.localOnly')
        : s.enabled && s.location
          ? t('settings.startup.entry', { location: s.location })
          : t(`settings.startup.service.${s.mechanism}`);

  return (
    <Row label={t('settings.startup.service')} detail={detail}>
      {s.supported && (
        <Switch
          checked={s.enabled}
          disabled={!s.canChange || update.isPending}
          label={t('settings.startup.service')}
          onChange={(enabled) =>
            update.mutate(enabled, {
              onSuccess: (r) => r.enabled && toast(t('settings.startup.saved')),
              onError: (e) => toast(errorText(e), 'error'),
            })
          }
        />
      )}
    </Row>
  );
}
