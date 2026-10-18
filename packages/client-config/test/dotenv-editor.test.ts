import { describe, expect, it } from 'vitest';
import { DotEnvEditor } from '../src/editing/dotenv-editor.js';
import { EditorException } from '../src/editing/errors.js';

describe('DotEnvEditor', () => {
  it('Get unquotes double and single quotes', () => {
    const editor = new DotEnvEditor("A=plain\nB=\"hello world\"\nC='lit\\eral'\n# D=commented\n");
    expect(editor.get('A')).toBe('plain');
    expect(editor.get('B')).toBe('hello world');
    expect(editor.get('C')).toBe('lit\\eral');
    expect(editor.get('D')).toBeNull();
    expect(editor.has('D')).toBe(false);
  });

  it('Get supports export prefix and spaces', () => {
    expect(new DotEnvEditor('export  KEY = value with spaces\n').get('KEY')).toBe(
      'value with spaces',
    );
  });

  it('Set on an existing key preserves other lines and comments', () => {
    const result = new DotEnvEditor('# config\nA=1\n\nB="two"\n').set('A', 'renamed');
    expect(result.text).toBe('# config\nA=renamed\n\nB="two"\n');
  });

  it('Set quotes values with spaces', () => {
    const result = new DotEnvEditor('A=1\n').set('B', 'two words');
    expect(result.text).toBe('A=1\nB="two words"\n');
    expect(result.get('B')).toBe('two words');
  });

  it('Set leaves safe values unquoted', () => {
    const result = new DotEnvEditor('A=1\n').set('B', 'astra-gemini-abc-123');
    expect(result.text).toBe('A=1\nB=astra-gemini-abc-123\n');
  });

  it('Set new key without trailing newline; remove leaves a single trailing newline', () => {
    const edited = new DotEnvEditor('A=1').set('B', '2').text;
    expect(edited).toBe('A=1\nB=2\n');
    expect(new DotEnvEditor(edited).remove('B').text).toBe('A=1\n');
  });

  it('Remove middle line keeps others', () => {
    expect(new DotEnvEditor('A=1\nB=2\nC=3\n').remove('B').text).toBe('A=1\nC=3\n');
  });

  it('Remove last line works', () => {
    expect(new DotEnvEditor('A=1\nB=2\n').remove('B').text).toBe('A=1\n');
  });

  it('CRLF files keep CRLF', () => {
    expect(new DotEnvEditor('A=1\r\nB=2\r\n').set('C', '3').text).toBe('A=1\r\nB=2\r\nC=3\r\n');
    expect(new DotEnvEditor('A=1\r\nB=2\r\n').set('B', 'renamed').text).toBe(
      'A=1\r\nB=renamed\r\n',
    );
  });

  it('Unicode value is quoted and preserved', () => {
    const result = new DotEnvEditor('').set('NAME', '沪江');
    expect(result.text).toBe('NAME="沪江"\n');
    expect(result.get('NAME')).toBe('沪江');
  });

  it('SetRaw restores original quoting', () => {
    const original = 'NAME="Doe, Jane"\nOTHER=1\n';
    const edited = new DotEnvEditor(original).set('NAME', 'changed').text;
    expect(edited).not.toBe(original);
    expect(new DotEnvEditor(edited).setRaw('NAME', '"Doe, Jane"').text).toBe(original);
  });

  it('multi-line value throws', () => {
    expect(() => new DotEnvEditor('A=1\n').set('B', 'two\nlines')).toThrow(EditorException);
  });
});
