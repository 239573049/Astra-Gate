import { hasDrift } from '@aidotnet/client-config';
import { clientName } from '../lib/clients.js';
import { UserError, type Io } from '../lib/io.js';
import { deleteProfile, readProfile, writeProfile } from '../lib/profile.js';
import type { Runtime } from '../lib/runtime.js';
import { ask, prompts as p, type Ui } from '../lib/ui.js';
import { msg } from '../messages.js';

export interface LeaveOptions {
  clients?: string;
  force?: boolean;
  yes?: boolean;
  keepProfile?: boolean;
}

export interface LeaveResult {
  clients: Array<{ kind: string; name: string; restored: string[]; drifted: string[]; deletedFiles: string[] }>;
}

export async function leave(rt: Runtime, ui: Ui, _io: Io, opts: LeaveOptions): Promise<LeaveResult> {
  const profile = readProfile(rt.paths);
  const tracked = rt.registry.all.filter((a) => rt.store.list(a.kind).length > 0);
  if (!profile && tracked.length === 0) throw new UserError(msg.notJoined);

  const wanted = opts.clients?.split(',').map((s) => s.trim()).filter(Boolean);
  for (const kind of wanted ?? []) if (!rt.registry.get(kind)) throw new UserError(msg.unknownClient(kind));
  const targets = wanted ? wanted.map((k) => rt.registry.require(k)) : tracked;
  const result: LeaveResult = { clients: [] };
  if (targets.length === 0) {
    ui.info(msg.leaveNothing);
    return result;
  }

  if (ui.interactive && !opts.yes) {
    ui.note(targets.map((a) => clientName(a.kind)).join('\n'));
    if (!ask(await p.confirm({ message: msg.leaveConfirm, initialValue: true }))) throw new UserError(msg.cancelled, 130);
  }

  for (const adapter of targets) {
    const plan = adapter.planDisable();
    const report = opts.force ? rt.applier.forceRestore(plan) : rt.applier.disable(plan);
    const name = clientName(adapter.kind);
    const drifted = report.keys.filter((k) => k.outcome === 'drifted').map((k) => k.keyPath);
    result.clients.push({
      kind: adapter.kind,
      name,
      restored: report.keys.filter((k) => k.outcome === 'restored').map((k) => k.keyPath),
      drifted,
      deletedFiles: report.deletedFiles,
    });
    if (hasDrift(report)) ui.warn(msg.leaveDrift(name, drifted.join(', ')));
    else ui.success(msg.leaveRestored(name));
  }

  const remaining = rt.registry.all.filter((a) => rt.store.list(a.kind).length > 0);
  if (profile) {
    if (remaining.length === 0 && !opts.keepProfile) deleteProfile(rt.paths);
    else writeProfile(rt.paths, { ...profile, clients: profile.clients.filter((k) => remaining.some((a) => a.kind === k)), updatedAt: new Date().toISOString() });
  }
  if (result.clients.some((c) => c.drifted.length > 0)) process.exitCode = 1;
  else ui.outro(msg.leaveDone);
  return result;
}
