import { BookOpen, Copy, ExternalLink } from 'lucide-react';
import { useEffect, useState } from 'react';

import { useAuthStatus, useSetAdminPassword, useToggleTunnel, useTunnel, useUpdateTunnel } from '../api/hooks';
import type { TunnelMode, TunnelStatus } from '../api/types';
import { Alert } from './arc/alert/alert';
import { Badge, Button, Disclosure, Dot, Group, Input, Row, Segmented, Spinner, Switch } from './ui/controls';
import { errorText, useFeedback } from './ui/overlays';
import { useI18n, type MessageKey, type TFunction } from '../i18n';
import { formatDateTime } from '../lib/format';
import { systemActions } from '../shell/systemActions';

const GUIDE_URL = 'https://astra-gate.si/docs/guide/remote-access';

/** Why the tunnel cannot start, in the UI's language (the server's own sentence as a fallback). */
function blocker(s: TunnelStatus, t: TFunction): string | null {
  if (s.mode === 'named' && !s.hasToken) return t('settings.remote.block.token');
  if (s.mode === 'named' && !s.hostname) return t('settings.remote.block.hostname');
  if (s.allowAdmin && !s.checks.passwordSet) return t('settings.remote.block.password');
  return s.checks.blocker ?? null;
}

/**
 * Settings → Remote access: a Cloudflare Tunnel Astra runs (cloudflared as a child process). Status and the switch,
 * what starting needs, how it connects (temporary address or the user's own tunnel), and what remote requests reach.
 */
export function RemoteAccessSettings() {
  const { t } = useI18n();
  const tunnel = useTunnel();
  const s = tunnel.data;

  if (!s) {
    return (
      <div className="mx-auto max-w-[680px]">
        {tunnel.isError ? <div className="text-[12px] text-[var(--danger)]">{errorText(tunnel.error)}</div> : <Spinner className="mx-auto my-8" />}
      </div>
    );
  }

  if (!s.supported) {
    return (
      <div className="mx-auto max-w-[680px]">
        <Alert tone="info" title={t('settings.remote.unsupported')}>
          {t('settings.remote.unsupportedDetail')}
        </Alert>
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-[680px]">
      <StatusGroup s={s} />
      <ChecksGroup s={s} />
      <ModeGroup s={s} />
      <ScopeGroup s={s} />
      <Disclosure title={t('settings.remote.log')}>
        {s.log.length === 0 ? (
          <p className="text-[12px] text-[var(--text-muted)]">{t('settings.remote.logEmpty')}</p>
        ) : (
          <pre className="max-h-72 overflow-auto font-mono text-[10.5px] leading-relaxed whitespace-pre-wrap text-[var(--text-secondary)] selectable">
            {s.log.join('\n')}
          </pre>
        )}
      </Disclosure>
    </div>
  );
}

function StatusGroup({ s }: { s: TunnelStatus }) {
  const { t, locale } = useI18n();
  const { toast, confirm } = useFeedback();
  const toggle = useToggleTunnel();
  const auth = useAuthStatus();
  const why = blocker(s, t);

  const copy = async (text: string) => {
    if (await systemActions.copy(text)) toast(t('common.copied'));
  };

  const onToggle = async (on: boolean) => {
    if (on && !(await confirm({ title: t('settings.remote.enableConfirm'), detail: t('settings.remote.enableDetail'), confirmLabel: t('settings.remote.enableAction') })))
      return;
    if (
      !on &&
      auth.data?.remote &&
      !(await confirm({ title: t('settings.remote.disableSelfConfirm'), detail: t('settings.remote.disableSelfDetail'), confirmLabel: t('settings.remote.disableAction'), destructive: true }))
    )
      return;
    toggle.mutate(on, { onError: (e) => toast(errorText(e), 'error') });
  };

  const tone = s.state === 'connected' ? 'green' : s.state === 'off' ? 'neutral' : 'orange';
  const stateText =
    s.state === 'connected' && s.connectedAt
      ? t('settings.remote.connectedSince', { time: formatDateTime(s.connectedAt, locale) })
      : t(`settings.remote.state.${s.state}` as MessageKey);
  const gatewayUrl = s.publicUrl ? `${s.publicUrl}/v1` : null;

  return (
    <Group title={t('settings.remote.status')} footer={t('settings.remote.statusFooter')}>
      <Row
        label={t('settings.remote.enable')}
        detail={
          <span className="inline-flex items-center gap-1.5">
            <Dot tone={tone} />
            {stateText}
          </span>
        }
      >
        <Switch
          checked={s.enabled}
          label={t('settings.remote.enable')}
          disabled={toggle.isPending || (!s.enabled && why !== null)}
          onChange={(on) => void onToggle(on)}
        />
      </Row>
      {!s.enabled && why && <p className="px-4 py-2 text-[12px] text-[var(--warning)]">{why}</p>}
      {s.enabled && gatewayUrl && (
        <Row label={t('settings.remote.publicUrl')} detail={<span className="font-mono selectable">{gatewayUrl}</span>}>
          <Button size="sm" variant="plain" icon={<Copy className="size-3.5" />} aria-label={t('common.copy')} onClick={() => void copy(gatewayUrl)} />
          <Button
            size="sm"
            variant="plain"
            icon={<ExternalLink className="size-3.5" />}
            aria-label={t('settings.remote.openPage')}
            title={t('settings.remote.openPage')}
            onClick={() => window.open(s.publicUrl!, '_blank', 'noopener')}
          />
        </Row>
      )}
      {s.enabled && s.publicUrl && <p className="px-4 py-2 text-[11px] text-[var(--text-muted)]">{t('settings.remote.publicUrlHint')}</p>}
      {s.cloudflared.downloading && (
        <Row label={t('settings.remote.download')} detail={s.cloudflared.progress != null ? `${Math.round(s.cloudflared.progress * 100)}%` : '…'} />
      )}
      {s.enabled && s.state !== 'connected' && (s.error || s.cloudflared.error) && (
        <div className="px-3 py-2">
          <Alert tone="danger" title={s.error ?? s.cloudflared.error ?? ''} />
        </div>
      )}
    </Group>
  );
}

function ChecksGroup({ s }: { s: TunnelStatus }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const setPassword = useSetAdminPassword();
  const [editing, setEditing] = useState(false);
  const [password, setPasswordText] = useState('');

  const cf = s.cloudflared;
  const cfText = cf.path
    ? cf.version
      ? t('settings.remote.cloudflaredReady', { version: cf.version })
      : t('settings.remote.cloudflaredCustom')
    : cf.asset
      ? t('settings.remote.cloudflaredMissing', { version: cf.version ?? '' })
      : t('settings.remote.cloudflaredUnsupported');

  const save = () =>
    setPassword.mutate(password, {
      onSuccess: () => {
        toast(t('settings.remote.passwordSaved'));
        setEditing(false);
        setPasswordText('');
      },
      onError: (e) => toast(errorText(e), 'error'),
    });

  return (
    <Group title={t('settings.remote.checks')}>
      <Row label={t('settings.remote.cloudflared')} detail={cfText}>
        <Dot tone={cf.path ? 'green' : cf.asset ? 'neutral' : 'red'} />
      </Row>
      <Row
        label={t('settings.remote.password')}
        detail={s.checks.passwordSet ? t('settings.remote.passwordSet') : t('settings.remote.passwordMissing')}
      >
        {!editing && (
          <Button size="sm" onClick={() => setEditing(true)}>
            {s.checks.passwordSet ? t('settings.remote.passwordChange') : t('settings.remote.passwordSetAction')}
          </Button>
        )}
      </Row>
      {editing && (
        <div className="flex items-center justify-end gap-2 px-4 py-2">
          <Input
            className="w-64"
            type="password"
            autoComplete="new-password"
            autoFocus
            aria-label={t('settings.remote.passwordNew')}
            placeholder={t('settings.remote.passwordNew')}
            value={password}
            onChange={(e) => setPasswordText(e.target.value)}
          />
          <Button size="sm" variant="plain" onClick={() => setEditing(false)}>
            {t('common.cancel')}
          </Button>
          <Button size="sm" variant="primary" disabled={password.length < 8} loading={setPassword.isPending} onClick={save}>
            {t('common.save')}
          </Button>
        </div>
      )}
    </Group>
  );
}

function ModeGroup({ s }: { s: TunnelStatus }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const update = useUpdateTunnel();
  const [form, setForm] = useState({ token: '', hostname: s.hostname ?? '' });
  useEffect(() => setForm((f) => ({ ...f, hostname: s.hostname ?? '' })), [s.hostname]);

  const copy = async (text: string) => {
    if (await systemActions.copy(text)) toast(t('common.copied'));
  };

  const changeMode = (mode: TunnelMode) => update.mutate({ mode }, { onError: (e) => toast(errorText(e), 'error') });

  const save = () =>
    update.mutate(
      { hostname: form.hostname.trim() || null, ...(form.token.trim() ? { token: form.token.trim() } : {}) },
      {
        onSuccess: () => {
          toast(t('settings.remote.saved'));
          setForm((f) => ({ ...f, token: '' }));
        },
        onError: (e) => toast(errorText(e), 'error'),
      },
    );

  const dirty = form.token.trim() !== '' || form.hostname.trim() !== (s.hostname ?? '');

  return (
    <Group title={t('settings.remote.mode')} footer={s.mode === 'named' ? t('settings.remote.namedFooter') : undefined}>
      <Row label={t('settings.remote.modeLabel')} detail={t(`settings.remote.mode.${s.mode}.hint` as MessageKey)}>
        <Segmented
          ariaLabel={t('settings.remote.modeLabel')}
          value={s.mode}
          onChange={changeMode}
          items={[
            { value: 'quick', label: t('settings.remote.mode.quick') },
            { value: 'named', label: t('settings.remote.mode.named') },
          ]}
        />
      </Row>
      {s.mode === 'quick' ? (
        <div className="px-3 py-2">
          <Alert tone="warning" title={t('settings.remote.quickWarning')} />
        </div>
      ) : (
        <>
          <Row label={t('settings.remote.token')} detail={s.hasToken ? t('settings.remote.tokenSaved') : t('settings.remote.tokenHint')}>
            <Input
              className="w-64"
              type="password"
              autoComplete="off"
              mono
              aria-label={t('settings.remote.token')}
              placeholder={s.hasToken ? '••••••••' : 'eyJhIjoi…'}
              value={form.token}
              onChange={(e) => setForm({ ...form, token: e.target.value })}
            />
          </Row>
          <Row label={t('settings.remote.hostname')} detail={t('settings.remote.hostnameHint')}>
            <Input
              className="w-64"
              mono
              aria-label={t('settings.remote.hostname')}
              placeholder="astra.example.com"
              value={form.hostname}
              onChange={(e) => setForm({ ...form, hostname: e.target.value })}
            />
          </Row>
          <Row label={t('settings.remote.origin')} detail={t('settings.remote.originHint')}>
            <span className="font-mono text-[12px] selectable">{s.originUrl}</span>
            <Button size="sm" variant="plain" icon={<Copy className="size-3.5" />} aria-label={t('common.copy')} onClick={() => void copy(s.originUrl)} />
          </Row>
          <div className="flex items-center justify-between px-4 py-2">
            <Button size="sm" variant="plain" icon={<BookOpen className="size-3.5" />} onClick={() => window.open(GUIDE_URL, '_blank', 'noopener')}>
              {t('settings.remote.guide')}
            </Button>
            <Button size="sm" disabled={!dirty || update.isPending} onClick={save}>
              {t('common.save')}
            </Button>
          </div>
        </>
      )}
    </Group>
  );
}

function ScopeGroup({ s }: { s: TunnelStatus }) {
  const { t } = useI18n();
  const { toast, confirm } = useFeedback();
  const update = useUpdateTunnel();

  const onAdmin = async (allowAdmin: boolean) => {
    if (allowAdmin && !(await confirm({ title: t('settings.remote.adminConfirm'), detail: t('settings.remote.adminDetail'), confirmLabel: t('settings.remote.adminAction'), destructive: true })))
      return;
    update.mutate({ allowAdmin }, { onError: (e) => toast(errorText(e), 'error') });
  };

  return (
    <Group title={t('settings.remote.scope')} footer={t('settings.remote.scopeFooter')}>
      <Row label={t('settings.remote.scope.gateway')} detail={t('settings.remote.scope.gatewayHint')}>
        <Badge tone="green">{t('settings.remote.scope.gatewayBadge')}</Badge>
      </Row>
      <Row
        label={t('settings.remote.scope.admin')}
        detail={s.checks.passwordSet ? t('settings.remote.scope.adminHint') : t('settings.remote.block.password')}
      >
        <Switch
          checked={s.allowAdmin}
          label={t('settings.remote.scope.admin')}
          disabled={update.isPending || (!s.allowAdmin && !s.checks.passwordSet)}
          onChange={(v) => void onAdmin(v)}
        />
      </Row>
    </Group>
  );
}
