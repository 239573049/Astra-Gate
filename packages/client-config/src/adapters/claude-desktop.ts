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
import { toJsonText } from '../editing/jsonc-editor.js';
import { ClientKinds } from '../domain/kinds.js';

/** Fixed profile id (a UUID) so Astra's profile never collides with other tools' profiles. */
export const CLAUDE_DESKTOP_PROFILE_ID = '7f3c1a55-0a17-4000-8000-1cabad000001';
export const CLAUDE_DESKTOP_PROFILE_NAME = 'Astra';

const PROFILE_FIELD_NAMES = [
  'coworkEgressAllowedHosts',
  'disableDeploymentModeChooser',
  'inferenceProvider',
  'inferenceGatewayAuthScheme',
  'inferenceGatewayBaseUrl',
  'inferenceGatewayApiKey',
];

type Entry = Record<string, unknown> | unknown;

/**
 * Claude Desktop (Switch): writes a "Claude Desktop on 3P" local configuration with Astra as the LLM gateway: a
 * profile under `Claude-3p/configLibrary/<profileId>.json`, applied through `configLibrary/_meta.json`, and
 * `deploymentMode = "3p"` in both `claude_desktop_config.json` files. The role map is applied by the gateway
 * itself, not written here. (Port of ClaudeDesktopClientAdapter.)
 */
export class ClaudeDesktopClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.ClaudeDesktop;
  readonly mode = 'switch' as const;
  readonly availability = 'available' as const;

  private get os(): string {
    return this.env.os;
  }

  private localAppData(): string {
    const d = this.env.getEnvironmentVariable('LOCALAPPDATA');
    return d ? d : this.env.combine('AppData', 'Local');
  }

  private configRoot(): string {
    const xdg = this.env.getEnvironmentVariable('XDG_CONFIG_HOME');
    return xdg ? xdg : this.env.combine('.config');
  }

  private get normalDir(): string {
    switch (this.os) {
      case 'osx':
        return this.env.combine('Library', 'Application Support', 'Claude');
      case 'windows':
        return path.join(this.localAppData(), 'Claude');
      default:
        return path.join(this.configRoot(), 'Claude');
    }
  }

  private get threepDir(): string {
    switch (this.os) {
      case 'osx':
        return this.env.combine('Library', 'Application Support', 'Claude-3p');
      case 'windows':
        return path.join(this.localAppData(), 'Claude-3p');
      default:
        return path.join(this.configRoot(), 'Claude-3p');
    }
  }

  private get normalConfigFile(): string {
    return path.join(this.normalDir, 'claude_desktop_config.json');
  }

  private get threepConfigFile(): string {
    return path.join(this.threepDir, 'claude_desktop_config.json');
  }

  private get profileFile(): string {
    return path.join(this.threepDir, 'configLibrary', `${CLAUDE_DESKTOP_PROFILE_ID}.json`);
  }

  private get metaFile(): string {
    return path.join(this.threepDir, 'configLibrary', '_meta.json');
  }

  detect(): ClientDetection {
    if (this.env.directoryExists(this.threepDir) || this.env.directoryExists(this.normalDir))
      return ClientDetections.found(null, `Claude Desktop directories under ${this.threepDir}`);
    return ClientDetections.notFound(`no ${this.threepDir} and no ${this.normalDir}`);
  }

  configPaths(): string[] {
    return [this.normalConfigFile, this.threepConfigFile, this.profileFile, this.metaFile];
  }

  protected override strictJsonFiles(): string[] {
    return this.configPaths();
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const changes: ConfigChange[] = [
      this.deploymentMode(this.normalConfigFile, '3p'),
      this.deploymentMode(this.threepConfigFile, '3p'),
    ];

    for (const [key, value] of this.profileFields(ctx)) {
      const before = this.currentValue(this.profileFile, 'json', key);
      changes.push(new ConfigChange(this.profileFile, 'json', key, before, value));
    }

    const entries = this.currentEntries(this.metaFile) ?? [];
    const existingIndex = entries.findIndex(isOurs);
    if (existingIndex >= 0) {
      const ours = entries[existingIndex];
      if (isObject(ours)) ours.name = CLAUDE_DESKTOP_PROFILE_NAME;
    } else entries.push({ id: CLAUDE_DESKTOP_PROFILE_ID, name: CLAUDE_DESKTOP_PROFILE_NAME });
    changes.push(
      new ConfigChange(
        this.metaFile,
        'json',
        'entries',
        this.currentValue(this.metaFile, 'json', 'entries'),
        toJsonText(entries),
      ),
      new ConfigChange(
        this.metaFile,
        'json',
        'appliedId',
        this.currentValue(this.metaFile, 'json', 'appliedId'),
        ClientAdapterBase.jsonString(CLAUDE_DESKTOP_PROFILE_ID),
      ),
    );
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const changes: ConfigChange[] = [];
    for (const file of [this.normalConfigFile, this.threepConfigFile]) {
      const before = this.currentValue(file, 'json', 'deploymentMode');
      if (before !== null)
        changes.push(new ConfigChange(file, 'json', 'deploymentMode', before, null));
    }
    for (const key of PROFILE_FIELD_NAMES) {
      const before = this.currentValue(this.profileFile, 'json', key);
      if (before !== null)
        changes.push(new ConfigChange(this.profileFile, 'json', key, before, null));
    }
    const entry = this.store.get(this.kind, this.metaFile, 'entries');
    if (entry && !entry.originalAbsent && entry.originalValueJson !== null)
      changes.push(
        new ConfigChange(this.metaFile, 'json', 'entries', null, entry.originalValueJson),
      );
    else {
      const entries = this.currentEntries(this.metaFile);
      if (entries !== null) {
        const kept = entries.filter((e) => !isOurs(e));
        if (kept.length !== entries.length)
          changes.push(
            new ConfigChange(
              this.metaFile,
              'json',
              'entries',
              this.currentValue(this.metaFile, 'json', 'entries'),
              toJsonText(kept),
            ),
          );
      }
    }
    const appliedBefore = this.currentValue(this.metaFile, 'json', 'appliedId');
    if (
      appliedBefore !== null &&
      appliedBefore === ClientAdapterBase.jsonString(CLAUDE_DESKTOP_PROFILE_ID)
    )
      changes.push(new ConfigChange(this.metaFile, 'json', 'appliedId', appliedBefore, null));
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const entry = this.store.get(this.kind, this.metaFile, 'appliedId');
    const enabled =
      entry !== null &&
      equalsValue(
        'json',
        this.currentValue(this.metaFile, 'json', 'appliedId'),
        entry.appliedValueJson,
      );
    return this.buildStatus(detection, enabled, ['Restart Claude Desktop to apply.']);
  }

  private profileFields(ctx: EnableContext): Array<[string, string]> {
    const baseUrl = ctx.gatewayBaseUrl.replace(/\/+$/, '');
    return [
      ['coworkEgressAllowedHosts', toJsonText(['*'])],
      ['disableDeploymentModeChooser', 'true'],
      ['inferenceProvider', ClientAdapterBase.jsonString('gateway')],
      ['inferenceGatewayAuthScheme', ClientAdapterBase.jsonString('bearer')],
      ['inferenceGatewayBaseUrl', ClientAdapterBase.jsonString(baseUrl)],
      ['inferenceGatewayApiKey', ClientAdapterBase.jsonString(ctx.localKey)],
    ];
  }

  private deploymentMode(file: string, mode: string): ConfigChange {
    return new ConfigChange(
      file,
      'json',
      'deploymentMode',
      this.currentValue(file, 'json', 'deploymentMode'),
      ClientAdapterBase.jsonString(mode),
    );
  }

  private currentEntries(file: string): Entry[] | null {
    const text = this.currentValue(file, 'json', 'entries');
    if (text === null) return null;
    try {
      const array: unknown = JSON.parse(text);
      if (!Array.isArray(array)) return null;
      return array.filter((item) => item !== null);
    } catch {
      return null;
    }
  }
}

const isObject = (v: unknown): v is Record<string, unknown> =>
  v !== null && typeof v === 'object' && !Array.isArray(v);

const isOurs = (e: unknown): boolean => isObject(e) && e.id === CLAUDE_DESKTOP_PROFILE_ID;
