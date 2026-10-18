import type { IClientAdapter, IClientConfigStateStore } from '../contracts.js';
import type { ClientEnvironment } from '../env.js';
import { CrushClientAdapter } from './crush.js';
import { OmpClientAdapter } from './omp.js';
import { OpenCodeClientAdapter, OpenCodeFlavours } from './opencode.js';
import { PiClientAdapter } from './pi.js';

/** Adapter group 2: see .plan/enterprise-connect.md (porting split). */
export function group2(env: ClientEnvironment, store: IClientConfigStateStore): IClientAdapter[] {
  return [
    new OpenCodeClientAdapter(env, store, OpenCodeFlavours.OpenCode),
    new OpenCodeClientAdapter(env, store, OpenCodeFlavours.MiMoCode),
    new PiClientAdapter(env, store),
    new OmpClientAdapter(env, store),
    new CrushClientAdapter(env, store),
  ];
}
