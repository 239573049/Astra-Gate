import { describe, expect, it } from 'vitest';
import { ConfigValueCodec as C } from '../src/config-value-codec.js';

describe('ConfigValueCodec', () => {
  it('passes JSON/YAML texts through and quotes TOML/env', () => {
    expect(C.encode('json', '{"a":1}')).toBe('{"a":1}');
    expect(C.encode('yaml', '1')).toBe('1');
    expect(C.encode('toml', 'x')).toBe('"x"');
    expect(C.encode('env', null)).toBeNull();
    expect(C.decodeToSource('json', '{"a":1}')).toBe('{"a":1}');
    expect(C.decodeToSource('toml', '"x"')).toBe('x');
    expect(C.decodeToSource('env', null)).toBeNull();
  });

  it('escapes like System.Text.Json defaults', () => {
    const s = 'a"b\\c<>&\'+`\u007f\u0001\n\t\b\f\r/ ~ é沪😀 :=';
    expect(C.encode('toml', s)).toBe(
      '"a\\u0022b\\\\c\\u003C\\u003E\\u0026\\u0027\\u002B\\u0060\\u007F\\u0001\\n\\t\\b\\f\\r/ ~ \\u00E9\\u6CAA\\uD83D\\uDE00 :="',
    );
    expect(C.decodeToSource('env', C.encode('env', s))).toBe(s);
  });

  it('decode rejects non-string JSON', () => {
    expect(() => C.decodeToSource('toml', '1')).toThrow();
    expect(() => C.decodeToSource('toml', '{')).toThrow();
  });

  it('equalsValue is structural for JSON and YAML, tolerant of comments and trailing commas', () => {
    expect(C.equalsValue('json', '{"b": 2, // c\n "a": [1,2,],}', '{"a":[1,2],"b":2}')).toBe(true);
    expect(C.equalsValue('yaml', '{"a":1}', '{"a":2}')).toBe(false);
    expect(C.equalsValue('json', '{oops', '{"a":1}')).toBe(false);
    expect(C.equalsValue('json', 'null', 'null')).toBe(false);
    expect(C.equalsValue('json', '"x"', '"x"')).toBe(true);
  });

  it('equalsValue handles nulls and TOML/env by decoded source', () => {
    expect(C.equalsValue('json', null, null)).toBe(true);
    expect(C.equalsValue('json', '1', null)).toBe(false);
    expect(C.equalsValue('toml', null, '"x"')).toBe(false);
    expect(C.equalsValue('toml', 'x y', '"x y"')).toBe(true);
    expect(C.equalsValue('env', 'x', '"y"')).toBe(false);
  });
});
