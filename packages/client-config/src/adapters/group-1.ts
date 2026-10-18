import type { IClientAdapter, IClientConfigStateStore } from '../contracts.js';
import type { ClientEnvironment } from '../env.js';

/** Adapter group 1: see .plan/enterprise-connect.md (porting split). */
export function group1(_env: ClientEnvironment, _store: IClientConfigStateStore): IClientAdapter[] {
  return [];
}
