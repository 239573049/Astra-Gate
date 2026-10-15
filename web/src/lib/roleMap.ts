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

/** Name parts that mark a provider's small, fast tier — what Claude Desktop's haiku role stands for. */
const SMALL_MODEL = /(^|[-_.:/\s])(haiku|flash|mini|lite|nano|small|fast|air)($|[-_.:/\s])/i;

/**
 * A role map suggested from a provider's models, for a Claude Desktop that has none yet: the model named after the
 * role when there is one (Claude models), else the first full-size model for sonnet and opus and the first small one
 * (flash, mini, air …) for haiku. Empty when there are no models.
 */
export function suggestRoles(models: readonly string[]): RoleMap {
  if (models.length === 0) return {};
  const named = (r: Role) => models.find((m) => m.toLowerCase().includes(r));
  const large = models.find((m) => !SMALL_MODEL.test(m)) ?? models[0];
  const small = models.find((m) => SMALL_MODEL.test(m)) ?? large;
  return { sonnet: named('sonnet') ?? large, opus: named('opus') ?? named('sonnet') ?? large, haiku: named('haiku') ?? small };
}
