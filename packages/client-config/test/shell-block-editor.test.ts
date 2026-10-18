import { describe, expect, it } from 'vitest';
import { EditorException } from '../src/editing/errors.js';
import { ShellBlockEditor } from '../src/editing/shell-block-editor.js';

describe('ShellBlockEditor', () => {
  it('Get on an absent block is null', () => {
    const editor = new ShellBlockEditor('export A=1\n');
    expect(editor.get('muse-code')).toBeNull();
    expect(editor.has('muse-code')).toBe(false);
  });

  it('Set appends the block after user lines, adding a missing newline', () => {
    const editor = new ShellBlockEditor('export A=1').set(
      'muse-code',
      "export B=2\nalias muse='x'",
    );
    expect(editor.text).toBe(
      "export A=1\n# >>> astra muse-code >>>\nexport B=2\nalias muse='x'\n# <<< astra muse-code <<<\n",
    );
    expect(editor.get('muse-code')).toBe("export B=2\nalias muse='x'");
  });

  it('Set replaces an existing block, keeping surrounding lines', () => {
    const text =
      'before\n# >>> astra muse-code >>>\nold\n# <<< astra muse-code <<<\nafter\n';
    const editor = new ShellBlockEditor(text).set('muse-code', 'new one');
    expect(editor.text).toBe(
      'before\n# >>> astra muse-code >>>\nnew one\n# <<< astra muse-code <<<\nafter\n',
    );
  });

  it('Set on an empty file writes only the block', () => {
    expect(new ShellBlockEditor('').set('muse-code', 'x').text).toBe(
      '# >>> astra muse-code >>>\nx\n# <<< astra muse-code <<<\n',
    );
  });

  it('Set keeps CRLF line endings', () => {
    const editor = new ShellBlockEditor('export A=1\r\n').set('muse-code', 'one\ntwo');
    expect(editor.text).toBe(
      'export A=1\r\n# >>> astra muse-code >>>\r\none\r\ntwo\r\n# <<< astra muse-code <<<\r\n',
    );
    expect(editor.get('muse-code')).toBe('one\ntwo');
  });

  it('Remove of an appended block restores the original text', () => {
    const original = "export A=1\nalias ll='ls -l'\n";
    const editor = new ShellBlockEditor(original).set('muse-code', 'export B=2').remove('muse-code');
    expect(editor.text).toBe(original);
    expect(editor.get('muse-code')).toBeNull();
  });

  it('Remove of an absent block is a no-op', () => {
    const text = 'export A=1\n';
    expect(new ShellBlockEditor(text).remove('muse-code').text).toBe(text);
  });

  it('blocks with different ids are independent', () => {
    const editor = new ShellBlockEditor('').set('one', '1').set('two', '2').remove('one');
    expect(editor.get('one')).toBeNull();
    expect(editor.get('two')).toBe('2');
  });

  it('an unterminated block throws', () => {
    const editor = new ShellBlockEditor('# >>> astra muse-code >>>\nexport A=1\n');
    expect(() => editor.get('muse-code')).toThrow(EditorException);
  });

  it('an invalid id throws', () => {
    expect(() => new ShellBlockEditor('').set('bad id', 'x')).toThrow(/ids must be non-empty/);
  });
});
