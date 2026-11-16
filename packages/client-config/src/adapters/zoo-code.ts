import fs from 'node:fs';
import path from 'node:path';
import { ClientAdapterBase } from '../adapter-base.js';
import {
  ClientDetections,
  type ClientDetection,
  type ClientStatus,
  type ConfigChange,
  type ConfigChangePlan,
  type EnableContext,
} from '../contracts.js';
import { ClientKinds } from '../domain/kinds.js';
import { JObj, parseJsonc, toJsonText, type JV } from '../editing/jsonc-editor.js';
import { ConfigFileClientAdapter } from './config-file-adapter.js';

const PROFILE_NAME = 'astra';
const EXTENSION_ID = 'zoocodeorganization.zoo-code';
/** VS Code's setting that names the file Zoo Code imports at start-up (a key with a dot in it, escaped for key paths). */
const IMPORT_SETTING = 'zoo-code\\.autoImportSettingsPath';
const DEFAULT_CONTEXT_WINDOW = 128_000;

const member = (o: JObj, key: string): JV | undefined => o.entries.find(([k]) => k === key)?.[1];

/**
 * Zoo Code (VS Code extension `ZooCodeOrganization.zoo-code`; Coexist): `astra-import.json` in Zoo Code's folder of VS
 * Code's `globalStorage` holds one OpenAI Compatible profile `astra`, and `zoo-code.autoImportSettingsPath` in VS Code's
 * `settings.json` (and each profile's) points at it. (Port of ZooCodeClientAdapter.)
 */
export class ZooCodeClientAdapter extends ConfigFileClientAdapter {
  readonly kind = ClientKinds.ZooCode;
  readonly mode = 'coexist' as const;
  readonly availability = 'available' as const;

  private get userDir(): string {
    return this.appSupportDir('Code', 'User');
  }

  private get extensionsDir(): string {
    const dir = this.env.getEnvironmentVariable('VSCODE_EXTENSIONS');
    return dir ? dir : this.env.combine('.vscode', 'extensions');
  }

  private get settingsFile(): string {
    return path.join(this.userDir, 'settings.json');
  }

  private get importFile(): string {
    return path.join(this.userDir, 'globalStorage', EXTENSION_ID, 'settings', 'astra-import.json');
  }

  detect(): ClientDetection {
    try {
      const dirs = fs.readdirSync(this.extensionsDir, { withFileTypes: true });
      if (dirs.some((d) => d.isDirectory() && d.name.toLowerCase().startsWith(EXTENSION_ID + '-')))
        return ClientDetections.found(null, `extension in ${this.extensionsDir}`);
    } catch {
      // no extensions folder
    }
    return this.applied(this.importFile, 'providerProfiles')
      ? ClientDetections.found(null, `import file ${this.importFile}`)
      : ClientDetections.notFound(`no Zoo Code extension in ${this.extensionsDir}`);
  }

  configPaths(): string[] {
    return [this.settingsFile, this.importFile];
  }

  /** The `settings.json` of VS Code's default profile and of every other profile that has settings of its own. */
  private settingsFiles(): string[] {
    const files = [this.settingsFile];
    const text = this.env.readTextOrNull(path.join(this.userDir, 'globalStorage', 'storage.json'));
    if (text === null) return files;
    let root: JV;
    try {
      root = parseJsonc(text).value;
    } catch {
      return files;
    }
    const profiles = root instanceof JObj ? member(root, 'userDataProfiles') : undefined;
    if (!Array.isArray(profiles)) return files;
    for (const profile of profiles) {
      if (!(profile instanceof JObj)) continue;
      const folder = this.profileFolder(member(profile, 'location'));
      if (folder === null || !this.env.directoryExists(folder)) continue;
      const flags = member(profile, 'useDefaultFlags');
      if (flags instanceof JObj && member(flags, 'settings') === true) continue;
      files.push(path.join(folder, 'settings.json'));
    }
    return files;
  }

  /** A profile's folder: `User/profiles/<location>`, or the `fsPath` / `path` of a URI location. */
  private profileFolder(location: JV | undefined): string | null {
    if (typeof location === 'string' && location.length > 0) return path.join(this.userDir, 'profiles', location);
    if (!(location instanceof JObj)) return null;
    const fsPath = member(location, 'fsPath');
    if (typeof fsPath === 'string' && fsPath.length > 0) return fsPath;
    let p = member(location, 'path');
    if (typeof p !== 'string' || p.length === 0) return null;
    if (p.length > 2 && p[0] === '/' && p[2] === ':') p = p.slice(1);
    return p.split('/').join(path.sep);
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const entries = ClientAdapterBase.modelEntries(ctx);
    const model = ctx.model ? ctx.model : (entries[0]?.id ?? null);
    const profile: Record<string, unknown> = {
      id: PROFILE_NAME,
      apiProvider: 'openai',
      openAiBaseUrl: ConfigFileClientAdapter.v1(ctx),
      openAiApiKey: ctx.localKey,
    };
    if (model !== null) profile.openAiModelId = model;
    profile.openAiStreamingEnabled = true;
    if (model !== null) {
      const info = ClientAdapterBase.modelHints(ctx).get(model);
      const modelInfo: Record<string, unknown> = {
        supportsPromptCache: false,
        supportsImages: ClientAdapterBase.boolHint(info?.vision) ?? false,
        contextWindow: ClientAdapterBase.positiveLong(info?.contextWindow) ?? DEFAULT_CONTEXT_WINDOW,
      };
      const maxTokens = ClientAdapterBase.positiveLong(info?.maxOutputTokens);
      if (maxTokens !== null) modelInfo.maxTokens = maxTokens;
      profile.openAiCustomModelInfo = modelInfo;
    }
    const profiles = {
      currentApiConfigName: PROFILE_NAME,
      apiConfigs: { [PROFILE_NAME]: profile },
    };
    const changes: ConfigChange[] = [this.setKey(this.importFile, 'providerProfiles', toJsonText(profiles))];
    for (const settings of new Set(this.settingsFiles()))
      changes.push(this.setKey(settings, IMPORT_SETTING, ClientAdapterBase.jsonString(this.importFile)));
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    return this.buildPlan(this.trackedRemovals());
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const enabled = this.applied(this.importFile, 'providerProfiles');
    return this.buildStatus(
      this.detect(),
      enabled,
      enabled
        ? [
            "Zoo Code reads Astra's profile when it starts: run Developer: Reload Window in VS Code to use this. Turning Astra off leaves the imported profile in Zoo Code.",
          ]
        : [],
    );
  }
}
