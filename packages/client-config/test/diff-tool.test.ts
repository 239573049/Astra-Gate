import { describe, expect, it } from 'vitest';
import { DiffTool } from '../src/editing/diff-tool.js';

describe('DiffTool', () => {
  it('Unified adds header and hunk', () => {
    const diff = DiffTool.unified('settings.json', 'a\nb\nc\n', 'a\nB\nc\n');
    expect(diff.startsWith('--- a/settings.json\n+++ b/settings.json\n')).toBe(true);
    expect(diff).toContain('@@ -1,3 +1,3 @@');
    expect(diff).toContain('-b\n+B\n');
    expect(diff).toContain(' a\n');
  });

  it('two distant changes produce two hunks', () => {
    const lines = Array.from({ length: 20 }, (_, i) => `line${i + 1}`);
    const before = lines.join('\n') + '\n';
    lines[2] = 'CHANGED-3';
    lines[17] = 'CHANGED-18';
    const after = lines.join('\n') + '\n';
    const diff = DiffTool.unified('f', before, after);
    expect(diff.split('@@ -').length - 1).toBe(2);
    expect(diff).toContain('-line3\n+CHANGED-3\n');
    expect(diff).toContain('-line18\n+CHANGED-18\n');
  });

  it('no change gives an empty diff body', () => {
    expect(DiffTool.unified('f', 'a\n', 'a\n')).toBe('--- a/f\n+++ b/f\n');
  });

  it('addition at end', () => {
    expect(DiffTool.unified('f', 'a\n', 'a\nb\n')).toContain('+b\n');
  });

  it('exact output for a middle change with CRLF input', () => {
    expect(DiffTool.unified('f', 'a\r\nb\r\nc\r\n', 'a\r\nB\r\nc\r\n')).toBe(
      '--- a/f\n+++ b/f\n@@ -1,3 +1,3 @@\n a\n-b\n+B\n c\n',
    );
  });
});
