import type { Runtime } from '../lib/runtime.js';
import { clientName } from '../lib/clients.js';
import { GatewayError, fetchKeyInfo, type KeyInfo } from '../lib/gateway.js';
import { UserError } from '../lib/io.js';
import { readProfile } from '../lib/profile.js';
import { maskKey, type Ui } from '../lib/ui.js';
import { msg } from '../messages.js';

export interface StatusResult {
  gatewayUrl: string;
  name: string | null;
  key: string;
  reachable: boolean;
  error?: string;
  key_info?: KeyInfo;
  clients: Array<{ kind: string; name: string; state: 'enabled' | 'drifted' | 'not-applied'; driftedKeys: string[]; warnings: string[] }>;
}

const money = (n: number) => `$${n.toFixed(2)}`;

function limitLine(l: KeyInfo['limits'][number]): string {
  const cost = l.metric === 'cost';
  const fmt = (n: number) => (cost ? money(n) : n.toLocaleString('en-US'));
  const period = { day: '今日', week: '本周', month: '本月' }[l.period] ?? l.period;
  return `${period}${cost ? '额度' : l.metric}: ${fmt(l.used)} / ${fmt(l.limit)} (${l.usedPercent.toFixed(0)}%)${l.exhausted ? '  已用完' : ''}`;
}

export async function status(rt: Runtime, ui: Ui): Promise<StatusResult> {
  const profile = readProfile(rt.paths);
  if (!profile) throw new UserError(msg.notJoined);

  const result: StatusResult = {
    gatewayUrl: profile.gatewayUrl,
    name: profile.name,
    key: maskKey(profile.apiKey),
    reachable: false,
    clients: [],
  };
  try {
    result.key_info = await fetchKeyInfo(profile.gatewayUrl, profile.apiKey, rt.fetch);
    result.reachable = true;
  } catch (e) {
    result.error = e instanceof GatewayError ? e.message : (e as Error).message;
  }

  const kinds = new Set([...profile.clients, ...rt.registry.all.filter((a) => rt.store.list(a.kind).length > 0).map((a) => a.kind)]);
  for (const kind of kinds) {
    const adapter = rt.registry.get(kind);
    if (!adapter) continue;
    const inspected = adapter.inspect();
    result.clients.push({
      kind,
      name: clientName(kind),
      state: inspected.driftedKeys.length > 0 ? 'drifted' : inspected.enabled ? 'enabled' : 'not-applied',
      driftedKeys: inspected.driftedKeys,
      warnings: inspected.warnings,
    });
  }

  ui.info(`网关  ${profile.gatewayUrl}`);
  ui.info(`令牌  ${result.key_info?.name ?? profile.name ?? '-'}  (${result.key})`);
  if (result.key_info) {
    for (const l of result.key_info.limits) ui.info(limitLine(l));
    const month = result.key_info.usage?.month;
    if (month) ui.info(`本月用量  ${month.requests} 次请求，费用 ${money(month.costUsd)}`);
    if (!result.key_info.enabled) ui.warn(msg.tokenDisabled);
  } else ui.warn(`无法获取用量：${result.error}`);
  for (const c of result.clients) {
    const label = { enabled: '已接入', drifted: '配置被改动', 'not-applied': '未生效' }[c.state];
    const line = `${c.name}  ${label}${c.driftedKeys.length ? `  (${c.driftedKeys.join(', ')})` : ''}`;
    if (c.state === 'enabled') ui.success(line);
    else ui.warn(line);
    for (const w of c.warnings) ui.warn(`  ${w}`);
  }
  return result;
}
