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
import {
  asObj,
  cloneJv,
  getMember,
  hasMember,
  jnum,
  jobj,
  listState,
  parseObject,
  removeMember,
  setMember,
  trimSlashes,
} from '../domain/group5-json.js';
import { ClientKinds } from '../domain/kinds.js';
import type { ApiProtocol } from '../domain/protocols.js';
import { EditorException } from '../editing/errors.js';
import { compactJson, jsonDeepEquals, JsoncEditor, type JObj, type JV } from '../editing/jsonc-editor.js';
import type { ConfigFileFormat } from '../types.js';

const PROVIDER_RULES_PATH = 'config.providerConfigRules.providerRules';
const MODEL_RULES_PATH = 'config.modelConfigRules.providerModelRules';
/** Fields of a provider rule that ZCode's settings change; Astra writes them but does not own them. */
const ZCODE_OWNED_RULE_FIELDS = ['enabled', 'config.modelOrder'];

/** The providers Astra writes, one per protocol: id, ZCode `api.type` and display name. */
const PROVIDERS: ReadonlyArray<{ protocol: ApiProtocol; id: string; apiType: string; name: string }> = [
  { protocol: 'openai-chat', id: 'astra', apiType: 'openai-chat-completions', name: 'Astra' },
  { protocol: 'openai-responses', id: 'astra-responses', apiType: 'openai-responses', name: 'Astra (Responses)' },
  { protocol: 'anthropic', id: 'astra-anthropic', apiType: 'anthropic-messages', name: 'Astra (Anthropic)' },
];

const rulePath = (providerId: string): string =>
  `${PROVIDER_RULES_PATH}.${JsoncEditor.selector(['providerId', providerId])}`;

const modelRulePath = (providerId: string, modelId: string): string =>
  `${MODEL_RULES_PATH}.${JsoncEditor.selector(['providerId', providerId], ['modelId', modelId])}`;

const isOurs = (keyPath: string): boolean =>
  keyPath.startsWith(PROVIDER_RULES_PATH + '.') || keyPath.startsWith(MODEL_RULES_PATH + '.');

const excludedFor = (keyPath: string): string[] =>
  keyPath.startsWith(PROVIDER_RULES_PATH + '.') ? ZCODE_OWNED_RULE_FIELDS : [];

/** A model id that an array selector can address (ids holding '[', ']', '=' or '&' get no model rule). */
const selectorSafe = (modelId: string): boolean => !/[[\]=&]/.test(modelId);

const parse = (json: string | null): JObj | null => parseObject(json, true);

/**
 * ZCode (zcode.z.ai; Coexist): adds personal provider rules to `~/.zcode/v2/provider_config.json`
 * (`ZCODE_DESKTOP_HOME_DIR` overrides the home), one rule per protocol (`astra`, `astra-responses`,
 * `astra-anthropic`) plus a model rule per model. ZCode's additions to a rule are carried over and not drift.
 * (Port of ZCodeClientAdapter.)
 */
export class ZCodeClientAdapter extends ClientAdapterBase {
  static readonly providerId = 'astra';

  readonly kind = ClientKinds.ZCode;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get dataDir(): string {
    const home = this.env.getEnvironmentVariable('ZCODE_DESKTOP_HOME_DIR');
    return path.join(home ? home : this.env.combine(), '.zcode', 'v2');
  }

  private get configFile(): string {
    return path.join(this.dataDir, 'provider_config.json');
  }

  detect(): ClientDetection {
    return this.env.directoryExists(this.dataDir)
      ? ClientDetections.found(null, `data directory ${this.dataDir}`)
      : ClientDetections.notFound(`no ${this.dataDir}`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  /** ZCode reads the file with JSON.parse: a comment or trailing comma makes it an empty config. */
  protected override strictJsonFiles(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    if (!this.env.fileExists(file))
      throw new EditorException(`ZCode has not created ${file} yet; start ZCode once, then enable it here.`);
    const root = trimSlashes(ctx.gatewayBaseUrl);
    const protocols = this.modelProtocols(ctx, 'openai-chat');
    const entries = ClientAdapterBase.modelEntries(ctx);
    const hints = ClientAdapterBase.modelHints(ctx);
    const changes: ConfigChange[] = [];
    const written = new Set<string>();
    for (const { protocol, id, apiType, name } of PROVIDERS) {
      const own = entries.filter((e) => protocols.get(e.id) === protocol).map((e) => e.id);
      if (protocol !== 'openai-chat' && own.length === 0) continue;
      const config = jobj([
        ['group', 'standard-personal'],
        ['access', jobj([['type', 'api-key'], ['apiKey', ctx.localKey]])],
        ['api', jobj([['type', apiType], ['baseUrl', protocol === 'anthropic' ? root : root + '/v1']])],
      ]);
      if (own.length > 0) {
        setMember(config, 'personalModelIds', [...own]);
        setMember(config, 'modelOrder', [...own]);
      }
      const rule = jobj([
        ['providerId', id],
        ['providerName', name],
        ['enabled', true],
        ['config', config],
      ]);
      changes.push(this.change(rulePath(id), rule, ZCODE_OWNED_RULE_FIELDS));
      written.add(rulePath(id));
      for (const model of own) {
        if (!selectorSafe(model)) continue;
        const p = modelRulePath(id, model);
        changes.push(this.change(p, modelRule(id, model, hints.get(model)), []));
        written.add(p);
      }
    }
    // Rules Astra wrote before that are not needed any more (a model left the provider, a protocol has no models now).
    for (const entry of listState(this.store, this.kind)) {
      if (entry.filePath !== file || !isOurs(entry.keyPath) || written.has(entry.keyPath)) continue;
      const before = this.currentValue(file, 'json', entry.keyPath);
      if (before !== null) changes.push(this.removal(entry.keyPath, before));
    }
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const file = this.configFile;
    const changes: ConfigChange[] = [];
    for (const entry of listState(this.store, this.kind)) {
      if (entry.filePath !== file || !isOurs(entry.keyPath)) continue;
      const before = this.currentValue(file, 'json', entry.keyPath);
      if (before !== null) changes.push(this.removal(entry.keyPath, before));
    }
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    return this.buildStatus(this.detect(), this.stillOurs(this.configFile, 'json', rulePath(ZCodeClientAdapter.providerId)), []);
  }

  protected override valueStillOurs(
    file: string,
    keyPath: string,
    format: ConfigFileFormat,
    current: string | null,
    appliedJson: string | null,
  ): boolean {
    return file === this.configFile && isOurs(keyPath)
      ? ownedPartsEqual(current, appliedJson, excludedFor(keyPath))
      : super.valueStillOurs(file, keyPath, format, current, appliedJson);
  }

  /**
   * The change that writes `next` at `p`. When Astra wrote the value before, what ZCode added to it since (and the
   * fields in `zcodeFields`) is carried over.
   */
  private change(p: string, next: JObj, zcodeFields: string[]): ConfigChange {
    const file = this.configFile;
    const before = this.currentValue(file, 'json', p);
    const state = this.store.get(this.kind, file, p);
    const current = state ? parse(before) : null;
    if (state && current) {
      carryOver(current, parse(state.appliedValueJson), next, zcodeFields, '');
      const config = asObj(getMember(next, 'config'));
      if (config) {
        const ids = getMember(config, 'personalModelIds');
        if (!Array.isArray(ids)) removeMember(config, 'modelOrder');
        else {
          const order = getMember(asObj(getMember(current, 'config')), 'modelOrder');
          if (Array.isArray(order)) setMember(config, 'modelOrder', mergedOrder(order, ids));
        }
      }
    }
    return new ConfigChange(file, 'json', p, before, compactJson(next), {
      stillOurs: (now, applied) => ownedPartsEqual(now, applied, zcodeFields),
    });
  }

  private removal(p: string, before: string): ConfigChange {
    return new ConfigChange(this.configFile, 'json', p, before, null, {
      stillOurs: (now, applied) => ownedPartsEqual(now, applied, excludedFor(p)),
    });
  }
}

/** A model's `providerModelRules` entry, in ZCode's own key order. */
function modelRule(providerId: string, modelId: string, info: Record<string, unknown> | undefined): JObj {
  const properties = jobj();
  const context = ClientAdapterBase.positiveLong(info?.contextWindow);
  if (context !== null) setMember(properties, 'contextWindow', jnum(context));
  const input = jobj();
  const vision = ClientAdapterBase.boolHint(info?.vision);
  if (vision !== null) setMember(input, 'supportsImage', vision);
  const pdf = ClientAdapterBase.boolHint(info?.pdf);
  if (pdf !== null) setMember(input, 'supportsPdf', pdf);
  if (input.entries.length > 0) setMember(properties, 'inputFormat', input);
  setMember(properties, 'supportsNativeWebSearch', false);
  return jobj([
    ['modelId', modelId],
    ['config', jobj([['properties', properties]])],
    ['providerId', providerId],
  ]);
}

/**
 * Whether `currentText` still holds every value Astra wrote (`appliedJson`) outside the `excluded` paths; fields ZCode
 * added are not compared. Arrays count as single values.
 */
function ownedPartsEqual(currentText: string | null, appliedJson: string | null, excluded: string[]): boolean {
  if (currentText === null || appliedJson === null) return currentText === null && appliedJson === null;
  const current = parse(currentText);
  const applied = parse(appliedJson);
  return current !== null && applied !== null && covers(current, applied, excluded, '');
}

function covers(current: JObj, applied: JObj, excluded: string[], prefix: string): boolean {
  for (const [key, value] of applied.entries) {
    const p = prefix + key;
    if (excluded.includes(p)) continue;
    const inner = asObj(value);
    if (inner) {
      const now = asObj(getMember(current, key));
      if (!now || !covers(now, inner, excluded, p + '.')) return false;
    } else if (!jsonDeepEquals(value, getMember(current, key))) return false;
  }
  return true;
}

/**
 * Copies into `next` what the current value holds that Astra did not write last time (ZCode's additions), and the
 * current value of the `zcodeFields` paths; a field Astra wrote before and no longer writes goes.
 */
function carryOver(current: JObj, previous: JObj | null, next: JObj, zcodeFields: string[], prefix: string): void {
  for (const [key, value] of current.entries) {
    const p = prefix + key;
    if (zcodeFields.includes(p)) {
      if (value !== undefined) setMember(next, key, cloneJv(value));
      continue;
    }
    const nextInner = asObj(getMember(next, key));
    const currentInner = asObj(value);
    if (nextInner && currentInner) carryOver(currentInner, asObj(getMember(previous, key)), nextInner, zcodeFields, p + '.');
    else if (!hasMember(next, key) && !(previous !== null && hasMember(previous, key))) setMember(next, key, cloneJv(value));
  }
}

/** ZCode's model order kept for the models still listed, then the new ones in Astra's order. */
function mergedOrder(order: JV[], ids: JV[]): string[] {
  const listed = ids.filter((n): n is string => typeof n === 'string');
  const kept = [...new Set(order.filter((n): n is string => typeof n === 'string').filter((s) => listed.includes(s)))];
  return [...kept, ...listed.filter((id) => !kept.includes(id))];
}
