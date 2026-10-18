import type { IClientAdapter, IClientConfigStateStore } from '../contracts.js';
import type { ClientEnvironment } from '../env.js';
import { CodexClientAdapter } from './codex.js';
import { GeminiCliClientAdapter } from './gemini-cli.js';
import { ZedClientAdapter } from './zed.js';

/** Adapter group 1: see .plan/enterprise-connect.md (porting split). */
export function group1(env: ClientEnvironment, store: IClientConfigStateStore): IClientAdapter[] {
  return [
    new CodexClientAdapter(env, store),
    new GeminiCliClientAdapter(env, store),
    new ZedClientAdapter(env, store),
  ];
}
