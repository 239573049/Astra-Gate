import { Copy, Ellipsis, KeyRound, Pencil, Plug, Plus, ShieldCheck, Trash2, Zap } from 'lucide-react';
import { useEffect, useState } from 'react';

import {
  useCreateMcpServer,
  useDeleteMcpServer,
  useForgetMcpOAuth,
  useMcpCalls,
  useMcpServers,
  useMcpSettings,
  useStartMcpOAuth,
  useTestMcpServer,
  useUpdateMcpServer,
  useUpdateMcpSettings,
} from '../api/hooks';
import type { McpAuthType, McpCall, McpServer, McpServerInput, McpServerStatus, McpTestResult, McpTransport } from '../api/types';
import { useI18n, type MessageKey } from '../i18n';
import { formatDateTime, formatMs } from '../lib/format';
import { systemActions } from '../shell/systemActions';
import { Alert } from './arc/alert/alert';
import { ClientMcpEntries } from './ClientMcpEntries';
import { LocalMcpServers } from './LocalMcpServers';
import { Badge, Button, EmptyState, Field, Group, Input, NumberInput, Row, Segmented, Spinner, Switch, TextArea } from './ui/controls';
import { errorText, Menu, Sheet, useFeedback } from './ui/overlays';

const STATUS_TONE: Record<McpServerStatus, 'neutral' | 'green' | 'orange' | 'red'> = {
  unknown: 'neutral',
  ok: 'green',
  needs_auth: 'orange',
  auth_error: 'red',
  unreachable: 'red',
  version_unsupported: 'red',
};

/** The MCP gateway page (/mcp): the gateway's switch and address, the upstream servers, and the recent calls. */
export function McpGateway() {
  return (
    <>
      <GatewaySettings />
      <ClientMcpEntries />
      <LocalMcpServers />
      <Servers />
      <Calls />
    </>
  );
}

// ---------------------------------------------------------------- gateway

function GatewaySettings() {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const settings = useMcpSettings();
  const update = useUpdateMcpSettings();
  const [origins, setOrigins] = useState('');
  const saved = settings.data?.allowedOrigins.join('\n') ?? '';

  useEffect(() => setOrigins(saved), [saved]);

  if (!settings.data) return <Spinner />;
  const gw = settings.data;
  const save = (body: { enabled?: boolean; allowedOrigins?: string[] }) =>
    update.mutate(body, { onSuccess: () => toast(t('common.saved'), 'success'), onError: (e) => toast(errorText(e), 'error') });

  return (
    <Group title={t('mcp.gateway.title')}>
      <Row label={t('mcp.gateway.enable')} detail={t('mcp.gateway.enableHint')}>
        <Switch checked={gw.enabled} disabled={update.isPending} label={t('mcp.gateway.enable')} onChange={(enabled) => save({ enabled })} />
      </Row>
      <Row label={t('mcp.gateway.endpoint')} detail={<span className="block whitespace-normal">{t('mcp.gateway.endpointHint')}</span>}>
        <code className="max-w-[22rem] truncate rounded bg-[var(--surface-muted,transparent)] px-1.5 py-0.5 font-mono text-[12px]" title={gw.endpoint}>
          {gw.endpoint}
        </code>
        <Button
          size="sm"
          variant="plain"
          icon={<Copy className="size-3.5" />}
          aria-label={t('mcp.gateway.copyEndpoint')}
          onClick={() => void systemActions.copy(gw.endpoint).then((ok) => ok && toast(t('mcp.gateway.copied'), 'success'))}
        />
      </Row>
      <div className="grid gap-2 px-4 py-3">
        <Field label={t('mcp.gateway.origins')} hint={t('mcp.gateway.originsHint')}>
          <TextArea mono rows={3} value={origins} placeholder="https://app.example.com" onChange={(e) => setOrigins(e.target.value)} />
        </Field>
        <div>
          <Button
            size="sm"
            disabled={origins.trim() === saved.trim() || update.isPending}
            onClick={() => save({ allowedOrigins: origins.split('\n').map((o) => o.trim()).filter(Boolean) })}
          >
            {t('mcp.gateway.originsSave')}
          </Button>
        </div>
      </div>
    </Group>
  );
}

// ---------------------------------------------------------------- servers

function Servers() {
  const { t } = useI18n();
  const { toast, confirm } = useFeedback();
  const servers = useMcpServers();
  const test = useTestMcpServer();
  const startOAuth = useStartMcpOAuth();
  const forget = useForgetMcpOAuth();
  const remove = useDeleteMcpServer();
  const [editing, setEditing] = useState<McpServer | 'new' | null>(null);
  const [tested, setTested] = useState<{ server: McpServer; result: McpTestResult } | null>(null);

  const runTest = (s: McpServer) =>
    test.mutate(s.id, {
      onSuccess: (result) => {
        if (result.ok) setTested({ server: s, result });
        else toast(t('mcp.server.testFailed', { message: result.message ?? result.status }), 'error');
      },
      onError: (e) => toast(errorText(e), 'error'),
    });

  const authorize = (s: McpServer) =>
    startOAuth.mutate(s.id, {
      onSuccess: (r) => {
        if (r.authorizationUrl) {
          window.open(r.authorizationUrl, '_blank', 'noopener');
          toast(t('mcp.server.authorizeOpened'), 'success');
        } else toast(r.message ?? t('mcp.server.authorizeNotNeeded'), 'success');
      },
      onError: (e) => toast(errorText(e), 'error'),
    });

  const forgetAuth = async (s: McpServer) => {
    if (!(await confirm({ title: t('mcp.server.forgetConfirm', { name: s.name }), detail: t('mcp.server.forgetDetail'), confirmLabel: t('mcp.server.forget'), destructive: true }))) return;
    forget.mutate(s.id, { onSuccess: () => toast(t('mcp.server.forgotten'), 'success'), onError: (e) => toast(errorText(e), 'error') });
  };

  const del = async (s: McpServer) => {
    if (!(await confirm({ title: t('mcp.server.deleteConfirm', { name: s.name }), detail: t('mcp.server.deleteDetail'), confirmLabel: t('common.delete'), destructive: true }))) return;
    remove.mutate(s.id, { onSuccess: () => toast(t('mcp.server.deleted', { name: s.name }), 'success'), onError: (e) => toast(errorText(e), 'error') });
  };

  return (
    <section className="mb-5">
      <div className="mb-2 flex items-center justify-between px-1">
        <h3 className="text-[12px] font-medium text-[var(--text-secondary)]">{t('mcp.servers.title')}</h3>
        <Button size="sm" variant="primary" icon={<Plus className="size-3.5" />} onClick={() => setEditing('new')}>
          {t('mcp.servers.add')}
        </Button>
      </div>
      {servers.data === undefined ? (
        <Spinner />
      ) : servers.data.length === 0 ? (
        <EmptyState icon={<Plug className="size-6" />} title={t('mcp.servers.emptyTitle')} detail={t('mcp.servers.emptyDetail')} />
      ) : (
        <div className="card divide-y divide-[var(--border)]">
          {servers.data.map((s) => (
            <ServerRow
              key={s.id}
              server={s}
              busy={test.isPending && test.variables === s.id}
              onTest={() => runTest(s)}
              onEdit={() => setEditing(s)}
              onAuthorize={() => authorize(s)}
              onForget={() => void forgetAuth(s)}
              onDelete={() => void del(s)}
            />
          ))}
        </div>
      )}
      <p className="mt-2 px-1 text-[11px] text-[var(--text-muted)]">{t('mcp.servers.localOnly')}</p>
      <ServerSheet server={editing} onClose={() => setEditing(null)} />
      <Sheet
        open={tested !== null}
        onOpenChange={(o) => !o && setTested(null)}
        title={t('mcp.server.testToolsTitle', { name: tested?.server.name ?? '' })}
        description={tested ? t('mcp.server.testOk', { count: tested.result.toolCount }) : undefined}
        footer={<Button onClick={() => setTested(null)}>{t('common.close')}</Button>}
      >
        <ul className="max-h-72 overflow-auto font-mono text-[12px] text-[var(--text-secondary)]">
          {tested?.result.tools.map((name) => (
            <li key={name} className="truncate py-0.5">{name}</li>
          ))}
        </ul>
      </Sheet>
    </section>
  );
}

function ServerRow({
  server: s, busy, onTest, onEdit, onAuthorize, onForget, onDelete,
}: {
  server: McpServer; busy: boolean; onTest: () => void; onEdit: () => void; onAuthorize: () => void; onForget: () => void; onDelete: () => void;
}) {
  const { t, locale } = useI18n();
  const oauth = s.authType === 'oauth';
  const authorized = s.oauth?.authorized === true;
  const detail = [
    s.slug,
    s.protocolVersion ? t('mcp.server.protocol', { version: s.protocolVersion }) : null,
    t('mcp.server.boundTokens', { count: s.boundTokens }),
    s.checkedAt ? formatDateTime(s.checkedAt, locale) : null,
  ].filter(Boolean).join(' · ');
  return (
    <Row
      label={
        <span className="flex min-w-0 items-center gap-2">
          <span className="truncate">{s.name}</span>
          <Badge tone={STATUS_TONE[s.status]} title={s.statusMessage ?? undefined}>{t(`mcp.status.${s.status}` as MessageKey)}</Badge>
          {!s.enabled && <Badge>{t('mcp.server.disabledBadge')}</Badge>}
        </span>
      }
      detail={
        <span className="block whitespace-normal">
          <span className="font-mono">{s.url}</span>
          <span className="block">{detail}</span>
          {oauth && authorized && s.oauth?.issuer && <span className="block">{t('mcp.server.authorizedBy', { issuer: s.oauth.issuer })}</span>}
        </span>
      }
    >
      {oauth && (
        <Button size="sm" variant={authorized ? 'glass' : 'primary'} icon={<ShieldCheck className="size-3.5" />} onClick={onAuthorize}>
          {authorized ? t('mcp.server.reauthorize') : t('mcp.server.authorize')}
        </Button>
      )}
      <Button size="sm" icon={<Zap className="size-3.5" />} loading={busy} onClick={onTest}>
        {t('mcp.server.test')}
      </Button>
      <Menu
        label={s.name}
        iconOnly
        icon={<Ellipsis className="size-4" />}
        items={[
          { id: 'edit', label: t('common.edit'), icon: <Pencil className="size-3.5" />, onSelect: onEdit },
          ...(oauth && authorized ? [{ id: 'forget', label: t('mcp.server.forget'), icon: <KeyRound className="size-3.5" />, onSelect: onForget }] : []),
          { id: 'delete', label: t('common.delete'), icon: <Trash2 className="size-3.5" />, destructive: true, separatorBefore: true, onSelect: onDelete },
        ]}
      />
    </Row>
  );
}

interface HeaderDraft { name: string; value: string; kept: boolean }

function ServerSheet({ server, onClose }: { server: McpServer | 'new' | null; onClose: () => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const create = useCreateMcpServer();
  const update = useUpdateMcpServer();
  const existing = server && server !== 'new' ? server : null;
  const [name, setName] = useState('');
  const [slug, setSlug] = useState('');
  const [url, setUrl] = useState('');
  const [transport, setTransport] = useState<McpTransport>('http');
  const [authType, setAuthType] = useState<McpAuthType>('none');
  const [headers, setHeaders] = useState<HeaderDraft[]>([]);
  const [timeout, setTimeoutSeconds] = useState<number | null>(60);
  const [enabled, setEnabled] = useState(true);

  useEffect(() => {
    if (server === null) return;
    const s = server === 'new' ? null : server;
    setName(s?.name ?? '');
    setSlug(s?.slug ?? '');
    setUrl(s?.url ?? '');
    setTransport(s?.transport ?? 'http');
    setAuthType(s?.authType ?? 'none');
    // Stored values are never sent to the browser: an existing header shows as "kept" until its value is typed again.
    setHeaders((s?.headerNames ?? []).map((n) => ({ name: n, value: '', kept: true })));
    setTimeoutSeconds(s?.timeoutSeconds ?? 60);
    setEnabled(s?.enabled ?? true);
  }, [server]);

  const pending = create.isPending || update.isPending;
  const valid = name.trim() !== '' && url.trim() !== '' && (authType !== 'header' || headers.some((h) => h.name.trim() !== '' && (h.value !== '' || h.kept)));

  const save = () => {
    const body: McpServerInput = {
      name: name.trim(),
      url: url.trim(),
      transport,
      authType,
      enabled,
      ...(timeout != null ? { timeoutSeconds: Math.floor(timeout) } : {}),
      ...(slug.trim() ? { slug: slug.trim() } : {}),
    };
    if (authType === 'header') {
      body.headers = Object.fromEntries(headers.filter((h) => h.name.trim() !== '').map((h) => [h.name.trim(), h.kept && h.value === '' ? null : h.value]));
    }
    const done = {
      onSuccess: (s: McpServer) => {
        onClose();
        toast(t(existing ? 'mcp.server.saved' : 'mcp.server.created', { name: s.name }), 'success');
      },
      onError: (e: unknown) => toast(errorText(e), 'error'),
    };
    if (existing) update.mutate({ id: existing.id, body }, done);
    else create.mutate(body, done);
  };

  const setHeader = (i: number, patch: Partial<HeaderDraft>) => setHeaders((hs) => hs.map((h, j) => (j === i ? { ...h, ...patch } : h)));

  return (
    <Sheet
      open={server !== null}
      onOpenChange={(o) => !o && onClose()}
      width={560}
      title={existing ? t('mcp.server.editTitle', { name: existing.name }) : t('mcp.server.addTitle')}
      footer={
        <>
          <Button onClick={onClose}>{t('common.cancel')}</Button>
          <Button variant="primary" disabled={!valid} loading={pending} onClick={save}>
            {t('common.save')}
          </Button>
        </>
      }
    >
      <div className="grid gap-4">
        <Field label={t('mcp.server.name')}>
          <Input value={name} maxLength={64} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label={t('mcp.server.url')} hint={t('mcp.server.urlHint')}>
          <Input mono value={url} placeholder="https://mcp.example.com/mcp" onChange={(e) => setUrl(e.target.value)} />
        </Field>
        <Field label={t('mcp.server.transport')} hint={t('mcp.server.transportHint')}>
          <Segmented
            ariaLabel={t('mcp.server.transport')}
            value={transport}
            onChange={setTransport}
            items={[
              { value: 'http', label: t('mcp.server.transport.http') },
              { value: 'sse', label: t('mcp.server.transport.sse') },
            ]}
          />
        </Field>
        <Field label={t('mcp.server.auth')}>
          <Segmented
            ariaLabel={t('mcp.server.auth')}
            value={authType}
            onChange={setAuthType}
            items={[
              { value: 'none', label: t('mcp.server.auth.none') },
              { value: 'header', label: t('mcp.server.auth.header') },
              { value: 'oauth', label: t('mcp.server.auth.oauth') },
            ]}
          />
        </Field>
        {authType === 'oauth' && <Alert tone="warning" title={t('mcp.server.oauthNote')} />}
        {authType === 'header' && (
          <Field label={t('mcp.server.headers')} hint={t('mcp.server.headersHint')}>
            <div className="grid gap-2">
              {headers.map((h, i) => (
                <div key={i} className="flex items-center gap-2">
                  <Input className="w-40" mono label={t('mcp.server.headerName')} placeholder="Authorization" value={h.name} readOnly={h.kept && existing !== null} onChange={(e) => setHeader(i, { name: e.target.value })} />
                  <Input
                    className="flex-1"
                    mono
                    type="password"
                    label={t('mcp.server.headerValue')}
                    placeholder={h.kept ? t('mcp.server.headerKept') : 'Bearer …'}
                    value={h.value}
                    onChange={(e) => setHeader(i, { value: e.target.value })}
                  />
                  <Button size="sm" variant="plain" icon={<Trash2 className="size-3.5" />} aria-label={t('mcp.server.removeHeader')} onClick={() => setHeaders((hs) => hs.filter((_, j) => j !== i))} />
                </div>
              ))}
              <div>
                <Button size="sm" icon={<Plus className="size-3.5" />} onClick={() => setHeaders((hs) => [...hs, { name: '', value: '', kept: false }])}>
                  {t('mcp.server.addHeader')}
                </Button>
              </div>
            </div>
          </Field>
        )}
        <Field label={t('mcp.server.slug')} hint={t('mcp.server.slugHint')}>
          <Input mono value={slug} maxLength={32} placeholder={existing ? '' : 'github'} onChange={(e) => setSlug(e.target.value)} />
        </Field>
        <Field label={t('mcp.server.timeout')} hint={t('mcp.server.timeoutHint')}>
          <NumberInput value={timeout} min={1} step={1} label={t('mcp.server.timeout')} onChange={setTimeoutSeconds} />
        </Field>
        <Row label={t('mcp.server.enabled')}>
          <Switch checked={enabled} label={t('mcp.server.enabled')} onChange={setEnabled} />
        </Row>
      </div>
    </Sheet>
  );
}

// ---------------------------------------------------------------- calls

function Calls() {
  const { t, locale } = useI18n();
  const calls = useMcpCalls(50);
  return (
    <Group title={t('mcp.calls.title')} footer={t('mcp.calls.hint')}>
      {calls.data === undefined ? (
        <div className="p-4"><Spinner /></div>
      ) : calls.data.length === 0 ? (
        <div className="px-4 py-6 text-center text-[12px] text-[var(--text-muted)]">{t('mcp.calls.empty')}</div>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full text-left text-[12px]">
            <thead className="text-[var(--text-muted)]">
              <tr>
                {(['time', 'token', 'tool', 'duration', 'result'] as const).map((c) => (
                  <th key={c} className="px-4 py-2 font-normal">{t(`mcp.calls.${c}` as MessageKey)}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {calls.data.map((c) => <CallRow key={c.id} call={c} locale={locale} />)}
            </tbody>
          </table>
        </div>
      )}
    </Group>
  );
}

function CallRow({ call: c, locale }: { call: McpCall; locale: string }) {
  const { t } = useI18n();
  return (
    <tr className="border-t border-[var(--border)]">
      <td className="whitespace-nowrap px-4 py-1.5 text-[var(--text-secondary)]">{formatDateTime(c.at, locale)}</td>
      <td className="px-4 py-1.5">{c.tokenName ?? c.tokenId}</td>
      <td className="px-4 py-1.5 font-mono">
        {c.serverSlug}<span className="text-[var(--text-muted)]">/</span>{c.tool}
        {c.frontEra && <span className="ml-2 text-[10.5px] text-[var(--text-muted)]">{t(`mcp.calls.era.${c.frontEra}` as MessageKey)}</span>}
      </td>
      <td className="whitespace-nowrap px-4 py-1.5 tabular-nums text-[var(--text-secondary)]">{formatMs(c.durationMs)}</td>
      <td className="px-4 py-1.5">
        {c.ok ? <Badge tone="green">{t('mcp.calls.ok')}</Badge> : <Badge tone="red">{t(`mcp.calls.error.${c.errorCategory ?? 'protocol'}` as MessageKey)}</Badge>}
      </td>
    </tr>
  );
}
