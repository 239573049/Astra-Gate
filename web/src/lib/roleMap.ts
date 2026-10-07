/** Claude Desktop role ids the gateway maps to provider models (plan §7.5). */
export const ROLES = ['sonnet', 'opus', 'haiku'] as const;
export type Role = (typeof ROLES)[number];
export type RoleMap = Partial<Record<Role, string>>;

/** The non-empty, known roles of a client's extras.roleMap. */
export function rolesOf(extras: Record<string, unknown> | null | undefined): RoleMap {
  const raw = extras?.roleMap;
  const out: RoleMap = {};
  if (!raw || typeof raw !== 'object') return out;
  for (const r of ROLES) {
    const v = (raw as Record<string, unknown>)[r];
    if (typeof v === 'string' && v.trim()) out[r] = v.trim();
  }
  return out;
}

/** Stable comparison key of a role map. */
export const roleKey = (m: RoleMap) => ROLES.map((r) => m[r] ?? '').join('\n');
