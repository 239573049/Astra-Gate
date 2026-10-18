import type { IClientAdapter, IClientConfigStateStore } from '../contracts.js';
import type { ClientEnvironment } from '../env.js';
import { GrokBuildClientAdapter } from './grok-build.js';
import { HermesAgentClientAdapter } from './hermes-agent.js';
import { KimiCodeClientAdapter } from './kimi-code.js';
import { MiniMaxCodeClientAdapter } from './minimax-code.js';
import { MuseCodeClientAdapter } from './muse-code.js';

/** Adapter group 4: see .plan/enterprise-connect.md (porting split). */
export function group4(env: ClientEnvironment, store: IClientConfigStateStore): IClientAdapter[] {
  return [
    new KimiCodeClientAdapter(env, store),
    new MiniMaxCodeClientAdapter(env, store),
    new GrokBuildClientAdapter(env, store),
    new HermesAgentClientAdapter(env, store),
    new MuseCodeClientAdapter(env, store),
  ];
}
