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
  parseObject,
  setMember,
  textOf,
  trimSlashes,
} from '../domain/group5-json.js';
import { ClientKinds } from '../domain/kinds.js';
import { parseApiProtocol, type ApiProtocol } from '../domain/protocols.js';
import { compactJson, jsonDeepEquals, type JObj } from '../editing/jsonc-editor.js';
import type { ConfigFileFormat } from '../types.js';

const OWNED_PROVIDER_FIELDS = ['name', 'protocol', 'baseUrl', 'apiKey'];
const OWNED_MODEL_FIELDS = ['upstreamModel', 'displayName', 'contextWindow', 'maxOutputTokens'];
const PROTOCOL_OVERRIDE = 'protocolOverride';
/** NextCoWork's context window for a model that does not declare one. */
const DEFAULT_CONTEXT_WINDOW = 200_000;

/**
 * NextCoWork (nextco.work; Coexist): adds `providers.astra` to `~/.next-cowork/providers.json`. Astra owns only the
 * fields it writes (ownedPartsEqual); NextCoWork's additions are carried over and not drift.
 * (Port of NextCoWorkClientAdapter.)
 */
export class NextCoWorkClientAdapter extends ClientAdapterBase {
  static readonly providerId = 'astra';
  private static readonly providerPath = `providers.${NextCoWorkClientAdapter.providerId}`;

  readonly kind = ClientKinds.NextCoWork;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get configDir(): string {
    return this.env.combine('.next-cowork');
  }

  private get configFile(): string {
    return this.env.combine('.next-cowork', 'providers.json');
  }

  detect(): ClientDetection {
    return this.env.directoryExists(this.configDir)
      ? ClientDetections.found(null, `config directory ${this.configDir}`)
      : ClientDetections.notFound(`no ${this.configDir}`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const provider = jobj([
      ['name', 'Astra'],
      ['protocol', 'openai-chat'],
      ['baseUrl', trimSlashes(ctx.gatewayBaseUrl) + '/v1'],
      ['apiKey', ctx.localKey],
    ]);
    const models = this.modelsJson(ctx);
    if (models.entries.length > 0) provider.entries.push(['models', models]);
    const before = this.currentValue(file, 'json', NextCoWorkClientAdapter.providerPath);
    // What NextCoWork added to the entry Astra wrote stays; a provider Astra never wrote is replaced as before.
    if (this.store.get(this.kind, file, NextCoWorkClientAdapter.providerPath) !== null) {
      const current = parseObject(before);
      if (current) carryOver(current, provider);
    }
    return this.buildPlan([
      new ConfigChange(file, 'json', NextCoWorkClientAdapter.providerPath, before, compactJson(provider), {
        stillOurs: ownedPartsEqual,
      }),
    ]);
  }

  planDisable(): ConfigChangePlan {
    const file = this.configFile;
    const before = this.currentValue(file, 'json', NextCoWorkClientAdapter.providerPath);
    return this.buildPlan(
      before === null
        ? []
        : [new ConfigChange(file, 'json', NextCoWorkClientAdapter.providerPath, before, null, { stillOurs: ownedPartsEqual })],
    );
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const enabled = this.stillOurs(this.configFile, 'json', NextCoWorkClientAdapter.providerPath);
    return this.buildStatus(detection, enabled, []);
  }

  /** Models whose protocolOverride the user changed in NextCoWork since Astra wrote them (IClientProtocolChoices). */
  changedProtocolChoices(): Map<string, ApiProtocol> {
    const changed = new Map<string, ApiProtocol>();
    const file = this.configFile;
    const entry = this.store.get(this.kind, file, NextCoWorkClientAdapter.providerPath);
    if (!entry || entry.appliedValueJson === null) return changed;
    const applied = parseObject(entry.appliedValueJson);
    const current = parseObject(this.currentValue(file, 'json', NextCoWorkClientAdapter.providerPath));
    const models = asObj(getMember(current, 'models'));
    if (!applied || !current || !models) return changed;
    for (const [id, node] of models.entries) {
      const model = asObj(node);
      const written = asObj(getMember(asObj(getMember(applied, 'models')), id));
      if (!model || !written) continue;
      const now = textOf(getMember(model, PROTOCOL_OVERRIDE)) ?? textOf(getMember(current, 'protocol'));
      const was = textOf(getMember(written, PROTOCOL_OVERRIDE)) ?? textOf(getMember(applied, 'protocol'));
      const protocol = parseApiProtocol(now);
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
    return file === this.configFile && keyPath === NextCoWorkClientAdapter.providerPath
      ? ownedPartsEqual(current, appliedJson)
      : super.valueStillOurs(file, keyPath, format, current, appliedJson);
  }

  /** The bound provider's models as NextCoWork aliases. */
  private modelsJson(ctx: EnableContext): JObj {
    const hints = ClientAdapterBase.modelHints(ctx);
    const protocols = this.modelProtocols(ctx, 'openai-chat');
    const models = jobj();
    for (const { id, name } of ClientAdapterBase.modelEntries(ctx)) {
      const info = hints.get(id);
      const model = jobj([['upstreamModel', id]]);
      if (name !== null) setMember(model, 'displayName', name);
      const protocol = protocols.get(id)!;
      if (protocol !== 'openai-chat') setMember(model, PROTOCOL_OVERRIDE, protocol);
      const context = ClientAdapterBase.positiveLong(info?.contextWindow);
      if (context !== null) setMember(model, 'contextWindow', jnum(context));
      // NextCoWork refuses every turn of a model whose maxOutputTokens exceeds its window (200k when unset).
      const maxOutput = ClientAdapterBase.positiveLong(info?.maxOutputTokens);
      if (maxOutput !== null)
        setMember(model, 'maxOutputTokens', jnum(Math.min(maxOutput, context ?? DEFAULT_CONTEXT_WINDOW)));
      // Only the hints Astra actually knows are written; thinking settings are left to NextCoWork's catalog.
      const capabilities = jobj();
      const vision = ClientAdapterBase.boolHint(info?.vision);
      if (vision !== null) {
        setMember(capabilities, 'vision', vision);
        setMember(capabilities, 'visionInput', vision);
      }
      const pdf = ClientAdapterBase.boolHint(info?.pdf);
      if (pdf !== null) setMember(capabilities, 'fileInput', pdf);
      if (capabilities.entries.length > 0) setMember(model, 'capabilities', capabilities);
      setMember(models, id, model);
    }
    return models;
  }
}

/**
 * Whether the entry still holds what Astra wrote in the parts Astra owns: the provider fields it wrote, the same set
 * of models, and per model the fields and capabilities it wrote.
 */
function ownedPartsEqual(currentText: string | null, appliedJson: string | null): boolean {
  if (currentText === null || appliedJson === null) return currentText === null && appliedJson === null;
  const current = parseObject(currentText);
  const applied = parseObject(appliedJson);
  if (!current || !applied) return false;
  for (const field of OWNED_PROVIDER_FIELDS)
    if (hasMember(applied, field) && !jsonDeepEquals(getMember(applied, field), getMember(current, field))) return false;
  const appliedModels = asObj(getMember(applied, 'models')) ?? jobj();
  const currentModels = asObj(getMember(current, 'models')) ?? jobj();
  if (appliedModels.entries.length !== currentModels.entries.length) return false;
  for (const [id, node] of appliedModels.entries) {
    const written = asObj(node);
    const now = asObj(getMember(currentModels, id));
    if (!written || !now) return false;
    for (const field of OWNED_MODEL_FIELDS)
      if (hasMember(written, field) && !jsonDeepEquals(getMember(written, field), getMember(now, field))) return false;
    const capabilities = asObj(getMember(written, 'capabilities'));
    if (capabilities) {
      const nowCaps = asObj(getMember(now, 'capabilities'));
      for (const [key, value] of capabilities.entries)
        if (!jsonDeepEquals(value, getMember(nowCaps, key))) return false;
    }
  }
  return true;
}

/** Copies what NextCoWork added to the current entry (provider and per-model fields Astra does not own) into the new one. */
function carryOver(current: JObj, next: JObj): void {
  for (const [key, value] of current.entries)
    if (key !== 'models' && !OWNED_PROVIDER_FIELDS.includes(key) && !hasMember(next, key))
      setMember(next, key, cloneJv(value));
  const currentModels = asObj(getMember(current, 'models'));
  const nextModels = asObj(getMember(next, 'models'));
  if (!currentModels || !nextModels) return;
  for (const [id, node] of nextModels.entries) {
    const model = asObj(node);
    const old = asObj(getMember(currentModels, id));
    if (!model || !old) continue;
    for (const [key, value] of old.entries) {
      if (OWNED_MODEL_FIELDS.includes(key) || key === PROTOCOL_OVERRIDE || (hasMember(model, key) && key !== 'capabilities'))
        continue;
      if (key === 'capabilities') {
        const oldCaps = asObj(value);
        if (!oldCaps) continue;
        let capabilities = asObj(getMember(model, 'capabilities'));
        if (!capabilities) {
          capabilities = jobj();
          setMember(model, 'capabilities', capabilities);
        }
        for (const [name, flag] of oldCaps.entries)
          if (!hasMember(capabilities, name)) setMember(capabilities, name, cloneJv(flag));
        continue;
      }
      setMember(model, key, cloneJv(value));
    }
  }
}
