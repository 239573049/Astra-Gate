import { AstraError } from '../errors.js';

export type SupportedPlatform = 'darwin' | 'linux' | 'win32';

export interface PlatformInfo {
  platform: SupportedPlatform;
  arch: string;
  /** .NET-style runtime identifier, e.g. osx-arm64 */
  rid: string;
  serverPackage: string;
  desktopPackage: string;
  /** directory name under npm/ */
  serverDir: string;
  desktopDir: string;
  /** file name of the server binary */
  binaryName: string;
}

const RID_PREFIX: Record<SupportedPlatform, string> = { darwin: 'osx', linux: 'linux', win32: 'win' };
const SUPPORTED_ARCHES = ['x64', 'arm64'];

export function platformInfo(
  platform: string = process.platform,
  arch: string = process.arch,
): PlatformInfo {
  const ridPrefix = (RID_PREFIX as Record<string, string | undefined>)[platform];
  if (!ridPrefix || !SUPPORTED_ARCHES.includes(arch)) {
    throw new AstraError(
      `Astra does not ship binaries for ${platform}/${arch}.`,
      'Supported platforms: macOS (x64, arm64), Linux (x64, arm64), Windows (x64, arm64).',
    );
  }
  const p = platform as SupportedPlatform;
  const dir = `${p}-${arch}`;
  return {
    platform: p,
    arch,
    rid: `${ridPrefix}-${arch}`,
    serverPackage: `@aidotnet/server-${dir}`,
    desktopPackage: `@aidotnet/desktop-${dir}`,
    serverDir: `server-${dir}`,
    desktopDir: `desktop-${dir}`,
    binaryName: p === 'win32' ? 'astra-server.exe' : 'astra-server',
  };
}

export const CLIENT_KINDS = [
  'codex',
  'claude-code',
  'gemini-cli',
  'opencode',
  'claude-desktop',
  'grok-build',
  'pi',
  'hermes-agent',
  'minimax-code',
  'copilot-cli',
  'vscode-copilot',
  'crush',
  'qwen-code',
  'droid',
  'kimi-code',
  'zed',
  'vscode-insiders',
  'vscodium',
  'omp',
  'mimo-code',
  'deepseek-harness',
  'workbuddy',
  'workbuddy-ai',
  'muse-code',
  'paseo',
] as const;

/** A client a plugin adds (a magpie agent plugin): "plugin-" and magpie's agent id. */
export type PluginClientKind = `plugin-${string}`;

export type ClientKind = (typeof CLIENT_KINDS)[number] | PluginClientKind;

const PLUGIN_CLIENT_KIND = /^plugin-[a-z0-9][a-z0-9._-]{0,63}$/;

/** Whether `kind` is a built-in client kind or a plugin's. */
export function isClientKind(kind: string): kind is ClientKind {
  return (CLIENT_KINDS as readonly string[]).includes(kind) || PLUGIN_CLIENT_KIND.test(kind);
}
