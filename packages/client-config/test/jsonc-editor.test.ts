import { describe, expect, it } from 'vitest';
import { EditorException } from '../src/editing/errors.js';
import { JsoncEditor, jsonDeepEquals, parseJsonc } from '../src/editing/jsonc-editor.js';

const parse = (text: string) => parseJsonc(text).value;

describe('JsoncEditor', () => {
  it.each(['[]', '[ ]', '[\n]\n', '// WorkBuddy\n[]\n', '[] // nothing yet'])(
    'Set object key replaces an empty root array (%j)',
    (text) => {
      expect(new JsoncEditor(text).set('models', []).get('models')).toEqual([]);
    },
  );

  it('Set object key on an empty root array keeps the comments around it', () => {
    const result = new JsoncEditor('// WorkBuddy\n[ ] // none yet\n').set('models', []);
    expect(result.text.startsWith('// WorkBuddy\n')).toBe(true);
    expect(result.text).toContain('// none yet');
  });

  it.each([
    ['[{"id":"mine"}]', 'an array'],
    ['null', 'a value'],
    ['3', 'a value'],
  ])('Set object key refuses a root that holds data and is not an object (%s)', (text, kind) => {
    expect(() => new JsoncEditor(text).set('models', [])).toThrow(`top level is ${kind}`);
  });

  it('Get nested value with comments and trailing commas', () => {
    const text = '{\n  // comment\n  "env": {\n    "A": "1",\n  },\n}';
    const editor = new JsoncEditor(text);
    expect((editor.get('env') as { A: string }).A).toBe('1');
    expect(editor.has('env.A')).toBe(true);
    expect(editor.has('env.B')).toBe(false);
  });

  it('Set existing member replaces the value span only', () => {
    const text = '{\n  "a": 1, // keep\n  "b": 2\n}\n';
    expect(new JsoncEditor(text).set('a', 42).text).toBe('{\n  "a": 42, // keep\n  "b": 2\n}\n');
  });

  it('Set new member appends with matching indent', () => {
    const text = '{\n  "env": {\n    "A": "1"\n  }\n}';
    expect(new JsoncEditor(text).set('env.B', '2').text).toBe(
      '{\n  "env": {\n    "A": "1",\n    "B": "2"\n  }\n}',
    );
  });

  it('Set new member in an empty inline object', () => {
    const result = new JsoncEditor('{\n  "env": {}\n}\n').set('env.X', '1');
    expect(result.text).toBe('{\n  "env": { "X": "1" }\n}\n');
  });

  it('Set creates intermediate objects', () => {
    const result = new JsoncEditor('{\n  "a": 1\n}\n').set('x.y.z', true);
    expect(result.get('x.y.z')).toBe(true);
  });

  it('Set on empty text creates a document', () => {
    const result = new JsoncEditor('').set('a', 'b');
    expect(jsonDeepEquals(parse('{"a":"b"}'), parse(result.text))).toBe(true);
  });

  it('Set object value replaces the whole subtree', () => {
    const text = '{\n  "provider": {"old": true} // note\n}\n';
    const result = new JsoncEditor(text).set('provider', { npm: '@ai-sdk/openai-compatible' });
    expect(result.text).toBe('{\n  "provider": {"npm":"@ai-sdk/openai-compatible"} // note\n}\n');
  });

  it('Remove middle member keeps commas balanced', () => {
    const text = '{\n  "a": 1,\n  "b": 2,\n  "c": 3\n}';
    expect(new JsoncEditor(text).remove('b').text).toBe('{\n  "a": 1,\n  "c": 3\n}');
  });

  it('Remove last member of a strict document drops the stranded comma', () => {
    const text = '{\n  "a": 1,\n  "b": 2\n}';
    const result = new JsoncEditor(text).remove('b');
    expect(result.text).toBe('{\n  "a": 1\n}');
    expect(result.set('b', 2).text).toBe(text);
    expect(jsonDeepEquals(parse('{"a": 1}'), parse(result.text))).toBe(true);
  });

  it('Remove multiline object member removes the whole block', () => {
    const text = '{\n  "keep": 1,\n  "mine": {\n    "x": 1,\n    "y": 2\n  },\n  "after": 3\n}';
    expect(new JsoncEditor(text).remove('mine').text).toBe('{\n  "keep": 1,\n  "after": 3\n}');
  });

  it('Remove inline object comma handling', () => {
    const result = new JsoncEditor('{"a": 1, "b": {"x": 1}}\n').remove('b');
    expect(result.text).toBe('{"a": 1}\n');
  });

  it('Remove keeps a trailing-comma document valid', () => {
    const text = '{\n  "a": 1,\n  "b": 2,\n}';
    const result = new JsoncEditor(text).remove('a');
    expect(jsonDeepEquals(parse('{"b": 2}'), parse(result.text))).toBe(true);
  });

  it('Unicode values survive splicing', () => {
    const result = new JsoncEditor('{\n  "greeting": "héllo"\n}\n').set('emoji', ' ok 🚀');
    expect(result.get('emoji')).toBe(' ok 🚀');
    expect(result.get('greeting')).toBe('héllo');
    expect(result.text).toContain('\\uD83D\\uDE80');
  });

  it('SetRaw restores original formatting', () => {
    const original = '{\n  "mine": {\n    "x": 1,\n    "y": 2\n  }\n}';
    const edited = new JsoncEditor(original).set('mine', { z: 9 }).text;
    expect(edited).not.toBe(original);
    const restored = new JsoncEditor(edited).setRaw('mine', '{\n    "x": 1,\n    "y": 2\n  }').text;
    expect(restored).toBe(original);
  });

  it('Invalid JSON throws', () => {
    expect(() => new JsoncEditor('{ invalid').has('a')).toThrow(EditorException);
  });

  it('Root non-object throws', () => {
    expect(() => new JsoncEditor('[1, 2]').set('a', 1)).toThrow(EditorException);
  });

  it('CRLF keeps the newline style', () => {
    const result = new JsoncEditor('{\r\n  "a": 1\r\n}\r\n').set('b', '2');
    expect(result.text).toBe('{\r\n  "a": 1,\r\n  "b": "2"\r\n}\r\n');
  });

  it('getJsonText matches System.Text.Json compact output', () => {
    const e = new JsoncEditor(
      '{"a": {"s": "é<>&\'+`\\"\\\\/\\u0001 🚀", "n": 1.50E+2, "l": [ null, {} ]}, "z": null}',
    );
    expect(e.getJsonText('a')).toBe(
      '{"s":"\\u00E9\\u003C\\u003E\\u0026\\u0027\\u002B\\u0060\\u0022\\\\/\\u0001 \\uD83D\\uDE80","n":1.50E+2,"l":[null,{}]}',
    );
    expect(e.getJsonText('z')).toBeNull();
    expect(e.getJsonText('missing')).toBeNull();
  });
});
