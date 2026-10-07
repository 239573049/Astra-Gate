import { useState } from 'react';

import { useClients, useHealth, useReapplyClients, useVersion } from '../../api/hooks';
import { API_VERSION, type ClientInfo } from '../../api/types';
import { useT } from '../../i18n';
import { getBridge, isDesktop } from '../../shell/bridge';
import { systemActions } from '../../shell/systemActions';
import { Alert } from '../arc/alert/alert';
import { Button } from '../ui/controls';
import { errorText, useFeedback } from '../ui/overlays';

export function majorOf(v: string | undefined | null): number | null {
  const m = /^(\d+)(\.|$)/.exec(v ?? '');
  return m ? Number(m[1]) : null;
}

const clientNames = (clients: ClientInfo[], kinds: string[]) =>
  kinds.map((k) => clients.find((c) => c.kind === k)?.name ?? k).join(', ');

/** Top banners: server unreachable (with "start service" on desktop), API version mismatch, stale client configs. */
export function ConnectionBanner() {
  const t = useT();
  const { toast } = useFeedback();
  const health = useHealth();
  const version = useVersion();
  const clients = useClients();
  const reapply = useReapplyClients();
  const [starting, setStarting] = useState(false);
  const down = health.isError && health.failureCount >= 1;
  const mismatch =
    getBridge()?.apiVersionMismatch ||
    (version.data && majorOf(version.data.apiVersion) !== null && majorOf(version.data.apiVersion) !== majorOf(API_VERSION));
  // Plan §7.1: e.g. Astra came up on another port while enabled clients still point at the old address.
  const all = clients.data ?? [];
  const outdated = down ? [] : all.filter((c) => c.configOutdated).map((c) => c.kind);

  // Arc alert: opens and collapses its own height when the condition changes.
  return (
    <div className="chrome shrink-0 px-2 pt-2 empty:hidden">
      <Alert
        open={Boolean(down || mismatch)}
        tone={down ? 'danger' : 'warning'}
        title={down ? (isDesktop ? t('banner.downDesktop') : t('banner.downWeb')) : t('banner.mismatch')}
      >
        {down && isDesktop ? (
          <Button
            size="sm"
            variant="primary"
            className="mt-2"
            loading={starting}
            onClick={async () => {
              setStarting(true);
              await systemActions.startService();
              setStarting(false);
              void health.refetch();
            }}
          >
            {t('shell.startService')}
          </Button>
        ) : undefined}
      </Alert>
      <Alert open={outdated.length > 0} tone="warning" title={t('banner.clientsOutdated', { names: clientNames(all, outdated) })}>
        <Button
          size="sm"
          variant="primary"
          className="mt-2"
          loading={reapply.isPending}
          onClick={() =>
            reapply.mutate(undefined, {
              onSuccess: (r) => toast(t('banner.clientsUpdated', { names: clientNames(all, r.updated) }), 'success'),
              onError: (e) => toast(errorText(e), 'error'),
            })
          }
        >
          {t('banner.clientsUpdate')}
        </Button>
      </Alert>
    </div>
  );
}
