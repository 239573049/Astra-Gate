import { BookOpen, Copy, ExternalLink } from 'lucide-react';
import { useEffect, useState } from 'react';

import { useAuthStatus, useDetectCloudflared, useSetAdminPassword, useToggleTunnel, useTunnel, useUpdateTunnel } from '../api/hooks';
import type { TunnelDownloadSource, TunnelMode, TunnelStatus } from '../api/types';
import { Alert } from './arc/alert/alert';
import { Badge, Button, Disclosure, Dot, Group, Input, Row, Segmented, Spinner, Switch } from './ui/controls';
import { errorText, Menu, Select, useFeedback } from './ui/overlays';
import { useI18n, type MessageKey, type TFunction } from '../i18n';
import { formatDateTime, formatDuration } from '../lib/format';
import { systemActions } from '../shell/systemActions';

const GUIDE_URL = 'https://astra-gate.si/docs/guide/remote-access';

/** Set once the "anyone with the address…" warning was accepted; later switches are one click. */
const ACK_KEY = 'astra.remote.acknowledged';

/** Why the tunnel cannot start, in the UI's language (the server's own sentence as a fallback). */
function blocker(s: TunnelStatus, t: TFunction): string | null {
  if (s.mode === 'named' && !s.hasToken) return t('settings.remote.block.token');
  if (s.mode === 'named' && !s.hostname) return t('settings.remote.block.hostname');
  if (s.allowAdmin && !s.checks.passwordSet) return t('settings.remote.block.password');
  return s.checks.blocker ?? null;
}

/** cloudflared's own wording, as something to do about it; the raw message stays in the output below. */
export function friendlyTunnelError(raw: string, s: Pick<TunnelStatus, 'originUrl'>, t: TFunction): string {
  if (/can no longer be read/i.test(raw)) return t('settings.remote.err.tokenLost');
  if (/downloading cloudflared|checksum/i.test(raw)) return t('settings.remote.err.download');
  if (/unauthorized|invalid tunnel secret|token.*(invalid|not valid|malformed)/i.test(raw)) return t('settings.remote.err.token');
  if (/origin service|origin server/i.test(raw)) return t('settings.remote.err.origin', { origin: s.originUrl });
  if (/429|too many requests|rate.?limit/i.test(raw)) return t('settings.remote.err.rate');
  if (/dial|timeout|timed out|no such host|unreachable|quic|edge/i.test(raw)) return t('settings.remote.err.network');
  return t('settings.remote.err.generic');
}

function acknowledged(): boolean {
  try {
    return localStorage.getItem(ACK_KEY) === '1';
  } catch {
    return false;
  }
}

function acknowledge() {
  try {
    localStorage.setItem(ACK_KEY, '1');
  } catch {
    // ignore
  }
}

/**
 * Settings → Remote access: a Cloudflare Tunnel Astra runs (cloudflared as a child process). The switch with its
 * state, address and anything blocking it; how it connects (temporary address or the user's own tunnel); what
 * remote requests reach; cloudflared's output.
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
      <ModeGroup s={s} />
      <ScopeGroup s={s} />
      <CloudflaredGroup s={s} />
      <Disclosure title={s.cloudflared.path && s.cloudflared.version ? t('settings.remote.logWithVersion', { version: s.cloudflared.version }) : t('settings.remote.log')}>
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
    if (on && !acknowledged()) {
      if (!(await confirm({ title: t('settings.remote.enableConfirm'), detail: t('settings.remote.enableDetail'), confirmLabel: t('settings.remote.enableAction') })))
        return;
      acknowledge();
    }
    if (
      !on &&
      auth.data?.remote &&
      !(await confirm({ title: t('settings.remote.disableSelfConfirm'), detail: t('settings.remote.disableSelfDetail'), confirmLabel: t('settings.remote.disableAction'), destructive: true }))
    )
      return;
    toggle.mutate(on, { onError: (e) => toast(errorText(e), 'error') });
  };

  const tone = s.state === 'connected' ? 'green' : s.state === 'off' ? 'neutral' : 'orange';
  const since = s.connectedAt ? Date.now() - new Date(s.connectedAt).getTime() : null;
  const stateText =
    s.state === 'connected' && since !== null
      ? since < 60_000
        ? t('settings.remote.connectedJustNow')
        : t('settings.remote.connectedFor', { duration: formatDuration(since, locale) })
      : s.state === 'downloading' && s.cloudflared.source
        ? t('settings.remote.state.downloadingFrom', { source: s.cloudflared.source })
        : t(`settings.remote.state.${s.state}` as MessageKey);

  // What is in the way, most pressing first: something to fix before starting, then cloudflared not being at hand.
  const cf = s.cloudflared;
  const cfNote = cf.downloading ? null : !cf.path ? (cf.asset ? t('settings.remote.cloudflaredMissing', { version: cf.version ?? '' }) : t('settings.remote.cloudflaredUnsupported')) : null;
  const rawError = s.enabled ? (s.error ?? cf.error ?? null) : null;
  // Origin errors come from single requests while the tunnel itself is fine; anything else only matters while down.
  const showError = rawError !== null && (s.state !== 'connected' || /origin service|origin server/i.test(rawError));

  const base = s.publicUrl;
  return (
    <Group title={t('settings.remote.status')} footer={t('settings.remote.statusFooter')}>
      <Row
        label={t('settings.remote.enable')}
        detail={
          <span className="inline-flex items-center gap-1.5" title={s.connectedAt ? formatDateTime(s.connectedAt, locale) : undefined}>
            <Dot tone={tone} />
            {stateText}
            {cf.downloading && cf.progress != null && <span className="tabular-nums">{Math.round(cf.progress * 100)}%</span>}
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
      {!s.enabled && !why && cfNote && <p className="px-4 py-2 text-[12px] text-[var(--text-muted)]">{cfNote}</p>}
      {s.enabled && base && (
        <Row label={t('settings.remote.publicUrl')} detail={<span className="font-mono selectable">{`${base}/v1`}</span>}>
          <Menu
            label={t('settings.remote.copyMenu')}
            icon={<Copy className="size-3.5" />}
            iconOnly
            items={[
              { id: 'openai', label: t('settings.remote.copy.openai'), onSelect: () => void copy(`${base}/v1`) },
              { id: 'anthropic', label: t('settings.remote.copy.anthropic'), onSelect: () => void copy(base) },
              { id: 'key', label: t('settings.remote.copy.key'), onSelect: () => void copy(`${base}/key`), separatorBefore: true },
            ]}
          />
          <Button
            size="sm"
            variant="plain"
            icon={<ExternalLink className="size-3.5" />}
            aria-label={t('settings.remote.openPage')}
            title={t('settings.remote.openPage')}
            onClick={() => window.open(base, '_blank', 'noopener')}
          />
        </Row>
      )}
      {s.enabled && base && <p className="px-4 py-2 text-[11px] text-[var(--text-muted)]">{t('settings.remote.publicUrlHint')}</p>}
      {showError && (
        <div className="px-3 py-2">
          <Alert tone={s.state === 'connected' ? 'warning' : 'danger'} title={friendlyTunnelError(rawError, s, t)}>
            {rawError}
          </Alert>
        </div>
      )}
    </Group>
  );
}

function ModeGroup({ s }: { s: TunnelStatus }) {
  const { t } = useI18n();
  const { toast, confirm } = useFeedback();
  const update = useUpdateTunnel();
  const [form, setForm] = useState({ token: '', hostname: s.hostname ?? '' });
  useEffect(() => setForm((f) => ({ ...f, hostname: s.hostname ?? '' })), [s.hostname]);

  const copy = async (text: string) => {
    if (await systemActions.copy(text)) toast(t('common.copied'));
  };

  const changeMode = async (mode: TunnelMode) => {
    if (s.enabled) {
      // Running: the tunnel restarts (a quick address changes), or stops when the named tunnel isn't filled in yet.
      const stops = mode === 'named' && (!s.hasToken || !s.hostname);
      const ok = await confirm({
        title: t('settings.remote.modeSwitchConfirm'),
        detail: stops ? t('settings.remote.modeSwitchStopDetail') : t('settings.remote.modeSwitchDetail'),
        confirmLabel: t('settings.remote.modeSwitchAction'),
        destructive: stops,
      });
      if (!ok) return;
    }
    update.mutate({ mode }, { onError: (e) => toast(errorText(e), 'error') });
  };

  const save = () =>
    update.mutate(
      { hostname: form.hostname.trim() || null, ...(form.token.trim() ? { token: form.token.trim() } : {}) },
      {
        onSuccess: (next) => {
          toast(next.enabled ? t('settings.remote.savedRestart') : t('settings.remote.saved'));
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
          onChange={(m) => void changeMode(m)}
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
  const auth = useAuthStatus();

  const onAdmin = async (allowAdmin: boolean) => {
    if (allowAdmin && !(await confirm({ title: t('settings.remote.adminConfirm'), detail: t('settings.remote.adminDetail'), confirmLabel: t('settings.remote.adminAction'), destructive: true })))
      return;
    if (
      !allowAdmin &&
      auth.data?.remote &&
      !(await confirm({ title: t('settings.remote.adminOffSelfConfirm'), detail: t('settings.remote.adminOffSelfDetail'), confirmLabel: t('settings.remote.disableAction'), destructive: true }))
    )
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
      <PasswordRow passwordSet={s.checks.passwordSet} />
    </Group>
  );
}

/** The admin password (what `astra-server set-password` sets); only remote admin needs it, so it lives here. */
function PasswordRow({ passwordSet }: { passwordSet: boolean }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const auth = useAuthStatus();
  const setPassword = useSetAdminPassword();
  const [editing, setEditing] = useState(false);
  const [password, setPasswordText] = useState('');

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
    <>
      <Row label={t('settings.remote.password')} detail={passwordSet ? t('settings.remote.passwordSet') : t('settings.remote.passwordMissing')}>
        {/* The server refuses it through a tunnel anyway; don't offer it there. */}
        {!editing && !auth.data?.remote && (
          <Button size="sm" onClick={() => setEditing(true)}>
            {passwordSet ? t('settings.remote.passwordChange') : t('settings.remote.passwordSetAction')}
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
    </>
  );
}

const SOURCES: TunnelDownloadSource[] = ['auto', 'github', 'astra', 'custom'];

/**
 * Where cloudflared comes from: which program runs, the download source (GitHub, the Astra site's mirror, or a custom
 * address — all checked against the same pinned checksums), or an installed cloudflared instead of a download.
 * Choosing a program to run is offered on this machine only (the server refuses it through a tunnel).
 */
function CloudflaredGroup({ s }: { s: TunnelStatus }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const update = useUpdateTunnel();
  const detect = useDetectCloudflared();
  const auth = useAuthStatus();
  const cf = s.cloudflared;
  const [baseUrl, setBaseUrl] = useState(s.download.downloadBaseUrl ?? '');
  const [choosing, setChoosing] = useState(false);
  const [path, setPath] = useState('');
  useEffect(() => setBaseUrl(s.download.downloadBaseUrl ?? ''), [s.download.downloadBaseUrl]);
  // Picking "custom" shows the address field first; it is saved together with the address.
  const [source, setSource] = useState<TunnelDownloadSource>(s.download.downloadSource);
  useEffect(() => setSource(s.download.downloadSource), [s.download.downloadSource]);

  const save = (config: Parameters<typeof update.mutate>[0], after?: () => void) =>
    update.mutate(config, {
      onSuccess: () => {
        toast(t('settings.remote.cf.saved'));
        after?.();
      },
      onError: (e) => toast(errorText(e), 'error'),
    });

  const changeSource = (next: TunnelDownloadSource) => {
    setSource(next);
    if (next !== 'custom' || s.download.downloadBaseUrl) save({ downloadSource: next });
  };

  const find = () =>
    detect.mutate(undefined, {
      onSuccess: (found) => (found.path ? setPath(found.path) : toast(t('settings.remote.cf.detectNone'))),
      onError: (e) => toast(errorText(e), 'error'),
    });

  const program = cf.fromEnvironment
    ? t('settings.remote.cf.fromEnv')
    : cf.installed
      ? cf.version
        ? t('settings.remote.cf.installedProgram', { version: cf.version })
        : t('settings.remote.cf.installedUnknown')
      : cf.path
        ? t('settings.remote.cf.downloaded', { version: cf.version ?? '' })
        : cf.asset
          ? t('settings.remote.cf.notDownloaded', { version: cf.version ?? '' })
          : t('settings.remote.cloudflaredUnsupported');

  return (
    <Group title="cloudflared" footer={t('settings.remote.cf.footer')}>
      <Row label={t('settings.remote.cf.program')} detail={<span title={cf.path ?? undefined}>{program}</span>}>
        <Dot tone={cf.path ? 'green' : cf.asset ? 'neutral' : 'red'} />
      </Row>
      {!s.download.cloudflaredPath && !cf.fromEnvironment && (
        <>
          <Row label={t('settings.remote.cf.source')} detail={t(`settings.remote.cf.source.${source}.hint` as MessageKey)}>
            <Select
              className="w-44"
              ariaLabel={t('settings.remote.cf.source')}
              value={source}
              onChange={changeSource}
              options={SOURCES.map((v) => ({ value: v, label: t(`settings.remote.cf.source.${v}` as MessageKey) }))}
            />
          </Row>
          {source === 'custom' && (
            <Row label={t('settings.remote.cf.baseUrl')} detail={t('settings.remote.cf.baseUrlHint')}>
              <Input
                className="w-64"
                mono
                aria-label={t('settings.remote.cf.baseUrl')}
                placeholder="https://…/cloudflare/cloudflared/releases/download"
                value={baseUrl}
                onChange={(e) => setBaseUrl(e.target.value)}
              />
              <Button
                size="sm"
                disabled={!baseUrl.trim() || update.isPending || (baseUrl.trim() === (s.download.downloadBaseUrl ?? '') && s.download.downloadSource === 'custom')}
                onClick={() => save({ downloadSource: 'custom', downloadBaseUrl: baseUrl.trim() })}
              >
                {t('common.save')}
              </Button>
            </Row>
          )}
        </>
      )}
      {!auth.data?.remote && !cf.fromEnvironment && (
        <Row
          label={t('settings.remote.cf.installed')}
          detail={s.download.cloudflaredPath ? <span className="font-mono selectable">{s.download.cloudflaredPath}</span> : t('settings.remote.cf.installedHint')}
        >
          {s.download.cloudflaredPath ? (
            <Button size="sm" disabled={update.isPending} onClick={() => save({ cloudflaredPath: '' })}>
              {t('settings.remote.cf.useDownloaded')}
            </Button>
          ) : (
            !choosing && (
              <Button
                size="sm"
                onClick={() => {
                  setChoosing(true);
                  find();
                }}
              >
                {t('settings.remote.cf.choose')}
              </Button>
            )
          )}
        </Row>
      )}
      {choosing && !s.download.cloudflaredPath && (
        <div className="flex items-center justify-end gap-2 px-4 py-2">
          <Input
            className="w-72"
            mono
            autoFocus
            aria-label={t('settings.remote.cf.installedPath')}
            placeholder={t('settings.remote.cf.installedPath')}
            value={path}
            onChange={(e) => setPath(e.target.value)}
          />
          <Button size="sm" variant="plain" loading={detect.isPending} onClick={find}>
            {t('settings.remote.cf.detect')}
          </Button>
          <Button size="sm" variant="plain" onClick={() => setChoosing(false)}>
            {t('common.cancel')}
          </Button>
          <Button size="sm" variant="primary" disabled={!path.trim()} loading={update.isPending} onClick={() => save({ cloudflaredPath: path.trim() }, () => setChoosing(false))}>
            {t('settings.remote.cf.use')}
          </Button>
        </div>
      )}
    </Group>
  );
}
