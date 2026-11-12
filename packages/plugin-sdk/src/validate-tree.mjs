// Dev-time checks of a rendered UI tree against the host's schema (design/plugin-v2 06 §3). The host normalizes every
// tree too (src/Astra.Plugins/V2/Ui/UiTree.cs) but only warns and drops what is wrong; here the same mistakes fail the
// test that rendered them, while it is cheap to fix.
import fs from 'node:fs';

const schema = JSON.parse(fs.readFileSync(new URL('../runtime/components.json', import.meta.url), 'utf8'));

/** The host's limits (UiTree.MaxNodes / MaxBytes, the 256 KB view state). */
export const LIMITS = { nodes: 5000, bytes: 1024 * 1024, state: 256 * 1024 };

const propsOf = (component) => ({ ...schema.common, ...(component.input ? schema.inputProps : {}), ...component.props });
const isObj = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
const isNode = (v) => isObj(v) && typeof v.type === 'string';

function typeProblem(kind, value, actions) {
  switch (kind) {
    case 'string':
    case 'text':
      return ['string', 'number', 'boolean'].includes(typeof value) ? null : 'expected text';
    case 'number':
      return typeof value === 'number' && Number.isFinite(value) ? null : 'expected a number';
    case 'boolean':
      return typeof value === 'boolean' ? null : 'expected true or false';
    case 'json':
      return null;
    case 'node':
      return isNode(value) ? null : 'expected a node';
    case 'nodes':
      return isNode(value) || (Array.isArray(value) && value.every((v) => isNode(v) || typeof v === 'string')) ? null : 'expected nodes';
    case 'action': {
      const name = typeof value === 'string' ? value : isObj(value) && typeof value.action === 'string' ? value.action : null;
      if (name === null) return 'an event takes an action name or { action, args }';
      if (actions && !actions.includes(name)) return `the view has no action "${name}"`;
      if (isObj(value) && value.args !== undefined && JSON.stringify(value.args).length > 16 * 1024) return 'args larger than 16 KiB are dropped';
      return null;
    }
  }
  if (kind.startsWith('enum:')) {
    const allowed = kind.slice(5).split('|');
    return allowed.includes(value) ? null : `expected one of ${allowed.join(', ')}`;
  }
  if (kind.startsWith('token:')) {
    const token = kind.slice(6);
    if (token === 'icon') return typeof value === 'string' && value.length > 0 && value.length <= 200 ? null : 'expected an icon name';
    if (token === 'width' && (typeof value === 'number' || isObj(value))) return null;
    if (token === 'gap' && isObj(value)) return null;
    const v = typeof value === 'number' ? String(value) : value;
    return (schema.tokens[token] ?? []).includes(v) ? null : `not a ${token} token (${(schema.tokens[token] ?? []).join(', ')})`;
  }
  return null;
}

/**
 * Checks `tree` (a node or an array of nodes). `actions` are the view's action names (null skips the check), `surface` a
 * slot whose roots are limited (tray.panel), `state` the view state for its size check.
 * Returns { errors, warnings }, each a list of "path: message" strings.
 */
export function validateTree(tree, { actions = null, slot = null, state } = {}) {
  const errors = [];
  const warnings = [];
  let count = 0;

  const visit = (node, where) => {
    count++;
    if (!isNode(node)) {
      errors.push(`${where}: not a node (${JSON.stringify(node)?.slice(0, 60)})`);
      return;
    }
    const component = schema.components[node.type];
    const at = `${where}.${node.type}`;
    const props = isObj(node.props) ? node.props : {};
    if (!component) {
      if (isNode(props.fallback)) {
        warnings.push(`${at}: not a component of UI ${schema.version}; shells show its fallback`);
        visit(props.fallback, `${at}.fallback`);
      } else errors.push(`${where}: unknown component "${node.type}"`);
      return;
    }
    const known = propsOf(component);
    for (const [name, value] of Object.entries(props)) {
      if (value === null || value === undefined) continue;
      const kind = known[name];
      if (!kind) {
        errors.push(`${at}: unknown prop "${name}"`);
        continue;
      }
      const problem = typeProblem(kind, value, actions);
      if (problem) errors.push(`${at}.${name}: ${problem}`);
      else if (kind === 'node') visit(value, `${at}.${name}`);
      else if (kind === 'nodes') (Array.isArray(value) ? value : [value]).forEach((v, i) => isNode(v) && visit(v, `${at}.${name}[${i}]`));
    }
    for (const need of component.required ?? [])
      if (props[need] === undefined || props[need] === '') errors.push(`${at}: ${need} is required`);

    const kids = Array.isArray(node.children) ? node.children : [];
    if (kids.length === 0) return;
    const mode = component.children;
    if (mode === 'none') {
      if (!['Text', 'Heading'].includes(node.type)) errors.push(`${at}: takes no children`);
      return;
    }
    if (mode === 'text') return;
    if (mode === 'one' && kids.length !== 1) errors.push(`${at}: takes one child, has ${kids.length}`);
    if (mode === 'two' && kids.length !== 2) errors.push(`${at}: takes two children, has ${kids.length}`);
    // a list made by mapping data: several siblings of one type and no keys — patches then rebuild instead of moving rows
    const byType = new Map();
    for (const k of kids) if (isNode(k)) byType.set(k.type, [...(byType.get(k.type) ?? []), k]);
    for (const [type, same] of byType)
      if (same.length >= 3 && same.some((k) => k.key === undefined)) warnings.push(`${at}: ${same.length} ${type} children without a key on each`);
    const keys = new Set();
    kids.forEach((k, i) => {
      if (isNode(k) && k.key !== undefined) {
        if (keys.has(k.key)) errors.push(`${at}[${i}]: two children have the key "${k.key}"`);
        keys.add(k.key);
      }
      if (typeof k !== 'string') visit(k, `${at}[${i}]`);
    });
  };

  const roots = Array.isArray(tree) ? tree : [tree];
  roots.forEach((r, i) => visit(r, roots.length > 1 ? `[${i}]` : 'root'));
  if (slot && schema.slotRoots?.[slot])
    for (const r of roots)
      if (isNode(r) && !schema.slotRoots[slot].includes(r.type)) errors.push(`root: ${r.type} can't be the root of ${slot} (${schema.slotRoots[slot].join(', ')})`);

  const bytes = Buffer.byteLength(JSON.stringify(tree ?? null));
  if (count > LIMITS.nodes) errors.push(`the tree has ${count} nodes; the host allows ${LIMITS.nodes}`);
  else if (count > LIMITS.nodes * 0.8) warnings.push(`the tree has ${count} nodes, over 80% of the host's ${LIMITS.nodes}`);
  if (bytes > LIMITS.bytes) errors.push(`the tree is ${Math.round(bytes / 1024)} KiB; the host allows ${LIMITS.bytes / 1024} KiB — use a data channel`);
  else if (bytes > LIMITS.bytes * 0.8) warnings.push(`the tree is ${Math.round(bytes / 1024)} KiB, over 80% of the host's limit`);
  if (state !== undefined) {
    const s = Buffer.byteLength(JSON.stringify(state ?? null));
    if (s > LIMITS.state) errors.push(`the view state is ${Math.round(s / 1024)} KiB; the host keeps ${LIMITS.state / 1024} KiB`);
    else if (s > LIMITS.state * 0.8) warnings.push(`the view state is ${Math.round(s / 1024)} KiB, over 80% of the host's limit`);
  }
  return { errors, warnings, nodes: count, bytes };
}

export { schema };
