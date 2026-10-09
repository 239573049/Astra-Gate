import { describe, expect, it } from 'vitest';

import type { ClientInfo, ClientKind } from '../api/types';
import { filterableClients, isClientInstalled } from '../lib/clientFilter';

const client = (kind: ClientKind, over: Partial<ClientInfo> & { installed?: boolean; probed?: boolean } = {}): ClientInfo =>
  ({
    kind,
    enabled: over.enabled ?? false,
    detection: { installed: over.installed ?? false, configExists: false, configPaths: [] },
    install: { installed: over.probed ?? false },
  }) as unknown as ClientInfo;

const ORDER: ClientKind[] = ['codex', 'claude-code', 'crush', 'zed'];

describe('overview client filter', () => {
  it('offers only installed clients, in display order', () => {
    const clients = [client('zed', { installed: true }), client('codex', { probed: true }), client('crush'), client('claude-code', { enabled: true })];
    expect(filterableClients(clients, ORDER)).toEqual(['codex', 'claude-code', 'zed']);
  });

  it('is empty while the client list is loading or when nothing is installed', () => {
    expect(filterableClients(undefined, ORDER)).toEqual([]);
    expect(filterableClients([client('codex'), client('zed')], ORDER)).toEqual([]);
  });

  it('keeps the selected client even when it is not installed', () => {
    expect(filterableClients([client('codex', { installed: true }), client('crush')], ORDER, 'crush')).toEqual(['codex', 'crush']);
    expect(filterableClients([client('crush')], ORDER, 'all')).toEqual([]);
  });

  it('tolerates clients without detection data', () => {
    expect(isClientInstalled({ kind: 'codex' } as ClientInfo)).toBe(false);
  });
});
