import type { IClientAdapter, IClientConfigStateStore } from '../contracts.js';
import type { ClientEnvironment } from '../env.js';
import { CopilotCliClientAdapter } from './copilot-cli.js';
import { DroidClientAdapter } from './droid.js';
import { QwenCodeClientAdapter } from './qwen-code.js';
import { VsCodeCopilotClientAdapter, VsCodeFlavours } from './vscode-copilot.js';

/** Adapter group 3: see .plan/enterprise-connect.md (porting split). */
export function group3(env: ClientEnvironment, store: IClientConfigStateStore): IClientAdapter[] {
  return [
    new VsCodeCopilotClientAdapter(env, store, VsCodeFlavours.stable),
    new VsCodeCopilotClientAdapter(env, store, VsCodeFlavours.insiders),
    new VsCodeCopilotClientAdapter(env, store, VsCodeFlavours.vscodium),
    new CopilotCliClientAdapter(env, store),
    new QwenCodeClientAdapter(env, store),
    new DroidClientAdapter(env, store),
  ];
}
