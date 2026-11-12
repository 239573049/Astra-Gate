// The `astra` module for Node: the in-memory host goes in first (install.mjs), then the exact module plugins import in
// Astra. Point your test runner's alias for `astra` here (vitest: resolve.alias { astra: '@astragate/plugin-sdk/runtime' }).
import './install.mjs';

export * from './astra.js';
export { default } from './astra.js';
