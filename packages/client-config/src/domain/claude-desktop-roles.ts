import type { JsonObject } from '../contracts.js';

/** Claude Desktop only accepts Claude role model ids (Astra.Core ClaudeDesktopRoles). */
export const CLAUDE_DESKTOP_ROLES_KEY = 'roleMap';

/** Roles in fallback order: an unmapped role uses the first mapped one of this list. */
export const CLAUDE_DESKTOP_ROLES: readonly string[] = ['sonnet', 'opus', 'haiku'];

/** The role id advertised on /v1/models for each role. */
export function claudeDesktopAdvertisedId(role: string): string {
  switch (role) {
    case 'opus':
      return 'claude-opus-5-5';
    case 'haiku':
      return 'claude-haiku-4-5';
    default:
      return 'claude-sonnet-5-5';
  }
}

/** The non-empty role → model entries of an extras object, in role order. */
export function parseClaudeDesktopRoles(extras: JsonObject | null | undefined): Array<[string, string]> {
  const result: Array<[string, string]> = [];
  const map = extras?.[CLAUDE_DESKTOP_ROLES_KEY];
  if (map === null || typeof map !== 'object' || Array.isArray(map)) return result;
  for (const role of CLAUDE_DESKTOP_ROLES) {
    const model = (map as JsonObject)[role];
    if (typeof model === 'string' && model.trim() !== '') result.push([role, model.trim()]);
  }
  return result;
}
