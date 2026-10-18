import type { IClientAdapter, IClientConfigStateStore } from '../contracts.js';
import type { ClientEnvironment } from '../env.js';

/** Adapter group 3: see .plan/enterprise-connect.md (porting split). */
export function group3(_env: ClientEnvironment, _store: IClientConfigStateStore): IClientAdapter[] {
  return [];
}
