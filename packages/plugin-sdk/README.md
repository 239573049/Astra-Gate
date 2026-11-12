# @astragate/plugin-sdk

Tools for writing **Astra v2 plugins** (design: `design/plugin-v2/` in the Astra repository).

- **Typings** of the `astra` module a plugin imports, with the UI 1.0 components generated from the host's own schema.
- **A test harness** (`@astragate/plugin-sdk/testing`): `mount` a view and press / change it like a shell does,
  `runCommand`, `runHook` — on the same `boot.js` / `astra.js` the server runs, over an in-memory host.
- **Page patterns** (`@astragate/plugin-sdk/patterns`): `SettingsPage`, `ListDetail`, `Wizard`, `Dashboard`, `CrudTable` —
  plain functions over UI 1.0 components, no shell support needed.
- **`astra-plugin`**: `init` a starter, `validate` it, `build` single-file bundles, `pack` an installable tarball (prints
  the sha256 and sha512 integrity a market entry needs).

```sh
npx @astragate/plugin-sdk init my-plugin --id me.my-plugin   # or: npx astra-plugin init …
cd my-plugin && npm install && npm test
npx astra-plugin build && npx astra-plugin pack dist
```

## Building

The sandbox imports only files beside the module, never packages. `astra-plugin build [dir] [--out dist] [--watch]` bundles
`main` and `gateway` with esbuild into one ESM file each (`astra` stays the host's module), copies the manifest (pointing
at the bundles), `locales/`, `assets/` and a relative icon into `dist/`, and validates the result. Pack `dist`, not the
source folder (`pack` leaves `dist/` out of a source pack).

## Validating

`astra-plugin validate` checks the manifest the way Astra does, then reads the code:

- an astra API the code calls without its permission (`storage`, `secrets`, `notifications`, `jobs`, `events`, `data.*`,
  `http.fetch` without a `net:` permission, a literal `http.fetch('https://…')` to an origin no `net:` names, `ctx.reject` /
  `ctx.setHeader` / `ctx.emit` in the gateway module) is an error; a permission the code never uses is a warning;
- `permissions.request(...)` of something not in `optionalPermissions`, and an emitted event `contributes.events` does
  not declare, are errors;
- with `locales/*.json`, a literal `t('key')` missing from every locale is an error, from some a warning;
- a package over 5 MB is a warning.

Tests (`test/`, `*.test.js`) are not scanned.

## Using the typings

Point `astra` at the SDK so TypeScript (and your editor) find it:

```json
{ "compilerOptions": { "paths": { "astra": ["./node_modules/@astragate/plugin-sdk"] } } }
```

## Testing

Alias `astra` to the runtime in your test runner (vitest shown):

```js
import { defineConfig } from 'vitest/config';
export default defineConfig({ resolve: { alias: { astra: '@astragate/plugin-sdk/runtime' } } });
```

```js
import { mount } from '@astragate/plugin-sdk/testing';
import { home } from '../main.js';

const view = await mount(home, { storage: { seen: 3 } });
await view.press('refresh');
expect(view.find('MetricCard').props.value).toBe(3);
```

Every render in `mount` is checked against the UI schema the host uses: an unknown component or prop, a wrong prop type,
an event naming an action the view does not have, a missing `a11yLabel` / `alt`, a wrong child count or a root a slot does
not allow fails the render (`mount(view, { strict: false })` collects them in `view.warnings` instead); unkeyed lists, a
used `fallback` and a tree or state over 80% of the host's limits are warnings. `validateTree(tree, { actions })` runs the
same checks on any tree.

## Patterns

```js
import { ui } from 'astra';
import { CrudTable } from '@astragate/plugin-sdk/patterns';

export const rules = ui.defineView({
  init: () => ({ rows: [], sheet: { open: false } }),
  render: (s) => CrudTable({
    title: 'Rules', columns: [{ key: 'word', title: 'Word' }], rows: s.rows, rowKey: 'id',
    createAction: 'create', editAction: 'edit', saveAction: 'save', closeAction: 'close', deleteAction: 'remove',
    sheet: { ...s.sheet, content: ui.TextField({ name: 'word' }) },
  }),
  actions: { /* create, edit, save, close, remove */ },
});
```

`ListDetail` takes `width: ctx.client.width` and becomes list → detail navigation on a compact container. Patterns have
no imports, so `build` bundles them like any other file; in Node tests they need nothing extra.

`validate` follows Astra's rules closely but Astra stays the judge: the console reports anything it refuses on install.

## Maintainers

`runtime/boot.js`, `runtime/astra.js`, `runtime/components.json` and `src/components.g.d.ts` are copies of / generated
from `src/Astra.Plugins/V2` — run `pnpm --filter @astragate/plugin-sdk gen` after changing them there; `test` fails
while they are stale.
