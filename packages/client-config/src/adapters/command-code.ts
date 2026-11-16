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
import { JObj, compactJson, type JV, JNum } from '../editing/jsonc-editor.js';
import { ClientKinds } from '../domain/kinds.js';

const PROVIDER_ID = 'astra';
const PROVIDER_PATH = `provider.${PROVIDER_ID}`;
const num = (n: number): JNum => new JNum(String(n));

/**
 * Command Code (commandcode.ai; Coexist): `~/.commandcode/settings.json` (model, modelProvider) and the user's providers in
 * `~/.commandcode/providers.json`. Adds `provider.astra` (keyless: `apiKey: false`, which Command Code requires) and, when a
 * model is chosen, `model` = `astra/<id>` and `modelProvider` = `astra`. (Port of CommandCodeClientAdapter.)
 */
export class CommandCodeClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.CommandCode;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get dir(): string {
    return this.env.combine('.commandcode');
  }

  private get settingsFile(): string {
    return path.join(this.dir, 'settings.json');
  }

  private get providersFile(): string {
    return path.join(this.dir, 'providers.json');
  }

  detect(): ClientDetection {
    const dir = this.dir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('command-code') ?? this.env.findOnPath('cmd');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no command-code executable on PATH`);
  }

  configPaths(): string[] {
    return [this.providersFile, this.settingsFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const hints = ClientAdapterBase.modelHints(ctx);
    const models = new Map<string, JV>();
    for (const { id, name } of ClientAdapterBase.modelEntries(ctx)) {
      const info = hints.get(id);
      const model: Array<[string, JV]> = [['name', name ?? id]];
      if (ClientAdapterBase.boolHint(info?.reasoning) === true) model.push(['reasoning', true]);
      const context = ClientAdapterBase.positiveLong(info?.contextWindow);
      if (context !== null) model.push(['contextWindow', num(context)]);
      const output = ClientAdapterBase.positiveLong(info?.maxOutputTokens);
      if (output !== null) model.push(['maxOutput', num(output)]);
      models.set(id, new JObj(model));
    }
    const provider = new JObj([
      ['name', 'Astra'],
      ['api', 'openai-completions'],
      ['baseURL', ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1'],
      ['apiKey', false],
      ['models', new JObj([...models])],
    ]);
    const changes: ConfigChange[] = [
      new ConfigChange(
        this.providersFile,
        'json',
        PROVIDER_PATH,
        this.currentValue(this.providersFile, 'json', PROVIDER_PATH),
        compactJson(provider),
      ),
    ];
    if (ctx.model) {
      changes.push(this.key('model', `${PROVIDER_ID}/${ctx.model}`));
      changes.push(this.key('modelProvider', PROVIDER_ID));
    }
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const changes: ConfigChange[] = [];
    const provider = this.currentValue(this.providersFile, 'json', PROVIDER_PATH);
    if (provider !== null) changes.push(new ConfigChange(this.providersFile, 'json', PROVIDER_PATH, provider, null));
    // The selection is only ours while it still names the Astra provider.
    const modelProvider = this.currentValue(this.settingsFile, 'json', 'modelProvider');
    if (modelProvider !== null && ClientAdapterBase.stringOf(modelProvider) === PROVIDER_ID) {
      changes.push(new ConfigChange(this.settingsFile, 'json', 'modelProvider', modelProvider, null));
      const model = this.currentValue(this.settingsFile, 'json', 'model');
      if (model !== null && this.store.get(this.kind, this.settingsFile, 'model') !== null)
        changes.push(new ConfigChange(this.settingsFile, 'json', 'model', model, null));
    }
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const enabled = this.stillOurs(this.providersFile, 'json', PROVIDER_PATH);
    const warnings: string[] = [];
    if (enabled) {
      warnings.push(
        'Command Code sends no API key to a custom provider: turn off "require gateway key" in Astra\'s settings so local requests are accepted (they use the default token).',
      );
      warnings.push(
        'Command Code reads its settings at start-up and still wants its own sign-in (cmd login): restart open sessions to use this.',
      );
    }
    return this.buildStatus(detection, enabled, warnings);
  }

  private key(key: string, value: string): ConfigChange {
    return new ConfigChange(
      this.settingsFile,
      'json',
      key,
      this.currentValue(this.settingsFile, 'json', key),
      ClientAdapterBase.jsonString(value),
    );
  }
}
