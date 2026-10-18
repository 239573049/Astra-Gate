import { describe, expect, it } from 'vitest';
import { EditorException } from '../src/editing/errors.js';
import { JsoncEditor } from '../src/editing/jsonc-editor.js';

type Obj = Record<string, any>;

/** Array-element selectors ("providers.[name=astra]") used for Copilot CLI's providers.json. */
describe('JsoncEditor array selectors', () => {
  it('splitKeyPath keeps dots inside selectors', () => {
    expect(JsoncEditor.splitKeyPath('models.[provider=astra&id=gpt-4.1]')).toEqual([
      'models',
      '[provider=astra&id=gpt-4.1]',
    ]);
    expect(JsoncEditor.splitKeyPath('a.b')).toEqual(['a', 'b']);
  });

  it('selector rejects reserved characters', () => {
    expect(JsoncEditor.selector(['name', 'astra'])).toBe('[name=astra]');
    expect(() => JsoncEditor.selector(['id', 'a&b'])).toThrow(EditorException);
  });

  it('Set appends an element, then replaces it, and remove restores the bytes', () => {
    const text =
      '{\n  "providers": [\n    {"name": "mine", "baseUrl": "http://localhost:11434/v1"}\n  ]\n}';
    const path = 'providers.' + JsoncEditor.selector(['name', 'astra']);
    const added = new JsoncEditor(text).setRaw(
      path,
      '{"name":"astra","baseUrl":"http://127.0.0.1:1/v1"}',
    );
    expect((added.get(path) as Obj).baseUrl).toBe('http://127.0.0.1:1/v1');
    expect((added.get('providers.[name=mine]') as Obj).baseUrl).toBe('http://localhost:11434/v1');

    const replaced = added.setRaw(path, '{"name":"astra","baseUrl":"http://127.0.0.1:2/v1"}');
    expect((JSON.parse(replaced.text) as Obj).providers).toHaveLength(2);
    expect((replaced.get(path) as Obj).baseUrl).toBe('http://127.0.0.1:2/v1');

    // Strict JSON stays strict: no stranded trailing comma.
    expect(replaced.remove(path).text).toBe(text);
  });

  it('Set creates a missing array', () => {
    const result = new JsoncEditor('').setRaw(
      'models.[provider=astra&id=m.1]',
      '{"id":"m.1","provider":"astra"}',
    );
    const node = JSON.parse(result.text) as Obj;
    expect(node.models[0].id).toBe('m.1');
    expect(result.has('models.[provider=astra&id=m.1]')).toBe(true);
    expect(result.has('models.[provider=other&id=m.1]')).toBe(false);
  });

  it('Set with a value not matching the selector fails the self-check', () => {
    expect(() => new JsoncEditor('{"p": []}').setRaw('p.[name=astra]', '{"name":"other"}')).toThrow(
      EditorException,
    );
  });
});
