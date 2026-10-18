import { describe, expect, it } from 'vitest';
import {
  COMMENT,
  TRAILING_COMMA,
  findProblem,
  isStrict,
  repair,
} from '../src/editing/strict-json.js';

/** Strict-JSON repair for clients that read their config with JSON.parse (Claude Desktop). */
describe('StrictJson', () => {
  it('Trailing comma is found and only the comma is removed', () => {
    const text = '{\n  "preferences": {\n    "sidebarMode": "chat"\n  },\n}\n';
    expect(findProblem(text)).toEqual({ problem: TRAILING_COMMA, line: 4 });
    expect(repair(text)).toBe('{\n  "preferences": {\n    "sidebarMode": "chat"\n  }\n}\n');
  });

  it('Trailing commas in arrays and behind comments are removed', () => {
    const text = '{"a": [1, 2,], "b": {"c": true, /* gone */ }, "d":/*x*/ 1,}';
    const repaired = repair(text);
    expect(repaired).toBe('{"a": [1, 2], "b": {"c": true }, "d": 1}');
    expect(isStrict(repaired!)).toBe(true);
  });

  it('Comments go with their line when alone and with their blanks at a line end', () => {
    const text =
      '{\r\n  // the key\r\n  "a": 1, // first\r\n  /* block\r\n     comment */\r\n  "b": 2\r\n}';
    expect(findProblem(text)).toEqual({ problem: COMMENT, line: 2 });
    expect(repair(text)).toBe('{\r\n  "a": 1,\r\n  "b": 2\r\n}');
  });

  it('Comment and comma lookalikes inside strings are left alone', () => {
    const text = '{"url": "http://x/*y*/", "s": ",}\\"//", "n": 1,}';
    expect(repair(text)).toBe('{"url": "http://x/*y*/", "s": ",}\\"//", "n": 1}');
  });

  it.each([
    ['{"a": 1}'], // strict already
    ['{"a": 1,,}'], // not JSONC either: nothing safe to do
    ['{"a": /* open'], // unterminated comment
    ['{"a": 1, "a": 2,}'], // duplicate members
    [''],
  ])('Nothing to repair: %j', (text) => {
    expect(findProblem(text)).toBeNull();
    expect(repair(text)).toBeNull();
  });
});
