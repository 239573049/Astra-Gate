// The MCP gateway's entry in Claude Code and Codex (issue #34, .plan/client-mcp-apply.md): pick a token that has MCP servers
// bound and write "<gateway>/mcp" plus that token into the client's own MCP settings. The server writes the key; it never
// comes back to the browser. Writing is refused off this machine, and a foreign or changed entry is never overwritten silently.

import { useState } from 'react';

import { useApplyClientMcp, useClientMcp, useRemoveClientMcp, useTokens } from '../api/hooks';
import type { ClientMcpEntry, ClientMcpState } from '../api/types';
import { useI18n, type MessageKey } from '../i18n';
import { Alert } from './arc/alert/alert';
import { Badge, Button, Group, Row, Spinner } from './ui/controls';
import { errorText, Select, useFeedback } from './ui/overlays';

const CLIENTS = [
  { kind: 'claude-code', name: 'Claude Code' },
  { kind: 'codex', name: 'Codex' },
] as const;

const STATE_TONE: Record<ClientMcpState, 'neutral' | 'green' | 'orange' | 'red'> = {
  missing: 'neutral',
  applied: 'green',
  drifted: 'orange',
  conflict: 'red',
};

export function ClientMcpEntries() {
  const { t } = useI18n();
  const entries = useClientMcp();
  const tokens = useTokens();
  // tokens that can reach MCP at all; a token with nothing bound would give the client an empty server list
  const eligible = (tokens.data ?? []).filter((tok) => tok.enabled && (tok.mcpServers?.length ?? 0) > 0);

  return (
    <Group title={t('mcp.clients.title')} footer={t('mcp.clients.stdio')}>
      <p className="px-4 pt-3 text-[12px] text-[var(--text-muted)]">{t('mcp.clients.hint')}</p>
      {entries.isLoading && <Spinner lines={2} />}
      {entries.data && eligible.length === 0 && tokens.data && <p className="px-4 py-2 text-[12px] text-[var(--text-muted)]">{t('mcp.clients.noToken')}</p>}
      {entries.data &&
        CLIENTS.map((c) => {
          const entry = entries.data.find((e) => e.kind === c.kind);
          return entry ? <ClientRow key={c.kind} name={c.name} entry={entry} tokens={eligible.map((tok) => ({ value: tok.id, label: tok.name }))} /> : null;
        })}
    </Group>
  );
}

function ClientRow({ name, entry, tokens }: { name: string; entry: ClientMcpEntry; tokens: { value: string; label: string }[] }) {
  const { t } = useI18n();
  const { toast, confirm } = useFeedback();
  const apply = useApplyClientMcp();
  const remove = useRemoveClientMcp();
  const [picked, setPicked] = useState<string | null>(null);
  const tokenId = picked ?? (tokens.some((x) => x.value === entry.tokenId) ? (entry.tokenId ?? null) : (tokens[0]?.value ?? null));
  const busy = apply.isPending || remove.isPending;
  const written = entry.state === 'applied' || entry.state === 'drifted';

  const write = (force: boolean) => {
    if (!tokenId) return;
    apply.mutate(
      { kind: entry.kind, tokenId, force },
      { onSuccess: () => toast(t('mcp.clients.applied', { client: name }), 'success'), onError: (e) => toast(errorText(e), 'error') },
    );
  };

  const take = async (force: boolean) => {
    if (force && !(await confirm({ title: t('mcp.clients.forceRemove'), detail: entry.file, destructive: true, confirmLabel: t('mcp.clients.forceRemove') }))) return;
    remove.mutate(
      { kind: entry.kind, force },
      { onSuccess: () => toast(t('mcp.clients.removed', { client: name }), 'success'), onError: (e) => toast(errorText(e), 'error') },
    );
  };

  const detail = (
    <span className="block whitespace-normal">
      <span className="block truncate font-mono text-[11px]" title={entry.file}>{entry.file}</span>
      {entry.state === 'applied' && entry.tokenName && <span className="block">{t('mcp.clients.usesToken', { name: entry.tokenName })}</span>}
      {entry.error && <span className="block text-[var(--danger)]">{t('mcp.clients.unreadable', { error: entry.error })}</span>}
    </span>
  );

  return (
    <div className="border-t border-[var(--border)] first:border-t-0">
      <Row
        label={
          <span className="flex items-center gap-2">
            {name}
            <Badge tone={STATE_TONE[entry.state]}>{t(`mcp.clients.state.${entry.state}` as MessageKey)}</Badge>
          </span>
        }
        detail={detail}
      >
        {tokens.length > 0 && (
          <Select
            className="w-44"
            ariaLabel={`${name} ${t('mcp.clients.token')}`}
            placeholder={t('mcp.clients.pickToken')}
            value={tokenId}
            onChange={setPicked}
            options={tokens}
            disabled={busy}
          />
        )}
        <Button variant="primary" disabled={busy || !tokenId || entry.state === 'conflict'} onClick={() => write(false)}>
          {written ? t('mcp.clients.replace') : t('mcp.clients.apply')}
        </Button>
        {written && (
          <Button disabled={busy} onClick={() => void take(entry.state === 'drifted')}>
            {entry.state === 'drifted' ? t('mcp.clients.forceRemove') : t('mcp.clients.remove')}
          </Button>
        )}
      </Row>
      {!entry.gatewayEnabled && entry.state !== 'missing' && <Alert tone="warning" className="mx-4 mb-3" title={t('mcp.clients.gatewayOff')} />}
      {!entry.gatewayEnabled && entry.state === 'missing' && <p className="px-4 pb-2 text-[11px] text-[var(--text-muted)]">{t('mcp.clients.gatewayOff')}</p>}
      {entry.state === 'conflict' && !entry.error && <Alert tone="warning" className="mx-4 mb-3" title={t('mcp.clients.conflict')} />}
      {entry.state === 'drifted' && (
        <div className="mx-4 mb-3 flex flex-wrap items-center gap-3">
          <span className="text-[12px] text-[var(--text-muted)]">{t('mcp.clients.drifted')}</span>
          <Button size="sm" disabled={busy || !tokenId} onClick={() => write(true)}>
            {t('mcp.clients.force')}
          </Button>
        </div>
      )}
    </div>
  );
}
