import { describe, expect, it } from 'vitest';
import { JsoncEditor } from '../src/editing/jsonc-editor';
import { YamlEditor } from '../src/editing/yaml-editor';

const SELECTOR = 'providers.[id=astra]';

describe('nested yaml rows (providers.[id=astra])', () => {
  const text = '# mine\nproviders:\n  - id: mine\n    name: Mine\n\n# tools\ntools:\n  - id: web\n';

  it('adds, replaces and removes a row without touching the rest of the file', () => {
    const yaml = new YamlEditor(text);
    expect(yaml.has(SELECTOR)).toBe(false);
    const added = yaml.setRaw(
      SELECTOR,
      '{"id":"astra","name":"Astra","models":[{"id":"m","abilities":{"tools":{"supported":true}}}]}',
    );
    expect(added.has(SELECTOR)).toBe(true);
    expect(added.text.startsWith('# mine\nproviders:\n  - id: mine\n    name: Mine\n  - id: "astra"')).toBe(true);
    expect(added.text).toContain('# tools\ntools:\n  - id: web\n');
    expect(added.text).toContain('      - id: "m"\n        abilities:\n          tools:\n            supported: true\n');
    const replaced = added.setRaw(SELECTOR, '{"id":"astra","name":"Other"}');
    expect(replaced.text).toContain('name: "Other"');
    expect(replaced.text).not.toContain('abilities');
    expect(replaced.remove(SELECTOR).text).toBe(text);
  });

  it('writes the key when it is missing or an empty list, and drops it with the last row', () => {
    const empty = new YamlEditor('providers: []\n').setRaw(SELECTOR, '{"id":"astra"}');
    expect(empty.text).toBe('providers:\n  - id: "astra"\n');
    expect(empty.remove(SELECTOR).text).toBe('');
    expect(new YamlEditor('').setRaw(SELECTOR, '{"id":"astra"}').text).toBe('providers:\n  - id: "astra"\n');
  });
});

describe('an escaped dot in a JSON key path', () => {
  it('addresses a flat key that holds a dot', () => {
    const text = '{\n  // c\n  "a.b": 1,\n  "a": { "b": 2 }\n}\n';
    const editor = new JsoncEditor(text);
    expect(editor.get('a\\.b')).toBe(1);
    expect(editor.get('a.b')).toBe(2);
    const set = editor.setRaw('c\\.d', '"x"');
    expect(set.text).toContain('"c.d": "x"');
    expect(set.remove('c\\.d').text).toBe(text);
    expect(JsoncEditor.splitKeyPath('a\\.b')).toEqual(['a.b']);
  });
});
