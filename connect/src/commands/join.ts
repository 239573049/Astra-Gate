import type { ConfigChangePlan, IClientAdapter } from '@aidotnet/client-config';
import { buildContext, clientName, defaultModelFor, detectClients, type DetectedClient } from '../lib/clients.js';
import {
  GatewayError,
  fetchConnectContext,
  fetchKeyInfo,
  isInsecure,
  normalizeGatewayUrl,
  type ConnectContext,
  type KeyInfo,
} from '../lib/gateway.js';
import { UserError, type Io } from '../lib/io.js';
import { localAstraRuns, readProfile, writeProfile, type Profile } from '../lib/profile.js';
import type { Runtime } from '../lib/runtime.js';
import { ask, prompts as p, redact, type Ui } from '../lib/ui.js';
import { msg } from '../messages.js';

export interface JoinOptions {
  clients?: string;
  model?: string;
  yes?: boolean;
  keyStdin?: boolean;
}

export type JoinClientStatus = 'applied' | 'unchanged' | 'failed';

export interface JoinResult {
  gatewayUrl: string;
  name: string;
  clients: Array<{ kind: string; name: string; status: JoinClientStatus; files: string[]; error?: string }>;
}

/** Asks for the key (or reads it from ASTRA_API_KEY / stdin) and checks it against the gateway. */
async function resolveKey(
  rt: Runtime,
  ui: Ui,
  io: Io,
  gatewayUrl: string,
  saved: Profile | null,
  opts: JoinOptions,
): Promise<{ key: string; info: KeyInfo }> {
  const fixed = opts.keyStdin ? (await io.readStdin()).trim() : io.env.ASTRA_API_KEY?.trim();
  let candidate = fixed || null;
  if (!candidate && saved && saved.gatewayUrl === gatewayUrl && ui.interactive) {
    const reuse = ask(await p.confirm({ message: msg.useSavedKey, initialValue: true }));
    if (reuse) candidate = saved.apiKey;
  }
  for (let attempt = 0; ; attempt++) {
    if (!candidate) {
      if (!ui.interactive) throw new UserError(msg.keyMissing);
      candidate = ask(
        await p.password({ message: `${msg.askKey}（${msg.keyHint}）`, validate: (v) => (v?.trim() ? undefined : '请输入 Key') }),
      ).trim();
    }
    const spin = ui.spinner();
    spin.start(msg.connecting);
    try {
      const info = await fetchKeyInfo(gatewayUrl, candidate, rt.fetch);
      spin.stop(msg.verified(info.name));
      return { key: candidate, info };
    } catch (e) {
      spin.stop('');
      if (e instanceof GatewayError && e.kind === 'unauthorized' && ui.interactive && !fixed && attempt < 2) {
        ui.warn(`${e.message} ${msg.retryKey}`);
        candidate = null;
        continue;
      }
      throw e instanceof GatewayError ? new UserError(e.message) : e;
    }
  }
}

const parseClientList = (value: string, adapters: readonly IClientAdapter[], detected: DetectedClient[]): IClientAdapter[] => {
  if (value === 'all') return detected.filter((d) => d.detection.detected).map((d) => d.adapter);
  return value
    .split(',')
    .map((s) => s.trim())
    .filter(Boolean)
    .map((kind) => adapters.find((a) => a.kind === kind) ?? failUnknown(kind));
};

const failUnknown = (kind: string): never => {
  throw new UserError(msg.unknownClient(kind));
};

function describePlan(name: string, plan: ConfigChangePlan): string {
  const files = [...new Set(plan.changes.map((c) => c.file))];
  return `${name}  ${files.join(', ')}  (${plan.changes.length} 项)`;
}

export async function join(
  rt: Runtime,
  ui: Ui,
  io: Io,
  urlArg: string | undefined,
  opts: JoinOptions,
): Promise<JoinResult> {
  const interactive = ui.interactive;
  ui.intro(msg.intro);

  const saved = readProfile(rt.paths);
  let urlInput = urlArg ?? (saved ? saved.gatewayUrl : undefined);
  if (!urlInput || (interactive && !urlArg && !opts.yes)) {
    if (!interactive) throw new UserError(msg.urlMissing);
    urlInput = ask(
      await p.text({
        message: msg.askUrl,
        placeholder: msg.urlPlaceholder,
        initialValue: urlInput ?? '',
        validate: (v) => {
          try {
            normalizeGatewayUrl(v ?? '');
            return undefined;
          } catch (e) {
            return (e as Error).message;
          }
        },
      }),
    );
  }
  let gatewayUrl: string;
  try {
    gatewayUrl = normalizeGatewayUrl(urlInput);
  } catch (e) {
    throw new UserError((e as Error).message);
  }
  if (isInsecure(gatewayUrl)) ui.warn(msg.httpWarning);

  const { key, info } = await resolveKey(rt, ui, io, gatewayUrl, saved, opts);

  let context: ConnectContext;
  try {
    context = await fetchConnectContext(gatewayUrl, key, rt.fetch);
  } catch (e) {
    throw e instanceof GatewayError ? new UserError(e.message) : e;
  }
  if (!info.enabled) throw new UserError(msg.tokenDisabled);
  if (context.models.length === 0) ui.warn(msg.noModels);

  // Which clients.
  const detected = detectClients(rt.registry.all);
  const found = detected.filter((d) => d.detection.detected);
  let adapters: IClientAdapter[];
  if (opts.clients) adapters = parseClientList(opts.clients, rt.registry.all, detected);
  else if (interactive && !opts.yes) {
    if (found.length === 0) throw new UserError(msg.noClientsFound);
    const kinds = ask(
      await p.multiselect({
        message: msg.pickClients,
        options: found.map((d) => ({ value: d.adapter.kind, label: d.name, hint: d.files[0] })),
        initialValues: found.map((d) => d.adapter.kind),
        required: true,
      }),
    );
    adapters = found.filter((d) => kinds.includes(d.adapter.kind)).map((d) => d.adapter);
  } else adapters = found.map((d) => d.adapter);
  if (adapters.length === 0) throw new UserError(msg.noClientsFound);

  // Which default model: one the user names for every client, the client's own (as it is), or automatic per client.
  type ModelChoice = { mode: 'auto' } | { mode: 'fixed'; id: string } | { mode: 'keep' };
  let choice: ModelChoice = { mode: 'auto' };
  if (opts.model) {
    if (context.models.length > 0 && !context.models.some((m) => m.id === opts.model))
      throw new UserError(`模型 ${opts.model} 不在这把 Key 的可用列表里：${context.models.map((m) => m.id).join(', ')}`);
    choice = { mode: 'fixed', id: opts.model };
  } else if (interactive && !opts.yes && context.models.length > 1) {
    const picked = ask(
      await p.select({
        message: msg.pickModel,
        options: [
          { value: '\0auto', label: msg.autoModel },
          ...context.models.map((m) => ({ value: m.id, label: m.name === m.id ? m.id : `${m.name}  ${m.id}` })),
          { value: '\0keep', label: msg.keepClientModel },
        ],
        initialValue: '\0auto',
      }),
    );
    choice = picked === '\0auto' ? { mode: 'auto' } : picked === '\0keep' ? { mode: 'keep' } : { mode: 'fixed', id: picked };
  }
  const modelFor = (kind: string): string | null =>
    choice.mode === 'fixed' ? choice.id : choice.mode === 'keep' ? null : defaultModelFor(kind, context.models);

  if (localAstraRuns(rt.paths)) ui.warn(msg.localAstraWarning);

  // Plan, preview, confirm.
  const plans = adapters.map((adapter) => ({
    adapter,
    name: clientName(adapter.kind),
    plan: adapter.planEnable(buildContext(adapter.kind, gatewayUrl, key, context, modelFor(adapter.kind))),
  }));
  const changing = plans.filter((x) => x.plan.diffs.length > 0);
  if (changing.length > 0) {
    ui.info(
      msg.willChange(
        new Set(changing.flatMap((x) => x.plan.changes.map((c) => c.file))).size,
        changing.reduce((n, x) => n + x.plan.changes.length, 0),
      ),
    );
    ui.note(changing.map((x) => describePlan(x.name, x.plan)).join('\n'));
    if (interactive && !opts.yes) {
      for (;;) {
        const action = ask(
          await p.select({
            message: msg.confirm,
            options: [
              { value: 'apply', label: '确认写入' },
              { value: 'diff', label: msg.showDiff },
              { value: 'cancel', label: '取消' },
            ],
          }),
        );
        if (action === 'diff') {
          for (const x of changing)
            ui.note(redact(x.plan.diffs.map((d) => d.unifiedDiff).join('\n'), key), x.name);
          continue;
        }
        if (action === 'cancel') {
          p.cancel(msg.cancelled);
          throw new UserError(msg.cancelled, 130);
        }
        break;
      }
    }
  } else ui.info(msg.nothingToChange);

  // Apply.
  const result: JoinResult = { gatewayUrl, name: info.name, clients: [] };
  for (const x of plans) {
    const files = [...new Set(x.plan.changes.map((c) => c.file))];
    if (x.plan.diffs.length === 0) {
      result.clients.push({ kind: x.adapter.kind, name: x.name, status: 'unchanged', files });
      continue;
    }
    try {
      rt.applier.apply(x.plan);
      result.clients.push({ kind: x.adapter.kind, name: x.name, status: 'applied', files });
      ui.success(msg.applied(x.name));
    } catch (e) {
      const detail = (e as Error).message;
      result.clients.push({ kind: x.adapter.kind, name: x.name, status: 'failed', files, error: detail });
      ui.error(msg.applyFailed(x.name, detail));
    }
  }

  const joined = result.clients.filter((c) => c.status !== 'failed').map((c) => c.kind);
  if (joined.length > 0) {
    const now = new Date().toISOString();
    writeProfile(rt.paths, {
      gatewayUrl,
      apiKey: key,
      name: info.name,
      model: choice.mode === 'fixed' ? choice.id : null,
      clients: [...new Set([...(saved?.clients ?? []), ...joined])],
      joinedAt: saved?.joinedAt ?? now,
      updatedAt: now,
    });
    ui.outro(msg.restartHint);
  }
  if (result.clients.some((c) => c.status === 'failed')) process.exitCode = 1;
  return result;
}
