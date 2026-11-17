import { Download, RefreshCw } from 'lucide-react';
import { useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';

import { keys, useApplyContainerUpdate, useCheckUpdate, useSettings, useUpdateSettings, useUpdateStatus } from '../api/hooks';
import { Button, Row, Spinner, Switch } from './ui/controls';
import { errorText, useFeedback } from './ui/overlays';
import { useI18n } from '../i18n';
import { getBridge } from '../shell/bridge';

/**
 * Update feed rows: auto-check and preview-channel toggles plus a manual check button. In the
 * desktop app an available update gets an apply button that runs the shared
 * update engine over the bridge (server first, then the app per track); the
 * plain web UI never installs anything itself and points at the tray menu
 * / `astra update` instead — except in Docker with a container updater, where the server
 * asks the updater to pull the new image (and can do so on its own: "install automatically").
 */
export function UpdateRows() {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const qc = useQueryClient();
  const update = useUpdateSettings();
  const settings = useSettings();
  const status = useUpdateStatus();
  const check = useCheckUpdate();
  const s = status.data;
  // The saved setting, not the status: the status only changes once a feed check returns (up to 15 s).
  const channel = settings.data?.updateChannel ?? s?.channel;
  const updates = getBridge()?.updates ?? null;
  const [applying, setApplying] = useState(false);
  const containerApply = useApplyContainerUpdate();
  const container = !updates && s?.containerUpdater === true;

  const runContainerApply = () =>
    containerApply.mutate(undefined, {
      // An unreachable or refusing updater still answers 200, with the reason in applyError.
      onSuccess: (r) =>
        r.applyError
          ? toast(t('settings.updateContainerFailed', { error: r.applyError }), 'error')
          : toast(t('settings.updateContainerRequested')),
      onError: (e) => toast(errorText(e), 'error'),
    });

  const runCheck = () =>
    check.mutate(undefined, {
      // A failed feed fetch still answers 200 with the previous result plus `error`: say it failed.
      onSuccess: (r) =>
        r.error
          ? toast(t('settings.updateCheckFailed', { error: r.error }), 'error')
          : toast(r.available ? t('settings.updateAvailable', { version: r.available }) : t('settings.upToDate')),
      onError: (e) => toast(errorText(e), 'error'),
    });

  const runApply = () => {
    if (!updates || applying) return;
    setApplying(true);
    updates
      .apply()
      .then((outcome) => {
        if (outcome === 'server') {
          toast(t('settings.updateApplied'));
          void qc.invalidateQueries({ queryKey: keys.version });
          void qc.invalidateQueries({ queryKey: keys.update });
        } else if (outcome === 'desktop') {
          // The desktop step relaunches the app; say so before it quits.
          toast(t('settings.updateDesktopRelaunch'));
        } else if (outcome === 'download') {
          toast(t('settings.updateDownloadOpened'));
        } else if (outcome === 'downloading') {
          toast(t('settings.updateDownloading'));
        } else {
          toast(t('settings.upToDate'));
        }
      })
      .catch((e) => toast(errorText(e), 'error'))
      .finally(() => setApplying(false));
  };

  const lastChecked = s?.lastCheckAt ? new Date(s.lastCheckAt).toLocaleString() : undefined;
  const pending =
    container && s?.available && s.applyVersion === s.available && s.applyRequestedAt
      ? s.applyError
        ? t('settings.updateContainerFailed', { error: s.applyError })
        : t('settings.updateContainerPending', { version: s.available, time: new Date(s.applyRequestedAt).toLocaleString() })
      : undefined;
  const availableDetail = s?.available
    ? pending ??
      (updates
        ? t('settings.updateAvailable', { version: s.available })
        : `${t('settings.updateAvailable', { version: s.available })} ${t(container ? 'settings.updateContainerHint' : 'settings.updateApplyHint')}`)
    : undefined;

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
      {container && (
        <Row label={t('settings.autoApply')} detail={t('settings.autoApplyHint')}>
          <Switch
            checked={s?.autoApply ?? true}
            label={t('settings.autoApply')}
            disabled={update.isPending}
            // The setting is not part of the settings DTO: the update status reports it.
            onChange={(autoApply) =>
              update.mutate(
                { updateAutoApply: autoApply },
                { onSuccess: () => void qc.invalidateQueries({ queryKey: keys.update }), onError: (e) => toast(errorText(e), 'error') },
              )
            }
          />
        </Row>
      )}
      <Row label={t('settings.previewUpdates')} detail={t('settings.previewUpdatesHint')}>
        <Switch
          checked={channel === 'preview'}
          label={t('settings.previewUpdates')}
          disabled={!channel || update.isPending}
          // Flips as soon as the setting is saved; the check for the new channel runs behind it
          // (the check row shows its spinner).
          onChange={(on) =>
            update.mutate({ updateChannel: on ? 'preview' : 'stable' }, { onSuccess: runCheck, onError: (e) => toast(errorText(e), 'error') })
          }
        />
      </Row>
      <Row
        label={t('settings.checkUpdates')}
        detail={
          check.isError
            ? t('settings.updateCheckFailed', { error: errorText(check.error) })
            : s?.available
              ? availableDetail
              : lastChecked
                ? t('settings.lastChecked', { time: lastChecked })
                : undefined
        }
      >
        <Button size="sm" variant="plain" disabled={check.isPending} onClick={runCheck}>
          {check.isPending ? <Spinner className="size-3.5" /> : <RefreshCw className="size-3.5" />}
        </Button>
        {updates && s?.available && (
          <Button size="sm" variant="plain" disabled={applying} aria-label={t('settings.updateApply')} onClick={runApply}>
            {applying ? <Spinner className="size-3.5" /> : <Download className="size-3.5" />}
          </Button>
        )}
        {container && s?.available && (
          <Button
            size="sm"
            variant="plain"
            disabled={containerApply.isPending}
            aria-label={t('settings.updateApply')}
            onClick={runContainerApply}
          >
            {containerApply.isPending ? <Spinner className="size-3.5" /> : <Download className="size-3.5" />}
          </Button>
        )}
      </Row>
    </>
  );
}
