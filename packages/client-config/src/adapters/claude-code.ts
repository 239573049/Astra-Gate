import path from 'node:path';
import { ClientAdapterBase } from '../adapter-base.js';
import { ConfigChange, ClientDetections, type ClientDetection, type ClientStatus, type ConfigChangePlan, type EnableContext } from '../contracts.js';
import { equalsValue } from '../config-value-codec.js';
import { CLAUDE_CODE_SLOTS, claudeCodeWith1M, parseClaudeCodeModels } from '../domain/claude-code-models.js';
import { ClientOptionKeys, clientOptionConfigKeyPaths, getBoolOption, getIntOption } from '../domain/client-options.js';
import { ClientKinds } from '../domain/kinds.js';

const API_KEY_PATH = 'env.ANTHROPIC_API_KEY';

/**
 * Claude Code (Switch): writes `env.ANTHROPIC_BASE_URL` and `env.ANTHROPIC_AUTH_TOKEN` in `~/.claude/settings.json`
 * (honoring CLAUDE_CONFIG_DIR), temporarily removes a present `env.ANTHROPIC_API_KEY` (recorded), and optionally
 * pins the model selection env vars. The "options" extras add the `[1m]` model suffix, `MAX_THINKING_TOKENS` and
 * `DISABLE_PROMPT_CACHING`; the cache TTL option is applied by the gateway, not written here. Warns on
 * `apiKeyHelper`. Everything else is untouched. (Port of ClaudeCodeClientAdapter.)
 */
export class ClaudeCodeClientAdapter extends ClientAdapterBase {
  readonly kind = ClientKinds.ClaudeCode;
  readonly mode = 'switch' as const;
  readonly availability = 'available' as const;

  private static readonly managedEnvKeys = [
    'ANTHROPIC_BASE_URL',
    'ANTHROPIC_AUTH_TOKEN',
    ...CLAUDE_CODE_SLOTS,
    ...clientOptionConfigKeyPaths(ClientKinds.ClaudeCode).map((p) => p.slice('env.'.length)),
  ];

  private get configDir(): string {
    const dir = this.env.getEnvironmentVariable('CLAUDE_CONFIG_DIR');
    return dir ? dir : this.env.combine('.claude');
  }

  private get settingsFile(): string {
    return path.join(this.configDir, 'settings.json');
  }

  detect(): ClientDetection {
    const dir = this.configDir;
    if (this.env.directoryExists(dir)) return ClientDetections.found(null, `config directory ${dir}`);
    const exe = this.env.findOnPath('claude');
    return exe
      ? ClientDetections.found(null, `executable ${exe}`)
      : ClientDetections.notFound(`no ${dir} and no claude executable on PATH`);
  }

  configPaths(): string[] {
    return [this.settingsFile];
  }

  planEnable(ctx: EnableContext): ConfigChangePlan {
    const file = this.settingsFile;
    const changes: ConfigChange[] = [
      this.envKey(file, 'ANTHROPIC_BASE_URL', ctx.gatewayBaseUrl),
      this.envKey(file, 'ANTHROPIC_AUTH_TOKEN', ctx.localKey),
    ];
    // Model slots are written only when a model was picked; an empty slot is never touched. The older single-key
    // "smallFastModel" extra still sets the Haiku tier, unless the slot map already does.
    const slots = parseClaudeCodeModels(ctx.extras);
    const context1M = getBoolOption(ctx.extras, ClientOptionKeys.context1M);
    for (const [slot, model] of slots) {
      const wants1M = context1M && ctx.modelsWithoutContext1M?.has(model) !== true;
      changes.push(this.envKey(file, slot, wants1M ? claudeCodeWith1M(slot, model) : model));
    }
    const smallFast = ctx.extraString('smallFastModel');
    if (smallFast && slots.every(([slot]) => slot !== 'ANTHROPIC_DEFAULT_HAIKU_MODEL'))
      changes.push(this.envKey(file, 'ANTHROPIC_DEFAULT_HAIKU_MODEL', smallFast));

    const thinking = getIntOption(ctx.extras, ClientOptionKeys.maxThinkingTokens);
    if (thinking !== null) changes.push(this.envKey(file, 'MAX_THINKING_TOKENS', String(thinking)));
    if (getBoolOption(ctx.extras, ClientOptionKeys.disablePromptCaching))
      changes.push(this.envKey(file, 'DISABLE_PROMPT_CACHING', '1'));

    // An existing ANTHROPIC_API_KEY would fight the gateway auth token: remove it (recorded, restored on disable).
    const apiKeyBefore = this.currentValue(file, 'json', API_KEY_PATH);
    if (apiKeyBefore !== null) changes.push(new ConfigChange(file, 'json', API_KEY_PATH, apiKeyBefore, null));
    return this.buildPlan(changes);
  }

  planDisable(): ConfigChangePlan {
    const file = this.settingsFile;
    const changes: ConfigChange[] = [];
    for (const key of ClaudeCodeClientAdapter.managedEnvKeys) {
      const keyPath = `env.${key}`;
      const before = this.currentValue(file, 'json', keyPath);
      if (before !== null) changes.push(new ConfigChange(file, 'json', keyPath, before, null));
    }
    // If we removed ANTHROPIC_API_KEY on enable, put it back.
    const entry = this.store.get(this.kind, file, API_KEY_PATH);
    if (entry && !entry.originalAbsent && this.currentValue(file, 'json', API_KEY_PATH) === null)
      changes.push(new ConfigChange(file, 'json', API_KEY_PATH, null, entry.originalValueJson));
    return this.buildPlan(changes);
  }

  planPurge(): ConfigChangePlan {
    return this.planDisable();
  }

  inspect(): ClientStatus {
    const detection = this.detect();
    const entry = this.store.get(this.kind, this.settingsFile, 'env.ANTHROPIC_BASE_URL');
    const enabled =
      entry !== null &&
      equalsValue('json', this.currentValue(this.settingsFile, 'json', 'env.ANTHROPIC_BASE_URL'), entry.appliedValueJson);

    const warnings: string[] = [];
    if (this.currentValue(this.settingsFile, 'json', 'apiKeyHelper') !== null)
      warnings.push('settings.json sets apiKeyHelper; it may override the gateway auth token (not modified by Astra).');
    return this.buildStatus(detection, enabled, warnings);
  }

  private envKey(file: string, key: string, value: string): ConfigChange {
    const keyPath = `env.${key}`;
    const before = this.currentValue(file, 'json', keyPath);
    return new ConfigChange(file, 'json', keyPath, before, ClientAdapterBase.jsonString(value));
  }
}
