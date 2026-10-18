import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { ClientConfigApplier } from '../src/applier.js';
import { ConfigChangePlan, EnableContext, type JsonObject, type RestoreReport } from '../src/contracts.js';
import { ALL_CLIENT_KINDS } from '../src/domain/kinds.js';
import { ClientEnvironment } from '../src/env.js';
import { ClientAdapterRegistry } from '../src/registry.js';
import { InMemoryStateStore } from '../src/state.js';

/**
 * Runs the golden cases shared with the C# tests (tests/Astra.Clients.Tests/GoldenClientConfigTests.cs): the same
 * steps over the same fake home must give the same plans, state, reports and files. The fake home appears as
 * `$HOME`. A client whose adapter is not ported yet is skipped; the coverage test names it.
 */
const ROOT = path.resolve(fileURLToPath(new URL('.', import.meta.url)), '../../../tests/fixtures/client-config');
const HOME = '$HOME';

interface Step {
  op: 'enable' | 'disable' | 'force-restore' | 'purge';
  context?: Record<string, unknown>;
  edit?: Array<{ file: string; write?: string; delete?: boolean }>;
}

interface CaseSpec {
  client: string;
  env?: Record<string, string>;
  steps: Step[];
  [key: string]: unknown;
}

const walk = (dir: string): string[] =>
  fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) => {
    const full = path.join(dir, e.name);
    return e.isDirectory() ? walk(full) : [full];
  });

const cases = (): string[] =>
  fs.existsSync(ROOT)
    ? walk(ROOT)
        .filter((f) => path.basename(f) === 'case.json')
        .map((f) => path.relative(ROOT, path.dirname(f)).split(path.sep).join('/'))
        .sort()
    : [];

const fill = (text: string, home: string) => text.split(HOME).join(home);
const normalize = (text: string, home: string) => text.split(home).join(HOME);
const normalizeOrNull = (text: string | null, home: string) => (text === null ? null : normalize(text, home));

function copyHome(source: string, home: string): void {
  if (!fs.existsSync(source)) return;
  for (const file of walk(source)) {
    const target = path.join(home, path.relative(source, file));
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, fill(fs.readFileSync(file).toString('utf8'), home));
  }
}

function snapshot(home: string): Map<string, string> {
  const result = new Map<string, string>();
  for (const file of walk(home).sort()) {
    const relative = path.relative(home, file).split(path.sep).join('/');
    if (relative.startsWith('.astra/')) continue;
    result.set(relative, normalize(fs.readFileSync(file).toString('utf8'), home));
  }
  return result;
}

const planJson = (plan: ConfigChangePlan, home: string) =>
  plan.changes.map((c) => ({
    file: normalize(c.file, home),
    format: c.format,
    keyPath: c.keyPath,
    kind: c.kind,
    before: normalizeOrNull(c.before, home),
    after: normalizeOrNull(c.after, home),
  }));

const reportJson = (report: RestoreReport, home: string) => ({
  keys: report.keys.map((k) => ({
    file: normalize(k.filePath, home),
    keyPath: k.keyPath,
    outcome: k.outcome,
    detail: k.detail ?? null,
  })),
  deletedFiles: report.deletedFiles.map((f) => normalize(f, home)),
  warnings: report.warnings.map((w) => normalize(w, home)),
});

function context(spec: CaseSpec, step: Step): EnableContext {
  const pick = (key: string): unknown => step.context?.[key] ?? spec[key];
  const without = pick('modelsWithoutContext1M');
  const extras = pick('extras');
  return new EnableContext({
    gatewayBaseUrl: pick('gatewayBaseUrl') as string,
    localKey: pick('localKey') as string,
    model: (pick('model') as string | null | undefined) ?? null,
    extras: extras ? (structuredClone(extras) as JsonObject) : null,
    modelsWithoutContext1M: Array.isArray(without) ? new Set(without as string[]) : null,
  });
}

function run(name: string): { expected: Map<string, string>; actual: Map<string, string> } {
  const caseDir = path.join(ROOT, name);
  const spec = JSON.parse(fs.readFileSync(path.join(caseDir, 'case.json'), 'utf8')) as CaseSpec;
  const home = fs.realpathSync(fs.mkdtempSync(path.join(os.tmpdir(), 'astra-golden-')));
  try {
    const variables: Record<string, string> = {};
    for (const [k, v] of Object.entries(spec.env ?? {})) variables[k] = fill(v, home);
    const env = new ClientEnvironment(home, 'osx', { lookup: (n) => variables[n] });
    const store = new InMemoryStateStore();
    const applier = new ClientConfigApplier(store, env, path.join(home, '.astra', 'backups', 'client-configs'));
    const adapter = ClientAdapterRegistry.createDefault(env, store).require(spec.client);
    copyHome(path.join(caseDir, 'home'), home);

    const actual = new Map<string, string>();
    spec.steps.forEach((step, i) => {
      const prefix = `${String(i + 1).padStart(2, '0')}-${step.op}`;
      for (const edit of step.edit ?? []) {
        const file = path.join(home, edit.file);
        if (edit.delete) fs.rmSync(file);
        else {
          fs.mkdirSync(path.dirname(file), { recursive: true });
          fs.writeFileSync(file, fill(edit.write!, home));
        }
      }
      switch (step.op) {
        case 'enable': {
          const plan = adapter.planEnable(context(spec, step));
          actual.set(`${prefix}/plan.json`, JSON.stringify(planJson(plan, home)));
          applier.apply(plan);
          break;
        }
        case 'disable':
          actual.set(`${prefix}/report.json`, JSON.stringify(reportJson(applier.disable(adapter.planDisable()), home)));
          break;
        case 'force-restore':
          actual.set(`${prefix}/report.json`, JSON.stringify(reportJson(applier.forceRestore(adapter.planDisable()), home)));
          break;
        case 'purge':
          actual.set(`${prefix}/report.json`, JSON.stringify(reportJson(applier.disable(adapter.planPurge()), home)));
          break;
      }
      actual.set(
        `${prefix}/state.json`,
        JSON.stringify(
          store.list(adapter.kind).map((e) => ({
            file: normalize(e.filePath, home),
            keyPath: e.keyPath,
            originalAbsent: e.originalAbsent,
            original: normalizeOrNull(e.originalValueJson, home),
            applied: normalizeOrNull(e.appliedValueJson, home),
          })),
        ),
      );
      for (const [relative, text] of snapshot(home)) actual.set(`${prefix}/files/${relative}`, text);
    });

    const expectedDir = path.join(caseDir, 'expected');
    const expected = new Map<string, string>();
    for (const file of walk(expectedDir))
      expected.set(path.relative(expectedDir, file).split(path.sep).join('/'), fs.readFileSync(file).toString('utf8'));
    return { expected, actual };
  } finally {
    fs.rmSync(home, { recursive: true, force: true });
  }
}

/** The C# state list is ordered by file then key (ordinal); the TS store lists in insertion order. */
const sortState = (text: string): unknown => {
  const list = JSON.parse(text) as Array<{ file: string; keyPath: string }>;
  const cmp = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0);
  return list.sort((a, b) => cmp(a.file, b.file) || cmp(a.keyPath, b.keyPath));
};

const ported = (client: string): boolean =>
  new ClientAdapterRegistry([]).get(client) === undefined &&
  ClientAdapterRegistry.createDefault(new ClientEnvironment('/', 'osx'), new InMemoryStateStore()).get(client) !== undefined;

describe('golden cases shared with the C# adapters', () => {
  for (const name of cases()) {
    const client = name.split('/')[0]!;
    it.skipIf(!ported(client))(name, () => {
      const { expected, actual } = run(name);
      expect([...actual.keys()].sort()).toEqual([...expected.keys()].sort());
      for (const [relative, text] of actual) {
        if (relative.endsWith('/state.json')) expect(sortState(text), relative).toEqual(sortState(expected.get(relative)!));
        else if (relative.endsWith('.json') && !relative.includes('/files/'))
          expect(JSON.parse(text), relative).toEqual(JSON.parse(expected.get(relative)!));
        else expect(text, relative).toBe(expected.get(relative));
      }
    });
  }

  it('has a ported adapter for every client that has golden cases', () => {
    const registry = ClientAdapterRegistry.createDefault(new ClientEnvironment('/', 'osx'), new InMemoryStateStore());
    const withCases = [...new Set(cases().map((n) => n.split('/')[0]!))];
    expect(withCases.filter((c) => !registry.get(c))).toEqual([]);
  });

  it('knows only client kinds that exist', () => {
    for (const name of cases()) expect(ALL_CLIENT_KINDS).toContain(name.split('/')[0]);
  });
});
