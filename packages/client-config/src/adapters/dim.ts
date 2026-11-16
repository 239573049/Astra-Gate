import { ClientAdapterBase } from '../adapter-base.js';
import {
  ConfigChange,
  ClientDetections,
  type ClientDetection,
  type ClientStatus,
  type ConfigChangePlan,
  type EnableContext,
} from '../contracts.js';
import { ClientKinds } from '../domain/kinds.js';
import { ShellRc } from './shell-rc.js';

const BLOCK_ID = 'dim';
const BLOCK_NOTE = '# Dim is routed through Astra. Turn the Dim client off in Astra to remove this block.';
/** The limits Dim is given for a model whose window Astra does not know. */
const DEFAULT_CONTEXT = 128_000;
const DEFAULT_OUTPUT = 32_000;

/**
 * Dim alpha (npm dimcode-alpha, command `dima`; Switch): Dim reads `DIM_*` variables for one run only, so Astra takes over one
 * managed block of the shell rc file that defines a `dima` function setting them for that invocation. (Port of
 * DimClientAdapter.)
 */
export class DimClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.Dim;
  readonly mode = 'switch' as const;
  readonly availability = 'available' as const;

  private get dimDir(): string {
    const dir = this.env.getEnvironmentVariable('DIMCODE_HOME')?.trim();
    return dir ? dir : this.env.combine('.dimcode-alpha');
  }

  detect(): ClientDetection {
    const dir = this.dimDir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('dima');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no dima executable on PATH`);
  }

  configPaths(): string[] {
    return [ShellRc.file(this.env)];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = ShellRc.file(this.env);
    const before = this.currentValue(file, 'shellblock', BLOCK_ID);
    return this.buildPlan([
      new ConfigChange(file, 'shellblock', BLOCK_ID, before, ClientAdapterBase.jsonString(DimClientAdapter.blockBody(ctx))),
    ]);
  }

  planDisable(): ConfigChangePlan {
    const file = ShellRc.file(this.env);
    const changes: ConfigChange[] = [];
    const before = this.currentValue(file, 'shellblock', BLOCK_ID);
    if (before !== null) changes.push(new ConfigChange(file, 'shellblock', BLOCK_ID, before, null));
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const enabled = this.stillOurs(ShellRc.file(this.env), 'shellblock', BLOCK_ID);
    return this.buildStatus(
      detection,
      enabled,
      enabled ? ['Open a new terminal (or source your shell rc file) so the dima function is defined.'] : [],
    );
  }

  private static blockBody(ctx: EnableContext): string {
    // Dim cannot pick a model from the gateway's catalog, so always pin one: the selected one, else the first listed.
    const model = ctx.model ? ctx.model : (ClientAdapterBase.modelEntries(ctx)[0]?.id ?? null);
    const info = model === null ? undefined : ClientAdapterBase.modelHints(ctx).get(model);
    const context = ClientAdapterBase.positiveLong(info?.contextWindow) ?? DEFAULT_CONTEXT;
    let output = ClientAdapterBase.positiveLong(info?.maxOutputTokens) ?? Math.min(DEFAULT_OUTPUT, context);
    if (output > context) output = Math.min(DEFAULT_OUTPUT, context);
    const vars = [
      `DIM_BASE_URL=${ShellRc.quote(ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1')}`,
      'DIM_API_FORMAT=openai-compatible',
      `DIM_API_KEY=${ShellRc.quote(ctx.localKey)}`,
    ];
    if (model) vars.push(`DIM_MODEL=${ShellRc.quote(model)}`);
    vars.push(`DIM_MAX_CONTEXT_TOKENS=${context}`);
    vars.push(`DIM_MAX_OUTPUT_TOKENS=${output}`);
    return [BLOCK_NOTE, 'unalias dima 2>/dev/null || true', 'dima() {', `  ${vars.join(' ')} command dima "$@"`, '}'].join('\n');
  }
}
