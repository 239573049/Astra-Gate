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

const BLOCK_ID = 'muse-code';
const BLOCK_NOTE =
  '# Muse Code is routed through Astra. Turn the Muse Code client off in Astra to remove this block.';
/** Subcommands that never talk to the model through the gateway (or do not accept `--base-url`). */
const PASS_THROUGH = 'resume|export|trace|skills|sandbox|session-message|auth|login|logout|init|config|help';

/** POSIX single-quoting: wraps the value in single quotes and escapes embedded quotes. */
const shellQuote = (value: string): string => "'" + value.split("'").join("'\\''") + "'";

/**
 * Muse Code (Meta's terminal agent, binary `muse`; Switch). Muse has no config key for the endpoint or key, so Astra
 * takes over one managed block of the shell rc file (`~/.zshrc`, or `~/.bashrc` when only that exists) that defines
 * a `muse` shell function passing `--base-url`, `META_API_KEY` and `MUSE_MODEL` per invocation. (Port of
 * MuseCodeClientAdapter.)
 */
export class MuseCodeClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.MuseCode;
  readonly mode = 'switch' as const;
  readonly availability = 'available' as const;

  /** The rc file: `~/.zshrc` (macOS default shell), or `~/.bashrc` when only that one exists. */
  private get rcFile(): string {
    const zshrc = this.env.combine('.zshrc');
    const bashrc = this.env.combine('.bashrc');
    return !this.env.fileExists(zshrc) && this.env.fileExists(bashrc) ? bashrc : zshrc;
  }

  detect(): ClientDetection {
    const dir = this.env.combine('.config', 'muse');
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('muse');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no muse executable on PATH`);
  }

  configPaths(): string[] {
    return [this.rcFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.rcFile;
    const before = this.currentValue(file, 'shellblock', BLOCK_ID);
    return this.buildPlan([
      new ConfigChange(file, 'shellblock', BLOCK_ID, before, ClientAdapterBase.jsonString(MuseCodeClientAdapter.blockBody(ctx))),
    ]);
  }

  planDisable(): ConfigChangePlan {
    const file = this.rcFile;
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
    const enabled = this.stillOurs(this.rcFile, 'shellblock', BLOCK_ID);
    return this.buildStatus(detection, enabled, []);
  }

  /**
   * The block body: a `muse` function. The key, model and gateway URL are passed per invocation (env prefix and
   * `--base-url`), never exported, so the user's login is untouched by every other command.
   */
  private static blockBody(ctx: EnableContext): string {
    // Muse's default-model path fails ("no visible models") against a gateway catalog, so always pin a model: the
    // selected one, else the first model the bound providers list.
    const model = ctx.model ? ctx.model : (ClientAdapterBase.modelEntries(ctx)[0]?.id ?? null);
    const envPrefix = `META_API_KEY=${shellQuote(ctx.localKey)}` + (model ? ` MUSE_MODEL=${shellQuote(model)}` : '');
    const baseUrl = shellQuote(ctx.gatewayBaseUrl.replace(/\/+$/, '') + '/v1');
    return [
      BLOCK_NOTE,
      'muse() {',
      '  case "${1-}" in',
      '    exec)',
      '      shift',
      `      ${envPrefix} command muse exec --base-url ${baseUrl} "$@"`,
      '      ;;',
      `    ${PASS_THROUGH})`,
      '      command muse "$@"',
      '      ;;',
      '    *)',
      `      ${envPrefix} command muse --base-url ${baseUrl} "$@"`,
      '      ;;',
      '  esac',
      '}',
    ].join('\n');
  }
}
