// The user's own MCP servers (issue #34): a local stdio command or a remote address, defined once; choose which clients get it. Astra
// writes the entry into the client's own MCP list; the client starts or reaches it, Astra never runs or proxies it. Environment and
// header values (often API keys) go to the server and into the client files only: the list shows their names. Existing entries in the
// clients can be imported. Changing a server is refused off this machine.

import { Download, Pencil, Plus, Terminal, Trash2 } from 'lucide-react';
import { useEffect, useState } from 'react';

import { useApplyLocalMcp, useCreateLocalMcp, useDeleteLocalMcp, useImportLocalMcp, useLocalMcpCandidates, useLocalMcpServers, useUpdateLocalMcp } from '../api/hooks';
import type { ClientKind, ClientMcpState, LocalMcpPlacement, LocalMcpServer, LocalMcpServerInput, LocalMcpTransport } from '../api/types';
import { useI18n, type MessageKey } from '../i18n';
import { Alert } from './arc/alert/alert';
import { Badge, Button, EmptyState, Field, Input, Row, Segmented, Spinner, Switch, TextArea } from './ui/controls';
import { errorText, Sheet, useFeedback } from './ui/overlays';

const CLIENTS: { kind: ClientKind; name: string }[] = [
  { kind: 'claude-code', name: 'Claude Code' },
  { kind: 'codex', name: 'Codex' },
  { kind: 'gemini-cli', name: 'Gemini CLI' },
  { kind: 'qwen-code', name: 'Qwen Code' },
  { kind: 'opencode', name: 'OpenCode' },
];

const STATE_TONE: Record<ClientMcpState, 'neutral' | 'green' | 'orange' | 'red'> = {
  missing: 'neutral',
  applied: 'green',
  drifted: 'orange',
  conflict: 'red',
};

const clientName = (kind: string) => CLIENTS.find((c) => c.kind === kind)?.name ?? kind;

/** One argument per line; blank lines are dropped. */
export const parseArgs = (text: string): string[] => text.split('\n').map((l) => l.replace(/\r$/, '')).filter((l) => l.trim() !== '');

/** `NAME=value` per line; null when a non-blank line has no name. */
export function parseEnv(text: string): Record<string, string> | null {
  const env: Record<string, string> = {};
  for (const raw of text.split('\n')) {
    const line = raw.replace(/\r$/, '');
    if (line.trim() === '') continue;
    const eq = line.indexOf('=');
    if (eq <= 0) return null;
    env[line.slice(0, eq).trim()] = line.slice(eq + 1);
  }
  return env;
}

/** `Name: value` per line; null when a non-blank line has no name. */
export function parseHeaders(text: string): Record<string, string> | null {
  const headers: Record<string, string> = {};
  for (const raw of text.split('\n')) {
    const line = raw.replace(/\r$/, '');
    if (line.trim() === '') continue;
    const colon = line.indexOf(':');
    if (colon <= 0) return null;
    headers[line.slice(0, colon).trim()] = line.slice(colon + 1).trim();
  }
  return headers;
}

export function LocalMcpServers() {
  const { t } = useI18n();
  const { toast, confirm } = useFeedback();
  const servers = useLocalMcpServers();
  const apply = useApplyLocalMcp();
  const remove = useDeleteLocalMcp();
  const [editing, setEditing] = useState<LocalMcpServer | 'new' | null>(null);
  const [importing, setImporting] = useState(false);

  const del = async (s: LocalMcpServer) => {
    if (!(await confirm({ title: t('mcp.local.deleteTitle', { name: s.name }), detail: t('mcp.local.deleteDetail'), destructive: true, confirmLabel: t('common.delete') }))) return;
    remove.mutate({ id: s.id }, { onSuccess: () => toast(t('mcp.local.deleted', { name: s.name }), 'success'), onError: (e) => toast(errorText(e), 'error') });
  };

  const rewrite = (s: LocalMcpServer, force: boolean) =>
    apply.mutate({ id: s.id, force }, { onSuccess: () => toast(t('mcp.local.applied', { name: s.name }), 'success'), onError: (e) => toast(errorText(e), 'error') });

  return (
    <section className="mt-6">
      <div className="mb-2 flex items-center justify-between gap-3 px-1">
        <div>
          <h3 className="text-[13px] font-semibold">{t('mcp.local.title')}</h3>
          <p className="text-[12px] text-[var(--text-muted)]">{t('mcp.local.hint')}</p>
        </div>
        <div className="flex shrink-0 gap-2">
          <Button icon={<Download className="size-4" />} onClick={() => setImporting(true)}>
            {t('mcp.local.import')}
          </Button>
          <Button variant="primary" icon={<Plus className="size-4" />} onClick={() => setEditing('new')}>
            {t('mcp.local.add')}
          </Button>
        </div>
      </div>
      {servers.isLoading ? (
        <Spinner />
      ) : !servers.data || servers.data.length === 0 ? (
        <EmptyState icon={<Terminal className="size-6" />} title={t('mcp.local.emptyTitle')} detail={t('mcp.local.emptyDetail')} />
      ) : (
        <div className="card divide-y divide-[var(--border)]">
          {servers.data.map((s) => (
            <LocalRow key={s.id} server={s} busy={apply.isPending || remove.isPending} onEdit={() => setEditing(s)} onDelete={() => void del(s)} onRewrite={(force) => rewrite(s, force)} />
          ))}
        </div>
      )}
      <p className="mt-2 px-1 text-[11px] text-[var(--text-muted)]">{t('mcp.local.footer')}</p>
      <LocalSheet server={editing} onClose={() => setEditing(null)} />
      <ImportSheet open={importing} onClose={() => setImporting(false)} />
    </section>
  );
}

function LocalRow({
  server: s, busy, onEdit, onDelete, onRewrite,
}: {
  server: LocalMcpServer; busy: boolean; onEdit: () => void; onDelete: () => void; onRewrite: (force: boolean) => void;
}) {
  const { t } = useI18n();
  const http = s.transport === 'http';
  const summary = http ? s.url : [s.command, ...s.args].join(' ');
  const extras = http ? s.headerNames.length : s.envNames.length;
  const needsAttention = s.placements.some((p) => p.wanted && p.state !== 'applied');
  return (
    <div className="px-1">
      <Row
        label={
          <span className="flex items-center gap-2">
            <span className="font-mono">{s.name}</span>
            {!s.enabled && <Badge tone="neutral">{t('mcp.local.off')}</Badge>}
          </span>
        }
        detail={
          <span className="block truncate font-mono text-[11px]" title={summary}>
            {summary}
            {extras > 0 && <span className="text-[var(--text-muted)]"> · {t(http ? 'mcp.local.headerCount' : 'mcp.local.envCount', { count: extras })}</span>}
          </span>
        }
      >
        {needsAttention && (
          <Button size="sm" disabled={busy} onClick={() => onRewrite(false)}>
            {t('mcp.local.rewrite')}
          </Button>
        )}
        <Button size="sm" icon={<Pencil className="size-3.5" />} onClick={onEdit} aria-label={t('common.edit')} />
        <Button size="sm" icon={<Trash2 className="size-3.5" />} disabled={busy} onClick={onDelete} aria-label={t('common.delete')} />
      </Row>
      <div className="flex flex-wrap gap-3 px-4 pb-3">
        {s.placements.map((p) => (
          <PlacementChip key={p.kind} placement={p} onForce={() => onRewrite(true)} />
        ))}
      </div>
    </div>
  );
}

function PlacementChip({ placement: p, onForce }: { placement: LocalMcpPlacement; onForce: () => void }) {
  const { t } = useI18n();
  // not wanted and not there: nothing to say
  if (!p.wanted && p.state === 'missing') return null;
  return (
    <span className="flex flex-col gap-1">
      <span className="flex items-center gap-1.5 text-[12px]" title={p.file}>
        {clientName(p.kind)}
        <Badge tone={STATE_TONE[p.state]}>{t(`mcp.local.state.${p.state}` as MessageKey)}</Badge>
        {p.state === 'drifted' && p.wanted && (
          <Button size="sm" onClick={onForce}>
            {t('mcp.clients.force')}
          </Button>
        )}
      </span>
      {p.error && <span className="max-w-md whitespace-normal text-[11px] text-[var(--danger)]">{p.error}</span>}
    </span>
  );
}

function LocalSheet({ server, onClose }: { server: LocalMcpServer | 'new' | null; onClose: () => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const create = useCreateLocalMcp();
  const update = useUpdateLocalMcp();
  const existing = server && server !== 'new' ? server : null;
  const [name, setName] = useState('');
  const [command, setCommand] = useState('');
  const [args, setArgs] = useState('');
  const [env, setEnv] = useState('');
  const [transport, setTransport] = useState<LocalMcpTransport>('stdio');
  const [url, setUrl] = useState('');
  const [headers, setHeaders] = useState('');
  const [clients, setClients] = useState<ClientKind[]>([]);
  const [enabled, setEnabled] = useState(true);

  useEffect(() => {
    if (server === null) return;
    const s = server === 'new' ? null : server;
    setName(s?.name ?? '');
    setCommand(s?.command ?? '');
    setArgs((s?.args ?? []).join('\n'));
    setEnv(''); // stored values are never sent to the browser
    setHeaders('');
    setTransport(s?.transport ?? 'stdio');
    setUrl(s?.url ?? '');
    setClients(s?.clients ?? CLIENTS.map((c) => c.kind));
    setEnabled(s?.enabled ?? true);
  }, [server]);

  const http = transport === 'http';
  const keptEnv = existing?.transport === 'stdio' && transport === 'stdio' && existing.envNames.length > 0;
  const keptHeaders = existing?.transport === 'http' && transport === 'http' && existing.headerNames.length > 0;
  const parsedEnv = parseEnv(env);
  const parsedHeaders = parseHeaders(headers);
  const pending = create.isPending || update.isPending;
  const valid = name.trim() !== '' && (http ? url.trim() !== '' && parsedHeaders !== null : command.trim() !== '' && parsedEnv !== null);

  const save = () => {
    const body: LocalMcpServerInput = http
      ? { name: name.trim(), transport, url: url.trim(), enabled, clients }
      : { name: name.trim(), transport, command: command.trim(), args: parseArgs(args), enabled, clients };
    // blank keeps the stored values on update (when the type did not change); anything typed replaces them as a whole
    const kept = existing?.transport === transport;
    if (http && parsedHeaders && (headers.trim() !== '' || !kept)) body.headers = parsedHeaders;
    if (!http && parsedEnv && (env.trim() !== '' || !kept)) body.env = parsedEnv;
    const done = {
      onSuccess: (s: LocalMcpServer) => {
        onClose();
        toast(t(existing ? 'mcp.local.saved' : 'mcp.local.created', { name: s.name }), 'success');
      },
      onError: (e: unknown) => toast(errorText(e), 'error'),
    };
    if (existing) update.mutate({ id: existing.id, body }, done);
    else create.mutate(body, done);
  };

  const toggle = (kind: ClientKind, on: boolean) => setClients((cs) => (on ? [...cs.filter((k) => k !== kind), kind] : cs.filter((k) => k !== kind)));

  return (
    <Sheet
      open={server !== null}
      onOpenChange={(o) => !o && onClose()}
      width={560}
      title={existing ? t('mcp.local.editTitle', { name: existing.name }) : t('mcp.local.addTitle')}
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
        <Field label={t('mcp.local.name')} hint={t('mcp.local.nameHint')}>
          <Input mono value={name} maxLength={32} placeholder="filesystem" onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label={t('mcp.local.transport')}>
          <Segmented
            ariaLabel={t('mcp.local.transport')}
            value={transport}
            onChange={setTransport}
            items={[
              { value: 'stdio', label: t('mcp.local.transport.stdio') },
              { value: 'http', label: t('mcp.local.transport.http') },
            ]}
          />
        </Field>
        {http ? (
          <>
            <Field label={t('mcp.local.url')} hint={t('mcp.local.urlHint')}>
              <Input mono value={url} placeholder="https://mcp.example.com/mcp" onChange={(e) => setUrl(e.target.value)} />
            </Field>
            <Field label={t('mcp.local.headers')} hint={keptHeaders ? t('mcp.local.headersKept', { names: existing!.headerNames.join(', ') }) : t('mcp.local.headersHint')}>
              <TextArea mono rows={3} value={headers} placeholder="Authorization: Bearer ..." onChange={(e) => setHeaders(e.target.value)} />
            </Field>
            {parsedHeaders === null && <Alert tone="warning" title={t('mcp.local.headersInvalid')} />}
          </>
        ) : (
          <>
            <Field label={t('mcp.local.command')}>
              <Input mono value={command} placeholder="npx" onChange={(e) => setCommand(e.target.value)} />
            </Field>
            <Field label={t('mcp.local.args')} hint={t('mcp.local.argsHint')}>
              <TextArea mono rows={4} value={args} placeholder={'-y\n@modelcontextprotocol/server-filesystem\n/Users/me/docs'} onChange={(e) => setArgs(e.target.value)} />
            </Field>
            <Field label={t('mcp.local.env')} hint={keptEnv ? t('mcp.local.envKept', { names: existing!.envNames.join(', ') }) : t('mcp.local.envHint')}>
              <TextArea mono rows={3} value={env} placeholder="API_KEY=..." onChange={(e) => setEnv(e.target.value)} />
            </Field>
            {parsedEnv === null && <Alert tone="warning" title={t('mcp.local.envInvalid')} />}
          </>
        )}
        <Field label={t('mcp.local.clients')} hint={t('mcp.local.clientsHint')}>
          <div className="grid gap-1">
            {CLIENTS.map((c) => (
              <Row key={c.kind} label={c.name}>
                <Switch checked={clients.includes(c.kind)} label={c.name} onChange={(on) => toggle(c.kind, on)} />
              </Row>
            ))}
          </div>
        </Field>
        <Row label={t('mcp.local.enabled')}>
          <Switch checked={enabled} label={t('mcp.local.enabled')} onChange={setEnabled} />
        </Row>
      </div>
    </Sheet>
  );
}

function ImportSheet({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const candidates = useLocalMcpCandidates(open);
  const run = useImportLocalMcp();
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const key = (c: { kind: string; name: string }) => `${c.kind}\u0000${c.name}`;

  useEffect(() => {
    if (open) setPicked(new Set());
  }, [open]);

  const usable = (candidates.data ?? []).filter((c) => c.importable && !c.taken);
  const toggle = (k: string, on: boolean) =>
    setPicked((p) => {
      const next = new Set(p);
      if (on) next.add(k);
      else next.delete(k);
      return next;
    });

  const submit = () => {
    const items = usable.filter((c) => picked.has(key(c))).map((c) => ({ kind: c.kind, name: c.name }));
    run.mutate(items, {
      onSuccess: (r) => {
        const msg = t('mcp.local.importDone', { count: r.imported.length });
        toast(r.skipped.length > 0 ? `${msg} ${t('mcp.local.importSkipped', { count: r.skipped.length, reasons: [...new Set(r.skipped.map((x) => x.reason))].join('; ') })}` : msg, r.imported.length > 0 ? 'success' : 'error');
        onClose();
      },
      onError: (e) => toast(errorText(e), 'error'),
    });
  };

  return (
    <Sheet
      open={open}
      onOpenChange={(o) => !o && onClose()}
      width={560}
      title={t('mcp.local.importTitle')}
      description={t('mcp.local.importHint')}
      footer={
        <>
          <Button onClick={onClose}>{t('common.cancel')}</Button>
          <Button variant="primary" disabled={picked.size === 0} loading={run.isPending} onClick={submit}>
            {t('mcp.local.importRun')}
          </Button>
        </>
      }
    >
      {candidates.isLoading ? (
        <Spinner />
      ) : !candidates.data || candidates.data.length === 0 ? (
        <p className="text-[13px] text-[var(--text-muted)]">{t('mcp.local.importEmpty')}</p>
      ) : (
        <div className="grid gap-1">
          {candidates.data.map((c) => {
            const k = key(c);
            const off = !c.importable || c.taken;
            return (
              <Row
                key={k}
                label={
                  <span className="flex items-center gap-2">
                    <span className="font-mono">{c.name}</span>
                    <Badge tone="neutral">{clientName(c.kind)}</Badge>
                  </span>
                }
                detail={
                  <span className="block truncate font-mono text-[11px]" title={c.summary}>
                    {off ? (c.taken ? t('mcp.local.importTaken') : (c.reason ?? '')) : c.summary}
                  </span>
                }
              >
                <Switch checked={picked.has(k)} disabled={off} label={`${c.name} ${clientName(c.kind)}`} onChange={(on) => toggle(k, on)} />
              </Row>
            );
          })}
          <p className="mt-2 text-[11px] text-[var(--text-muted)]">{t('mcp.local.importSecrets')}</p>
        </div>
      )}
    </Sheet>
  );
}
