import { AstraError } from '../errors.js';
import { apiRequest } from '../lib/http.js';
import { ensureServerRunning } from '../lib/server-lifecycle.js';
import { printTable, success } from '../ui.js';

// `astra group`: named routing groups, the virtual models `group/<slug>` (magpie parity). The server holds them
// (`/api/routing/groups`); this is the command-line client, in magpie's `key=value` style:
//
//   astra group add "Opus anywhere" models=claude/claude-opus-5-5,copilot/claude-opus-5.5 routing=smart
//   astra group set opus-anywhere models+=openrouter/anthropic/claude-opus-5.5 routing=weight weight=claude/claude-opus-5-5=3
//   astra group show opus-anywhere
//   astra group rm opus-anywhere

export interface GroupMemberRow {
  provider?: string | null;
  model?: string | null;
  group?: string | null;
  weight: number;
}

export interface GroupRow {
  id: string;
  slug: string;
  modelId: string;
  name: string;
  mode: string;
  stays: string;
  enabled: boolean;
  listed: boolean;
  members: GroupMemberRow[];
}

interface MemberStatusRow {
  key: string;
  providerName?: string | null;
  weight: number;
  health: string;
  coolingUntil?: string | null;
  usedPercent?: number | null;
  resetsAt?: string | null;
  lastUsed?: string | null;
  problem?: string | null;
}

interface GroupStatusRow {
  lastDecisionAt?: string | null;
  lastReason?: string | null;
  members: MemberStatusRow[];
}

export const GROUP_MODES = ['smart', 'order', 'rotate', 'usage', 'pace', 'weight'] as const;
export const GROUP_STAYS = ['auto', 'session', 'turn', 'off', 'strict'] as const;
/** magpie's names for the same things. */
const MODE_ALIASES: Record<string, string> = { fast: 'smart', inorder: 'order', 'in-order': 'order', 'in-turn': 'rotate', 'least-used': 'usage' };

async function api<T>(path: string, method: 'GET' | 'POST' | 'PATCH' | 'DELETE' = 'GET', body?: unknown): Promise<T> {
  const { base, runtime, started } = await ensureServerRunning();
  if (started) console.log('Server was not running — started it in the background.');
  return apiRequest<T>({ base, path, method, body, runtimeToken: runtime?.runtimeToken });
}

/** `provider/model` or `group/slug`; a model id may hold slashes (`openrouter/anthropic/claude-opus`), only the first one splits. */
export function parseMember(text: string): GroupMemberRow {
  const at = text.indexOf('/');
  if (at <= 0 || at === text.length - 1)
    throw new AstraError(`"${text}" is not a member.`, 'Write a member as provider/model, or group/<slug> for another group.');
  const head = text.slice(0, at);
  const rest = text.slice(at + 1);
  return head === 'group' ? { group: rest, weight: 1 } : { provider: head, model: rest, weight: 1 };
}

const memberText = (m: GroupMemberRow): string => (m.group ? `group/${m.group}` : `${m.provider}/${m.model}`);

function list(value: string): string[] {
  return value
    .split(',')
    .map((s) => s.trim())
    .filter(Boolean);
}

export interface GroupEdit {
  name?: string;
  slug?: string;
  mode?: string;
  stays?: string;
  enabled?: boolean;
  listed?: boolean;
  members?: GroupMemberRow[];
}

const yes = (v: string): boolean => {
  if (['yes', 'true', 'on', '1'].includes(v.toLowerCase())) return true;
  if (['no', 'false', 'off', '0'].includes(v.toLowerCase())) return false;
  throw new AstraError(`"${v}" is not yes or no.`);
};

/**
 * Applies magpie-style `key=value` words to a group's current members: `models=` replaces the list, `models+=` appends,
 * `models-=` removes, `weight=member=3,…` sets weights (`weight=` alone resets them), `routing=`, `stays=`, `listed=`,
 * `enabled=`, `name=`, `slug=`.
 */
export function parseGroupWords(words: string[], current: GroupMemberRow[] = []): GroupEdit {
  const edit: GroupEdit = {};
  let members: GroupMemberRow[] | undefined;
  const weights = new Map<string, number>();
  let resetWeights = false;
  for (const word of words) {
    const m = /^([a-z]+)(\+=|-=|=)(.*)$/i.exec(word);
    if (!m) throw new AstraError(`"${word}" is not key=value.`, 'Example: models=claude/opus,copilot/opus routing=smart');
    const key = m[1]!.toLowerCase();
    const op = m[2]!;
    const value = m[3]!;
    switch (key) {
      case 'models':
      case 'members': {
        const items = list(value).map(parseMember);
        const base = members ?? current;
        if (op === '=') members = items;
        else if (op === '+=') members = [...base, ...items.filter((i) => !base.some((b) => memberText(b) === memberText(i)))];
        else members = base.filter((b) => !items.some((i) => memberText(i) === memberText(b)));
        break;
      }
      case 'routing':
      case 'mode': {
        const mode = MODE_ALIASES[value.toLowerCase()] ?? value.toLowerCase();
        if (!(GROUP_MODES as readonly string[]).includes(mode))
          throw new AstraError(`Unknown routing "${value}".`, `Use one of: ${GROUP_MODES.join(', ')}.`);
        edit.mode = mode;
        break;
      }
      case 'stays': {
        if (!(GROUP_STAYS as readonly string[]).includes(value.toLowerCase()))
          throw new AstraError(`Unknown stays "${value}".`, `Use one of: ${GROUP_STAYS.join(', ')}.`);
        edit.stays = value.toLowerCase();
        break;
      }
      case 'weight': {
        if (value === '') resetWeights = true;
        for (const item of list(value)) {
          const eq = item.lastIndexOf('=');
          const weight = Number(item.slice(eq + 1));
          if (eq <= 0 || !Number.isInteger(weight) || weight < 1 || weight > 1000)
            throw new AstraError(`"${item}" is not member=weight.`, 'A weight is a whole number from 1 to 1000, e.g. weight=claude/opus=3.');
          weights.set(memberText(parseMember(item.slice(0, eq))), weight);
        }
        break;
      }
      case 'listed':
        edit.listed = yes(value);
        break;
      case 'enabled':
        edit.enabled = yes(value);
        break;
      case 'name':
        edit.name = value;
        break;
      case 'slug':
        edit.slug = value;
        break;
      default:
        throw new AstraError(`Unknown setting "${key}".`, 'Use models, routing, stays, weight, listed, enabled, name or slug.');
    }
  }
  if (members || weights.size > 0 || resetWeights) {
    const base = (members ?? current).map((x) => ({ ...x }));
    for (const x of base) {
      if (resetWeights) x.weight = 1;
      const w = weights.get(memberText(x));
      if (w !== undefined) x.weight = w;
    }
    for (const key of weights.keys())
      if (!base.some((x) => memberText(x) === key)) throw new AstraError(`"${key}" is not a member of this group.`, 'Set weight= after models=.');
    edit.members = base;
  }
  return edit;
}

/** A group by id, slug (`opus` or `group/opus`) or exact name. */
export async function findGroup(ref: string): Promise<GroupRow> {
  const groups = await api<GroupRow[]>('/api/routing/groups');
  const needle = ref.toLowerCase().replace(/^group\//, '');
  const found =
    groups.find((g) => g.id.toLowerCase() === ref.toLowerCase()) ??
    groups.find((g) => g.slug === needle) ??
    groups.find((g) => g.name.toLowerCase() === ref.toLowerCase());
  if (!found) throw new AstraError(`No group matches "${ref}".`, 'Run `astra group list` to see the groups.');
  return found;
}

export async function runGroupList(): Promise<void> {
  const groups = await api<GroupRow[]>('/api/routing/groups');
  if (groups.length === 0) {
    console.log('No routing groups yet. Make one with: astra group add "Opus anywhere" models=claude/claude-opus-5-5,copilot/claude-opus-5.5 routing=smart');
    return;
  }
  printTable(
    groups.map((g) => [g.modelId, g.name, g.mode, g.stays, String(g.members.length), g.enabled ? (g.listed ? 'on' : 'on, unlisted') : 'off']),
    ['MODEL', 'NAME', 'ROUTING', 'STAYS', 'MEMBERS', 'STATE'],
  );
}

export async function runGroupShow(ref: string): Promise<void> {
  const group = await findGroup(ref);
  const status = await api<GroupStatusRow>(`/api/routing/groups/${encodeURIComponent(group.id)}/status`);
  console.log(`${group.name}  (${group.modelId})`);
  console.log(`routing=${group.mode} stays=${group.stays} ${group.enabled ? 'on' : 'off'}${group.listed ? '' : ' unlisted'}`);
  const live = new Map(status.members.map((m) => [m.key, m]));
  printTable(
    group.members.map((m, i) => {
      const s = live.get(memberText(m));
      const quota = s?.usedPercent != null ? `${Math.round(s.usedPercent)}% used${s.resetsAt ? `, renews ${s.resetsAt}` : ''}` : 'quota unknown';
      const state = s ? (s.health === 'cooling' && s.coolingUntil ? `cooling until ${s.coolingUntil}` : s.health) : '';
      return [String(i + 1), memberText(m), group.mode === 'weight' ? String(m.weight) : '', state, quota, s?.problem ?? ''];
    }),
    ['#', 'MEMBER', 'WEIGHT', 'STATE', 'QUOTA', 'PROBLEM'],
  );
  if (status.lastReason) console.log(`Last choice: ${status.lastReason}${status.lastDecisionAt ? ` (${status.lastDecisionAt})` : ''}`);
}

export async function runGroupAdd(name: string, words: string[]): Promise<void> {
  const edit = parseGroupWords(words);
  if (!edit.members?.length) throw new AstraError('A group needs members.', 'Add models=provider/model,provider/model.');
  const group = await api<GroupRow>('/api/routing/groups', 'POST', { name, ...edit });
  success(`Group ${group.modelId} created with ${group.members.length} member${group.members.length === 1 ? '' : 's'} (routing=${group.mode}).`);
}

export async function runGroupSet(ref: string, words: string[]): Promise<void> {
  const group = await findGroup(ref);
  const edit = parseGroupWords(words, group.members);
  if (Object.keys(edit).length === 0) throw new AstraError('Nothing to change.', 'Give key=value words, e.g. routing=usage.');
  await api<GroupRow>(`/api/routing/groups/${encodeURIComponent(group.id)}`, 'PATCH', edit);
  success(`Group ${group.modelId} updated.`);
}

export async function runGroupRemove(ref: string): Promise<void> {
  const group = await findGroup(ref);
  await api(`/api/routing/groups/${encodeURIComponent(group.id)}`, 'DELETE');
  success(`Group ${group.modelId} removed.`);
}
