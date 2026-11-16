import path from 'node:path';
import { ClientAdapterBase } from '../adapter-base.js';
import {
  ConfigChange,
  ClientDetections,
  type ClientDetection,
  type ConfigChangePlan,
  type EnableContext,
} from '../contracts.js';
import { ClientKinds } from '../domain/kinds.js';
import { FileClientAdapter, PROVIDER_ID } from './file-adapter.js';

const ACCOUNT_TABLE = 'provider_accounts.astra';
const ALIAS_PREFIX = 'astra-';
const DEFAULT_CONTEXT = 128_000;

const isModelTable = (keyPath: string): boolean =>
  keyPath.startsWith('models.' + ALIAS_PREFIX) && keyPath.indexOf('.', 'models.'.length) < 0;

/**
 * AtomCode (npm `@atomgit.com/atomcode`; Coexist): `~/.atomcode/config.toml` (`ATOMCODE_HOME` when absolute). Adds the account
 * `[provider_accounts.astra]`, one `[models.astra-<id>]` table per bound model and, with a model, `default_provider` /
 * `default_model`. (Port of AtomCodeClientAdapter.)
 */
export class AtomCodeClientAdapter extends FileClientAdapter {
  readonly kind = ClientKinds.AtomCode;

  private get dir(): string {
    const home = this.env.getEnvironmentVariable('ATOMCODE_HOME');
    return home && path.isAbsolute(home) ? home : this.env.combine('.atomcode');
  }

  private get configFile(): string {
    return path.join(this.dir, 'config.toml');
  }

  protected get markerFile(): string {
    return this.configFile;
  }

  protected get markerKey(): string {
    return ACCOUNT_TABLE;
  }

  protected override isTableKeyPath(keyPath: string): boolean {
    return keyPath === ACCOUNT_TABLE || isModelTable(keyPath);
  }

  protected override get enabledWarnings(): string[] {
    return ['AtomCode reads its settings at start-up: restart open atomcode sessions to use this.'];
  }

  detect(): ClientDetection {
    const dir = this.dir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('atomcode');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no atomcode executable on PATH`);
  }

  configPaths(): string[] {
    return [this.configFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.configFile;
    const raw = FileClientAdapter.raw;
    const changes: ConfigChange[] = [];
    changes.push(
      ...this.tableChanges(file, ACCOUNT_TABLE, [
        ['provider', raw('openai-compatible')],
        ['base_url', raw(FileClientAdapter.gateway(ctx.gatewayBaseUrl))],
        ['api_key', raw(ctx.localKey)],
      ]),
    );

    const hints = ClientAdapterBase.modelHints(ctx);
    const aliases = FileClientAdapter.aliases(ALIAS_PREFIX, ClientAdapterBase.modelEntries(ctx).map((e) => e.id));
    for (const [id, alias] of aliases) {
      const info = hints.get(id);
      const context = ClientAdapterBase.positiveLong(info?.contextWindow) ?? DEFAULT_CONTEXT;
      let output = ClientAdapterBase.positiveLong(info?.maxOutputTokens);
      // An output above the window AtomCode is told is cut to it.
      if (output !== null && output > context) output = context;
      changes.push(
        ...this.tableChanges(file, `models.${alias}`, [
          ['account', raw(PROVIDER_ID)],
          ['model', raw(id)],
          ['context_window', String(context)],
          ['max_tokens', output !== null ? String(output) : null],
          ['supports_vision', ClientAdapterBase.boolHint(info?.vision) === true ? 'true' : 'false'],
        ]),
      );
    }

    // Models Astra registered earlier that the bound provider no longer offers are taken out again.
    const aliasValues = new Set(aliases.values());
    const tracked = this.store
      .list(this.kind)
      .filter((e) => e.filePath === file && isModelTable(e.keyPath))
      .map((e) => e.keyPath)
      .sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
    for (const keyPath of tracked) {
      if (aliasValues.has(keyPath.slice('models.'.length))) continue;
      const current = this.currentValue(file, 'toml', keyPath, 'table');
      if (current !== null) changes.push(new ConfigChange(file, 'toml', keyPath, current, null, { kind: 'table' }));
    }

    const selected = ctx.model ? aliases.get(ctx.model) : undefined;
    if (selected !== undefined) {
      changes.push(this.tomlSet(file, 'default_provider', raw(selected)));
      changes.push(this.tomlSet(file, 'default_model', raw(selected)));
    }
    return this.buildPlan(changes);
  }
}
