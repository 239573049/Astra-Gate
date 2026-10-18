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
import { equalsValue } from '../config-value-codec.js';
import { EditorException } from '../editing/errors.js';
import { JsoncEditor, toJsonText } from '../editing/jsonc-editor.js';
import { ClientKinds } from '../domain/kinds.js';
import { ApiProtocols, type ApiProtocol } from '../domain/protocols.js';

const PROVIDER_ID = 'astra';
const SELECTED_MODEL_PATH = 'model';

/** The providers Astra registers, one per protocol: name, `type` and `wireApi`. */
const PROVIDERS: Array<{
  protocol: ApiProtocol;
  name: string;
  type: string;
  wireApi: string | null;
}> = [
  { protocol: 'openai-chat', name: PROVIDER_ID, type: 'openai', wireApi: null },
  {
    protocol: 'openai-responses',
    name: PROVIDER_ID + '-responses',
    type: 'openai',
    wireApi: 'responses',
  },
  { protocol: 'anthropic', name: PROVIDER_ID + '-anthropic', type: 'anthropic', wireApi: null },
];

const providerPathOf = (name: string) => 'providers.' + JsoncEditor.selector(['name', name]);
const modelPathPrefix = (provider: string) => 'models.[provider=' + provider + '&id=';
const modelPath = (provider: string, id: string) =>
  'models.' + JsoncEditor.selector(['provider', provider], ['id', id]);
const PROVIDER_PATH = providerPathOf(PROVIDER_ID);

function canSelect(id: string): boolean {
  try {
    JsoncEditor.selector(['id', id]);
    return true;
  } catch (e) {
    if (e instanceof EditorException) return false;
    throw e;
  }
}

/**
 * GitHub Copilot CLI (Coexist): registers a bring-your-own-key provider in `<COPILOT_HOME>/providers.json`
 * (or COPILOT_PROVIDERS_CONFIG) plus one model element per bound model, grouped into one provider per protocol
 * (astra, astra-responses, astra-anthropic), and selects the chosen model in `settings.json`.
 * (Port of CopilotCliClientAdapter.)
 */
export class CopilotCliClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.CopilotCli;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get homeDir(): string {
    const dir = this.env.getEnvironmentVariable('COPILOT_HOME');
    return dir ? dir : this.env.combine('.copilot');
  }

  private get providersFile(): string {
    const file = this.env.getEnvironmentVariable('COPILOT_PROVIDERS_CONFIG');
    return file ? file : path.join(this.homeDir, 'providers.json');
  }

  private get settingsFile(): string {
    return path.join(this.homeDir, 'settings.json');
  }

  detect(): ClientDetection {
    const dir = this.homeDir;
    if (this.env.directoryExists(dir))
      return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('copilot');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no copilot executable on PATH`);
  }

  configPaths(): string[] {
    return [this.providersFile, this.settingsFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.providersFile;
    const root = ctx.gatewayBaseUrl.replace(/\/+$/, '');
    const hints = ClientAdapterBase.modelHints(ctx);
    const protocols = this.modelProtocols(ctx, ApiProtocols.openAIChat);
    // selector metacharacters cannot be addressed safely
    const models = ClientAdapterBase.modelEntries(ctx).filter((e) => canSelect(e.id));
    const changes: ConfigChange[] = [];
    const wanted = new Set<string>();
    let selected: string | null = null;
    for (const { protocol, name, type, wireApi } of PROVIDERS) {
      const own = models.filter((m) => protocols.get(m.id) === protocol);
      const providerPath = providerPathOf(name);
      const before = this.currentValue(file, 'json', providerPath);
      if (protocol !== 'openai-chat' && own.length === 0) {
        if (before !== null && this.store.get(this.kind, file, providerPath) !== null)
          changes.push(new ConfigChange(file, 'json', providerPath, before, null));
        continue;
      }
      const provider: Record<string, unknown> = {
        name,
        type,
        baseUrl: protocol === 'anthropic' ? root : root + '/v1',
        apiKey: ctx.localKey,
      };
      if (wireApi !== null) provider.wireApi = wireApi;
      changes.push(new ConfigChange(file, 'json', providerPath, before, toJsonText(provider)));
      for (const { id } of own) {
        const p = modelPath(name, id);
        wanted.add(p);
        if (id === ctx.model) selected = `${name}/${id}`;
        changes.push(
          new ConfigChange(
            file,
            'json',
            p,
            this.currentValue(file, 'json', p),
            toJsonText(modelJson(id, name, hints.get(id))),
          ),
        );
      }
    }
    // Models Astra registered earlier that the bound provider no longer offers (or that moved to another protocol's
    // provider) are taken out again.
    for (const p of this.trackedModelPaths(file)) {
      if (wanted.has(p)) continue;
      const current = this.currentValue(file, 'json', p);
      if (current !== null) changes.push(new ConfigChange(file, 'json', p, current, null));
    }
    if (selected !== null)
      changes.push(
        new ConfigChange(
          this.settingsFile,
          'json',
          SELECTED_MODEL_PATH,
          this.currentValue(this.settingsFile, 'json', SELECTED_MODEL_PATH),
          ClientAdapterBase.jsonString(selected),
        ),
      );
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const file = this.providersFile;
    const changes: ConfigChange[] = [];
    const providerBefore = this.currentValue(file, 'json', PROVIDER_PATH);
    if (providerBefore !== null)
      changes.push(new ConfigChange(file, 'json', PROVIDER_PATH, providerBefore, null));
    for (const { name } of PROVIDERS.slice(1)) {
      const p = providerPathOf(name);
      if (this.store.get(this.kind, file, p) === null) continue;
      const before = this.currentValue(file, 'json', p);
      if (before !== null) changes.push(new ConfigChange(file, 'json', p, before, null));
    }
    for (const p of this.trackedModelPaths(file)) {
      const current = this.currentValue(file, 'json', p);
      if (current !== null) changes.push(new ConfigChange(file, 'json', p, current, null));
    }
    // A model the user picked afterwards (GitHub-hosted or another BYOK provider) is theirs and stays.
    const selected = this.currentValue(this.settingsFile, 'json', SELECTED_MODEL_PATH);
    const selectedText = ClientAdapterBase.stringOf(selected);
    if (selectedText !== null && PROVIDERS.some((p) => selectedText.startsWith(p.name + '/')))
      changes.push(
        new ConfigChange(this.settingsFile, 'json', SELECTED_MODEL_PATH, selected, null),
      );
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const entry = this.store.get(this.kind, this.providersFile, PROVIDER_PATH);
    const enabled =
      entry !== null &&
      equalsValue(
        'json',
        this.currentValue(this.providersFile, 'json', PROVIDER_PATH),
        entry.appliedValueJson,
      );
    return this.buildStatus(detection, enabled, []);
  }

  private trackedModelPaths(file: string): string[] {
    return (
      this.store
        .list(this.kind)
        .filter(
          (e) =>
            e.filePath === file &&
            e.keyPath.endsWith(']') &&
            PROVIDERS.some((p) => e.keyPath.startsWith(modelPathPrefix(p.name))),
        )
        .map((e) => e.keyPath)
        // the C# state store lists ordered by file, then key (ordinal)
        .sort((a, b) => (a < b ? -1 : a > b ? 1 : 0))
    );
  }
}

/** A model entry: id, provider, and the limits / vision support Astra knows (Copilot uses its defaults otherwise). */
function modelJson(
  id: string,
  provider: string,
  info: Record<string, unknown> | undefined,
): Record<string, unknown> {
  const model: Record<string, unknown> = { id, provider };
  const context = ClientAdapterBase.positiveLong(info?.contextWindow);
  if (context !== null) model.maxContextWindowTokens = context;
  const maxOutput = ClientAdapterBase.positiveLong(info?.maxOutputTokens);
  if (maxOutput !== null) model.maxOutputTokens = maxOutput;
  const vision = ClientAdapterBase.boolHint(info?.vision);
  if (vision !== null) model.capabilities = { supports: { vision } };
  return model;
}
