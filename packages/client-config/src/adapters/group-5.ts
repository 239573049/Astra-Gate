import type { IClientAdapter, IClientConfigStateStore } from '../contracts.js';
import type { ClientEnvironment } from '../env.js';
import { NextCoWorkClientAdapter } from './nextcowork.js';
import { PaseoClientAdapter } from './paseo.js';
import { TKCodeClientAdapter } from './tkcode.js';
import { WorkBuddyClientAdapter } from './workbuddy.js';
import { ZCodeClientAdapter } from './zcode.js';

/** Adapter group 5: see .plan/enterprise-connect.md (porting split). */
export function group5(env: ClientEnvironment, store: IClientConfigStateStore): IClientAdapter[] {
  return [
    new ZCodeClientAdapter(env, store),
    new TKCodeClientAdapter(env, store),
    new WorkBuddyClientAdapter(env, store),
    new WorkBuddyClientAdapter(env, store, true),
    new PaseoClientAdapter(env, store),
    new NextCoWorkClientAdapter(env, store),
  ];
}
