import type { IClientAdapter, IClientConfigStateStore } from '../contracts.js';
import type { ClientEnvironment } from '../env.js';
import { AstrBotClientAdapter } from './astrbot.js';
import { LiveAgentClientAdapter } from './liveagent.js';
import { OpenBotClientAdapter } from './openbot.js';
import { OpenClawClientAdapter } from './openclaw.js';
import { OpenMausBotClientAdapter } from './openmausbot.js';
import { QwenPawClientAdapter } from './qwenpaw.js';

/** Adapter group 10: the desktop apps, chat apps and bots of magpie-parity batch 5 (the file-based ones). */
export function group10(env: ClientEnvironment, store: IClientConfigStateStore): IClientAdapter[] {
  return [
    new LiveAgentClientAdapter(env, store),
    new OpenMausBotClientAdapter(env, store),
    new AstrBotClientAdapter(env, store),
    new QwenPawClientAdapter(env, store),
    new OpenClawClientAdapter(env, store),
    new OpenBotClientAdapter(env, store),
  ];
}
