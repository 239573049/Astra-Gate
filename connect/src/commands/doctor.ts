import { clientName } from '../lib/clients.js';
import { GatewayError, fetchConnectContext, fetchKeyInfo } from '../lib/gateway.js';
import { localAstraRuns, readProfile } from '../lib/profile.js';
import type { Runtime } from '../lib/runtime.js';
import type { Ui } from '../lib/ui.js';
import { msg } from '../messages.js';

export interface DoctorResult {
  ok: boolean;
  problems: string[];
}

/** Checks the connection and every client the profile joined; prints what to do about each problem. */
export async function doctor(rt: Runtime, ui: Ui): Promise<DoctorResult> {
  const problems: string[] = [];
  const profile = readProfile(rt.paths);
  if (!profile) {
    ui.error(msg.notJoined);
    return { ok: false, problems: [msg.notJoined] };
  }
  const problem = (text: string) => {
    problems.push(text);
    ui.error(text);
  };

  try {
    const info = await fetchKeyInfo(profile.gatewayUrl, profile.apiKey, rt.fetch);
    ui.success(`网关可连通，Key 有效（${info.name}）`);
    if (!info.enabled) problem(msg.tokenDisabled);
    for (const l of info.limits) if (l.exhausted) problem(`额度已用完：${l.period}`);
    const context = await fetchConnectContext(profile.gatewayUrl, profile.apiKey, rt.fetch);
    if (context.models.length === 0) problem(msg.noModels);
    else ui.success(`可用模型 ${context.models.length} 个`);
  } catch (e) {
    problem(e instanceof GatewayError ? e.message : (e as Error).message);
  }

  if (localAstraRuns(rt.paths)) ui.warn(msg.localAstraWarning);

  for (const kind of profile.clients) {
    const adapter = rt.registry.get(kind);
    const name = clientName(kind);
    if (!adapter) {
      problem(`${name}：当前版本不支持，无法检查`);
      continue;
    }
    const status = adapter.inspect();
    if (status.driftedKeys.length > 0) problem(`${name}：配置被改动过（${status.driftedKeys.join(', ')}），重新运行 astra-connect join 可修复`);
    else if (!status.enabled) problem(`${name}：配置没有指向企业网关，重新运行 astra-connect join`);
    else ui.success(`${name}：配置正常`);
    for (const r of status.repairs) problem(`${name}：${r.file} 第 ${r.line} 行含有客户端读不了的写法（${r.problem}）`);
    for (const w of status.warnings) ui.warn(`${name}：${w}`);
  }
  return { ok: problems.length === 0, problems };
}
