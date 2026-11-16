import path from 'node:path';
import { ClientAdapterBase } from '../adapter-base.js';
import {
  ConfigChange,
  ClientDetections,
  type ClientDetection,
  type ClientStatus,
  type ConfigChangePlan,
  type EnableContext,
} from '../contracts.js';
import { toJsonText } from '../editing/jsonc-editor.js';
import { ClientKinds } from '../domain/kinds.js';
import { listState } from './shell-rc.js';

const PROVIDER_ID = 'astra';
const PROVIDER_PATH = `models.providers.${PROVIDER_ID}`;
const MODEL_PATH = 'agents.defaults.model';
const ALLOW_PATH = 'agents.defaults.modelPolicy.allow';
/** What OpenClaw is told of a model whose context window is not known. */
const DEFAULT_CONTEXT_WINDOW = 128000;
const PROFILE = /^[a-z0-9][a-z0-9_-]*$/i;

const parse = (text: string | null): unknown => {
  if (text === null) return null;
  try {
    return JSON.parse(text) as unknown;
  } catch {
    return null;
  }
};
const isPlainObject = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v);

/**
 * OpenClaw (openclaw/openclaw; Coexist): one `openclaw.json` (`OPENCLAW_CONFIG_PATH`, else in `OPENCLAW_STATE_DIR`, else
 * `~/.openclaw-<profile>`, else `~/.openclaw`; `~` is `OPENCLAW_HOME` when set). Adds `models.providers.astra` and, when a model is chosen, makes
 * `astra/<model>` the primary of `agents.defaults.model` and adds `astra/*` to a non-empty `agents.defaults.modelPolicy.allow`.
 * (Port of OpenClawClientAdapter.)
 */
export class OpenClawClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.OpenClaw;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private tilde(p: string): string {
    if (p === '~') return this.env.homeDirectory;
    if (p.startsWith('~/') || p.startsWith('~\\')) return path.join(this.env.homeDirectory, p.slice(2));
    return p;
  }

  private get home(): string {
    const h = this.env.getEnvironmentVariable('OPENCLAW_HOME')?.trim();
    return !h || h === 'undefined' || h === 'null' ? this.env.homeDirectory : this.tilde(h);
  }

  private get stateDir(): string {
    const home = this.home;
    let dir = path.join(home, '.openclaw');
    const profile = this.env.getEnvironmentVariable('OPENCLAW_PROFILE')?.trim();
    if (profile && profile.toLowerCase() !== 'default' && profile.length <= 64 && PROFILE.test(profile))
      dir = path.join(home, '.openclaw-' + profile);
    const state = this.env.getEnvironmentVariable('OPENCLAW_STATE_DIR')?.trim();
    if (state) dir = this.tilde(state);
    return dir;
  }

  private get configFile(): string {
    const p = this.env.getEnvironmentVariable('OPENCLAW_CONFIG_PATH')?.trim();
    return p ? this.tilde(p) : path.join(this.stateDir, 'openclaw.json');
  }

  detect(): ClientDetection {
    const dir = this.stateDir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `state directory ${dir}`);
    const exe = this.env.findOnPath('openclaw');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no openclaw executable on PATH`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const hints = ClientAdapterBase.modelHints(ctx);
    const models: Array<Record<string, unknown>> = [];
    for (const { id } of ClientAdapterBase.modelEntries(ctx)) {
      const info = hints.get(id);
      models.push({
        id,
        name: id,
        reasoning: ClientAdapterBase.boolHint(info?.reasoning) ?? false,
        input: ClientAdapterBase.boolHint(info?.vision) === true ? ['text', 'image'] : ['text'],
        contextWindow: ClientAdapterBase.positiveLong(info?.contextWindow) ?? DEFAULT_CONTEXT_WINDOW,
      });
    }
    const provider = {
      baseUrl: ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1',
      apiKey: ctx.localKey,
      api: 'openai-completions',
      models,
    };
    const changes: ConfigChange[] = [
      new ConfigChange(file, 'json', PROVIDER_PATH, this.currentValue(file, 'json', PROVIDER_PATH), toJsonText(provider)),
    ];
    if (ctx.model) {
      const reference = `${PROVIDER_ID}/${ctx.model}`;
      const current = this.currentValue(file, 'json', MODEL_PATH);
      const existing = parse(current);
      let after: string;
      if (isPlainObject(existing)) after = toJsonText({ ...existing, primary: reference });
      else after = ClientAdapterBase.jsonString(reference);
      changes.push(new ConfigChange(file, 'json', MODEL_PATH, current, after));

      const allowText = this.currentValue(file, 'json', ALLOW_PATH);
      const allow = parse(allowText);
      if (
        Array.isArray(allow) &&
        allow.length > 0 &&
        !allow.some((a) => typeof a === 'string' && (a === '*' || a === reference || a === `${PROVIDER_ID}/*`))
      )
        changes.push(new ConfigChange(file, 'json', ALLOW_PATH, allowText, toJsonText([...allow, `${PROVIDER_ID}/*`])));
    }
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const changes: ConfigChange[] = [];
    for (const entry of listState(this.store, this.kind)) {
      const current = this.currentValue(entry.filePath, 'json', entry.keyPath);
      if (current !== null) changes.push(new ConfigChange(entry.filePath, 'json', entry.keyPath, current, null));
    }
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const enabled = this.stillOurs(this.configFile, 'json', PROVIDER_PATH);
    return this.buildStatus(
      detection,
      enabled,
      enabled
        ? [
            'OpenClaw takes this up without a restart. A chat that already has a model keeps it: send /model default in that chat to move it onto Astra.',
          ]
        : [],
    );
  }
}
