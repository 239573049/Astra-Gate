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
  jnum,
  jobj,
  parseObject,
  setMember,
  textOf,
  trimSlashes,
} from '../domain/group5-json.js';
import { ClientKinds } from '../domain/kinds.js';
import type { ApiProtocol } from '../domain/protocols.js';
import { compactJson, jsonDeepEquals, type JObj, type JV } from '../editing/jsonc-editor.js';
import type { ConfigFileFormat } from '../types.js';

const OWNED_PROVIDER_FIELDS = ['name', 'api', 'baseUrl', 'apiKey'];
const OWNED_MODEL_FIELDS = ['name', 'contextWindow', 'maxTokens', 'reasoning', 'images'];
const API_FIELD = 'api';
const CHAT_API = 'chat';
const MODEL_KEY = 'model';
const INT_MAX = 2147483647;

/**
 * TKCode (github.com/AtomUICommunity/TKCode; Coexist): adds `providers.astra` to `~/.tkcode/settings.json`
 * (`TKCODE_HOME` overrides the directory) and, when a model is selected, sets the top-level `model` to
 * "astra/<id>". Astra owns only the fields it writes (ownedPartsEqual); TKCode's additions are carried over.
 * (Port of TKCodeClientAdapter.)
 */
export class TKCodeClientAdapter extends ClientAdapterBase {
  static readonly providerId = 'astra';
  private static readonly providerPath = `providers.${TKCodeClientAdapter.providerId}`;

  readonly kind = ClientKinds.TKCode;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get dataDir(): string {
    const home = this.env.getEnvironmentVariable('TKCODE_HOME');
    return home ? home : this.env.combine('.tkcode');
  }

  private get configFile(): string {
    return path.join(this.dataDir, 'settings.json');
  }

  detect(): ClientDetection {
    return this.env.directoryExists(this.dataDir)
      ? ClientDetections.found(null, `data directory ${this.dataDir}`)
      : ClientDetections.notFound(`no ${this.dataDir}`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const provider = jobj([
      ['name', 'Astra'],
      [API_FIELD, CHAT_API],
      ['baseUrl', trimSlashes(ctx.gatewayBaseUrl) + '/v1'],
      ['apiKey', ctx.localKey],
    ]);
    const models = this.modelsJson(ctx);
    // Without a models list TKCode asks the gateway's GET /v1/models and enables everything it returns.
    if (models.entries.length > 0) provider.entries.push(['models', models]);
    const before = this.currentValue(file, 'json', TKCodeClientAdapter.providerPath);
    // What TKCode added to the entry Astra wrote stays; a provider Astra never wrote is replaced.
    if (this.store.get(this.kind, file, TKCodeClientAdapter.providerPath) !== null) {
      const current = parseObject(before);
      if (current) carryOver(current, provider);
    }
    const changes: ConfigChange[] = [
      new ConfigChange(file, 'json', TKCodeClientAdapter.providerPath, before, compactJson(provider), {
        stillOurs: ownedPartsEqual,
      }),
    ];
    if (ctx.model)
      changes.push(
        new ConfigChange(
          file,
          'json',
          MODEL_KEY,
          this.currentValue(file, 'json', MODEL_KEY),
          ClientAdapterBase.jsonString(`${TKCodeClientAdapter.providerId}/${ctx.model}`),
        ),
      );
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const file = this.configFile;
    const changes: ConfigChange[] = [];
    const provider = this.currentValue(file, 'json', TKCodeClientAdapter.providerPath);
    if (provider !== null)
      changes.push(
        new ConfigChange(file, 'json', TKCodeClientAdapter.providerPath, provider, null, { stillOurs: ownedPartsEqual }),
      );
    const model = this.currentValue(file, 'json', MODEL_KEY);
    if (model !== null && ClientAdapterBase.stringOf(model)?.startsWith(TKCodeClientAdapter.providerId + '/') === true)
      changes.push(new ConfigChange(file, 'json', MODEL_KEY, model, null));
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    return this.buildStatus(
      this.detect(),
      this.stillOurs(this.configFile, 'json', TKCodeClientAdapter.providerPath),
      [],
    );
  }

  /** Models whose protocol the user changed in TKCode since Astra wrote them (IClientProtocolChoices). */
  changedProtocolChoices(): Map<string, ApiProtocol> {
    const changed = new Map<string, ApiProtocol>();
    const file = this.configFile;
    const entry = this.store.get(this.kind, file, TKCodeClientAdapter.providerPath);
    if (!entry || entry.appliedValueJson === null) return changed;
    const applied = parseObject(entry.appliedValueJson);
    const current = parseObject(this.currentValue(file, 'json', TKCodeClientAdapter.providerPath));
    const models = asObj(getMember(current, 'models'));
    if (!applied || !current || !models) return changed;
    for (const [id, node] of models.entries) {
      const model = asObj(node);
      const written = asObj(getMember(asObj(getMember(applied, 'models')), id));
      if (!model || !written) continue;
      // A model without its own api follows the provider's.
      const now = textOf(getMember(model, API_FIELD)) ?? textOf(getMember(current, API_FIELD));
      const was = textOf(getMember(written, API_FIELD)) ?? textOf(getMember(applied, API_FIELD));
      const protocol = protocolOfApi(now);
      if (now !== was && protocol) changed.set(id, protocol);
    }
    return changed;
  }

  protected override valueStillOurs(
    file: string,
    keyPath: string,
    format: ConfigFileFormat,
    current: string | null,
    appliedJson: string | null,
  ): boolean {
    return file === this.configFile && keyPath === TKCodeClientAdapter.providerPath
      ? ownedPartsEqual(current, appliedJson)
      : super.valueStillOurs(file, keyPath, format, current, appliedJson);
  }

  /** The bound provider's models as TKCode model entries (only the facts Astra knows are written). */
  private modelsJson(ctx: EnableContext): JObj {
    const hints = ClientAdapterBase.modelHints(ctx);
    const protocols = this.modelProtocols(ctx, 'openai-chat');
    const models = jobj();
    for (const { id, name } of ClientAdapterBase.modelEntries(ctx)) {
      const info = hints.get(id);
      const model = jobj();
      const protocol = protocols.get(id)!;
      if (protocol !== 'openai-chat') setMember(model, API_FIELD, protocol === 'anthropic' ? 'messages' : 'responses');
      if (name !== null && name !== id) setMember(model, 'name', name);
      const context = intHint(info?.contextWindow);
      if (context !== null) setMember(model, 'contextWindow', jnum(context));
      const maxOutput = intHint(info?.maxOutputTokens);
      if (maxOutput !== null) setMember(model, 'maxTokens', jnum(maxOutput));
      const reasoning = ClientAdapterBase.boolHint(info?.reasoning);
      if (reasoning !== null) setMember(model, 'reasoning', reasoning);
      const vision = ClientAdapterBase.boolHint(info?.vision);
      if (vision !== null) setMember(model, 'images', vision);
      setMember(models, id, model);
    }
    return models;
  }
}

/** A positive hint that fits TKCode's int (a larger number would make it reject the whole file), else null. */
function intHint(node: unknown): number | null {
  const value = ClientAdapterBase.positiveLong(node);
  return value !== null && value <= INT_MAX ? value : null;
}

function protocolOfApi(api: string | null): ApiProtocol | null {
  switch (api) {
    case CHAT_API:
      return 'openai-chat';
    case 'responses':
      return 'openai-responses';
    case 'messages':
      return 'anthropic';
    default:
      return null;
  }
}

const same = (a: JV, b: JV): boolean => jsonDeepEquals(a, b);

/**
 * Whether the entry still holds what Astra wrote in the parts Astra owns: the provider fields it wrote, the same set
 * of models, and per model the fields it wrote. TKCode's additions and a model's `api` are not compared.
 */
function ownedPartsEqual(currentText: string | null, appliedJson: string | null): boolean {
  if (currentText === null || appliedJson === null) return currentText === null && appliedJson === null;
  const current = parseObject(currentText);
  const applied = parseObject(appliedJson);
  if (!current || !applied) return false;
  for (const field of OWNED_PROVIDER_FIELDS)
    if (!same(getMember(applied, field), getMember(current, field))) return false;
  const appliedModels = asObj(getMember(applied, 'models')) ?? jobj();
  const currentModels = asObj(getMember(current, 'models')) ?? jobj();
  if (appliedModels.entries.length !== currentModels.entries.length) return false;
  for (const [id, node] of appliedModels.entries) {
    const written = asObj(node);
    const now = asObj(getMember(currentModels, id));
    if (!written || !now) return false;
    for (const field of OWNED_MODEL_FIELDS)
      if (written.entries.some(([k]) => k === field) && !same(getMember(written, field), getMember(now, field)))
        return false;
  }
  return true;
}

/** Copies what TKCode added to the current entry (provider and per-model fields Astra does not own) into the new one. */
function carryOver(current: JObj, next: JObj): void {
  for (const [key, value] of current.entries)
    if (key !== 'models' && !OWNED_PROVIDER_FIELDS.includes(key)) setMember(next, key, cloneJv(value));
  const currentModels = asObj(getMember(current, 'models'));
  const nextModels = asObj(getMember(next, 'models'));
  if (!currentModels || !nextModels) return;
  for (const [id, node] of nextModels.entries) {
    const model = asObj(node);
    const old = asObj(getMember(currentModels, id));
    if (!model || !old) continue;
    for (const [key, value] of old.entries)
      if (key !== API_FIELD && !OWNED_MODEL_FIELDS.includes(key)) setMember(model, key, cloneJv(value));
  }
}
