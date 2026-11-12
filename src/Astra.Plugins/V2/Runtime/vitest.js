// The `vitest` a plugin's tests import when `astra plugin test` runs them in the sandbox (design/plugin-v2 06 §4): describe /
// it / test (+ .skip, .only), beforeEach / afterEach / beforeAll / afterAll and expect with the common matchers. The same
// test files run under real vitest in Node (with the SDK's in-memory host); here they run against the real host calls.
const root = { name: '', tests: [], suites: [], before: [], after: [], beforeAll: [], afterAll: [], parent: null };
let current = root;
let only = false;

const add = (name, fn, mode) => {
  if (mode === 'only') only = true;
  current.tests.push({ name: String(name), fn, mode });
};
export const describe = (name, fn) => {
  const suite = { name: String(name), tests: [], suites: [], before: [], after: [], beforeAll: [], afterAll: [], parent: current };
  current.suites.push(suite);
  const prev = current;
  current = suite;
  try { fn(); } finally { current = prev; }
};
describe.skip = () => {};
export const it = (name, fn) => add(name, fn, 'run');
it.skip = (name) => add(name, null, 'skip');
it.only = (name, fn) => add(name, fn, 'only');
it.each = (rows) => (name, fn) => rows.forEach((row, i) => add(String(name).replace('%s', String(Array.isArray(row) ? row[0] : row)).replace('%i', String(i)), () => fn(...(Array.isArray(row) ? row : [row])), 'run'));
export const test = it;
export const beforeEach = (fn) => current.before.push(fn);
export const afterEach = (fn) => current.after.push(fn);
export const beforeAll = (fn) => current.beforeAll.push(fn);
export const afterAll = (fn) => current.afterAll.push(fn);
export const vi = { fn: (impl) => { const f = (...a) => { f.mock.calls.push(a); return impl ? impl(...a) : undefined; }; f.mock = { calls: [] }; return f; } };

const show = (v) => { try { return JSON.stringify(v); } catch { return String(v); } };
const equal = (a, b) => {
  if (Object.is(a, b)) return true;
  if (typeof a !== 'object' || typeof b !== 'object' || a === null || b === null) return false;
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  const ka = Object.keys(a).filter((k) => a[k] !== undefined), kb = Object.keys(b).filter((k) => b[k] !== undefined);
  return ka.length === kb.length && ka.every((k) => equal(a[k], b[k]));
};
const fail = (message) => { const e = new Error(message); e.name = 'AssertionError'; throw e; };

export const expect = (actual) => {
  const matchers = (not) => {
    const check = (ok, message) => { if (ok === not) fail((not ? 'not: ' : '') + message); };
    return {
      toBe: (v) => check(Object.is(actual, v), `expected ${show(actual)} to be ${show(v)}`),
      toEqual: (v) => check(equal(actual, v), `expected ${show(actual)} to equal ${show(v)}`),
      toStrictEqual: (v) => check(equal(actual, v), `expected ${show(actual)} to equal ${show(v)}`),
      toBeTruthy: () => check(!!actual, `expected ${show(actual)} to be truthy`),
      toBeFalsy: () => check(!actual, `expected ${show(actual)} to be falsy`),
      toBeNull: () => check(actual === null, `expected ${show(actual)} to be null`),
      toBeUndefined: () => check(actual === undefined, `expected ${show(actual)} to be undefined`),
      toBeDefined: () => check(actual !== undefined, 'expected a value'),
      toBeGreaterThan: (v) => check(actual > v, `expected ${show(actual)} > ${show(v)}`),
      toBeGreaterThanOrEqual: (v) => check(actual >= v, `expected ${show(actual)} >= ${show(v)}`),
      toBeLessThan: (v) => check(actual < v, `expected ${show(actual)} < ${show(v)}`),
      toBeLessThanOrEqual: (v) => check(actual <= v, `expected ${show(actual)} <= ${show(v)}`),
      toHaveLength: (n) => check(actual != null && actual.length === n, `expected length ${n}, got ${actual == null ? actual : actual.length}`),
      toContain: (v) => check(actual != null && actual.includes(v), `expected ${show(actual)} to contain ${show(v)}`),
      toContainEqual: (v) => check(Array.isArray(actual) && actual.some((x) => equal(x, v)), `expected ${show(actual)} to contain ${show(v)}`),
      toMatch: (re) => check(typeof actual === 'string' && (re instanceof RegExp ? re.test(actual) : actual.includes(re)), `expected ${show(actual)} to match ${String(re)}`),
      toMatchObject: (v) => check(actual !== null && typeof actual === 'object' && Object.keys(v).every((k) => equal(actual[k], v[k])), `expected ${show(actual)} to match ${show(v)}`),
      toHaveProperty: (k) => check(actual != null && k in Object(actual), `expected a property ${k}`),
      toHaveBeenCalled: () => check(actual && actual.mock && actual.mock.calls.length > 0, 'expected a call'),
      toHaveBeenCalledTimes: (n) => check(actual && actual.mock && actual.mock.calls.length === n, `expected ${n} calls`),
      toThrow: (m) => {
        let thrown = null;
        try { actual(); } catch (e) { thrown = e; }
        check(thrown !== null && (m === undefined || String(thrown && thrown.message).includes(m instanceof RegExp ? '' : m) && (!(m instanceof RegExp) || m.test(thrown.message))), `expected a throw${m ? ' matching ' + String(m) : ''}`);
      },
    };
  };
  const m = matchers(false);
  m.not = matchers(true);
  const names = Object.keys(m).filter((k) => k !== 'not');
  m.resolves = Object.fromEntries(names.map((k) => [k, async (...a) => expect(await actual)[k](...a)]));
  m.rejects = Object.fromEntries(names.map((k) => [k, async (...a) => {
    let e = null;
    try { await actual; } catch (x) { e = x; }
    if (e === null) fail('expected a rejection');
    return expect(() => { throw e; })[k](...a);
  }]));
  return m;
};

// run on the host's test.run operation: every test in order, its hooks around it; a failure doesn't stop the rest
globalThis.__astraTestRun = async () => {
  const results = [];
  const chain = (s) => (s ? [...chain(s.parent), s] : []);
  const walk = async (suite, prefix) => {
    for (const fn of suite.beforeAll) await fn();
    for (const t of suite.tests) {
      const name = prefix + t.name;
      if (t.mode === 'skip' || (only && t.mode !== 'only')) { results.push({ name, status: 'skipped' }); continue; }
      const started = Date.now();
      try {
        for (const s of chain(suite)) for (const fn of s.before) await fn();
        await t.fn();
        for (const s of chain(suite).reverse()) for (const fn of s.after) await fn();
        results.push({ name, status: 'passed', ms: Date.now() - started });
      } catch (e) {
        results.push({ name, status: 'failed', ms: Date.now() - started, error: String(e && e.message || e) });
      }
    }
    for (const s of suite.suites) await walk(s, prefix + s.name + ' › ');
    for (const fn of suite.afterAll) await fn();
  };
  await walk(root, '');
  return { tests: results };
};
