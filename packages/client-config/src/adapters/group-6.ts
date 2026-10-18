import type { IClientAdapter, IClientConfigStateStore } from '../contracts.js';
import type { ClientEnvironment } from '../env.js';
import { ClaudeDesktopClientAdapter } from './claude-desktop.js';
import { DeepSeekHarnessClientAdapter } from './deepseek-harness.js';

/** Adapter group 6: see .plan/enterprise-connect.md (porting split). */
export function group6(env: ClientEnvironment, store: IClientConfigStateStore): IClientAdapter[] {
  return [new DeepSeekHarnessClientAdapter(env, store), new ClaudeDesktopClientAdapter(env, store)];
}
