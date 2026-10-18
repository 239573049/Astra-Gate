import type { IClientAdapter, IClientConfigStateStore } from './contracts.js';
import type { ClientEnvironment } from './env.js';
import { ALL_CLIENT_KINDS } from './domain/kinds.js';
import { ClaudeCodeClientAdapter } from './adapters/claude-code.js';
import { group1 } from './adapters/group-1.js';
import { group2 } from './adapters/group-2.js';
import { group3 } from './adapters/group-3.js';
import { group4 } from './adapters/group-4.js';
import { group5 } from './adapters/group-5.js';
import { group6 } from './adapters/group-6.js';

/** The client adapters ported so far, in ClientKinds order (see the golden coverage test). */
export class ClientAdapterRegistry {
  constructor(readonly all: readonly IClientAdapter[]) {}

  static createDefault(env: ClientEnvironment, store: IClientConfigStateStore): ClientAdapterRegistry {
    const adapters = [
      new ClaudeCodeClientAdapter(env, store),
      ...group1(env, store),
      ...group2(env, store),
      ...group3(env, store),
      ...group4(env, store),
      ...group5(env, store),
      ...group6(env, store),
    ];
    const order = (kind: string) => ALL_CLIENT_KINDS.indexOf(kind);
    return new ClientAdapterRegistry(adapters.sort((a, b) => order(a.kind) - order(b.kind)));
  }

  get(kind: string): IClientAdapter | undefined {
    return this.all.find((a) => a.kind === kind);
  }

  require(kind: string): IClientAdapter {
    const adapter = this.get(kind);
    if (!adapter) throw new Error(`Unknown client kind '${kind}'.`);
    return adapter;
  }
}
