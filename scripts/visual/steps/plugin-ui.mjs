// Web steps for the v2 plugin UI scenes (tests/Astra.Desktop.UiTests/Scenes.PluginUi.cs): every scene of the scene set
// (tests/fixtures/plugin-ui, bundled in src/Astra.Plugins/V2/Ui/scenes.json) shown alone in the component gallery. The
// names follow Scenes.PluginUiName; the desktop registers all of them only with ASTRA_VISUAL_PLUGIN_UI=all, and a step
// without a recording is simply not captured.
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = dirname(dirname(dirname(dirname(fileURLToPath(import.meta.url)))));
const { scenes } = JSON.parse(readFileSync(join(repo, 'src/Astra.Plugins/V2/Ui/scenes.json'), 'utf8'));

/** Scenes.PluginUiName: "Button/variant-primary" → plugin-ui__button-variant-primary. */
export const pluginUiName = (id) => 'plugin-ui__' + id.replace('_patterns/', 'pattern-').replaceAll('/', '-').toLowerCase();

export const STEPS = Object.fromEntries(
  scenes.map((s) => [pluginUiName(s.id), { path: `/plugins/dev/gallery?scene=${encodeURIComponent(s.id)}`, settle: 800 }]),
);
