import { describe, expect, it } from 'vitest';
import { EditorException } from '../src/editing/errors';
import { TomlEditor, tomlString } from '../src/editing/toml-editor';

describe('TomlEditor', () => {
  it('Set_ExistingTopLevelKey_ReplacesValueOnly', () => {
    const r = new TomlEditor('a = 1 # keep\nb = "x"\n').set('a', '42');
    expect(r.text).toBe('a = 42 # keep\nb = "x"\n');
  });

  it('Set_NewTopLevelKey_InsertsAfterLastTopLevelKey', () => {
    const r = new TomlEditor('# header comment\n\nalpha = 1\n\n[t]\nx = 1\n').setString('beta', 'b');
    expect(r.text).toBe('# header comment\n\nalpha = 1\nbeta = "b"\n\n[t]\nx = 1\n');
  });

  it('Set_NewTopLevelKey_NoTables_AppendsAtEnd', () => {
    expect(new TomlEditor('a = 1').setString('b', 'x').text).toBe('a = 1\nb = "x"\n');
  });

  it('Set_IntoExistingTable_AppendsInsideTable', () => {
    const r = new TomlEditor('[t]\nx = 1 # keep\n').setString('t.y', '2');
    expect(r.text).toBe('[t]\nx = 1 # keep\ny = "2"\n');
  });

  it('Set_MissingTable_CreatesTableAtEndOfFile', () => {
    const r = new TomlEditor('[a]\nx = 1\n').set('b.c.key', '"v"');
    expect(r.text).toBe('[a]\nx = 1\n\n[b.c]\nkey = "v"\n');
  });

  it('Set_NoTrailingNewline_NewKeyRestoresExactly', () => {
    const withKey = new TomlEditor('a = 1').set('b', '2').text;
    expect(withKey).toBe('a = 1\nb = 2\n');
    expect(new TomlEditor(withKey).remove('b').text).toBe('a = 1\n');
  });

  it('CrlfFiles_StayCrlf', () => {
    const r = new TomlEditor('a = 1\r\n\r\n[t]\r\nx = 1\r\n').set('b', '2');
    expect(r.text).toBe('a = 1\r\nb = 2\r\n\r\n[t]\r\nx = 1\r\n');
    expect(r.text).not.toContain('\n[b');
    const table = r.insertTable('model.astra', [['k', '"v"']]);
    expect(table.text).toBe(
      'a = 1\r\nb = 2\r\n\r\n[t]\r\nx = 1\r\n\r\n[model.astra]\r\nk = "v"\r\n',
    );
  });

  it('RemoveTable_RemovesHeaderAndItems_KeepsNeighbors', () => {
    const text = 'top = 1\n\n[mine]\nk = 1\n\n[other]\nk = 2\n';
    expect(new TomlEditor(text).removeTable('mine').text).toBe('top = 1\n\n[other]\nk = 2\n');
  });

  it('RemoveTable_LastTable_Works', () => {
    const text = '[a]\nx = 1\n\n[mine]\nk = 1\n';
    expect(new TomlEditor(text).removeTable('mine').text).toBe('[a]\nx = 1\n');
  });

  it('Remove_KeepsPrecedingCommentLines', () => {
    const text = '# my choice\nmodel = "x"\nother = 1\n';
    expect(new TomlEditor(text).remove('model').text).toBe('# my choice\nother = 1\n');
  });

  it('GetValue_StringBoolAndMissing', () => {
    const e = new TomlEditor('s = "a\\tb"\nb = true\nn = 4141\n');
    expect(e.getStringValue('s')).toBe('a\tb');
    expect(e.getBoolValue('b')).toBe(true);
    expect(e.getValue('n')).toBe('4141');
    expect(e.getValue('missing')).toBeNull();
    expect(e.keyExists('missing')).toBe(false);
  });

  it('TableExists_FindsDottedHeaders', () => {
    const e = new TomlEditor('[model_providers.astra]\nname = "x"\n');
    expect(e.tableExists('model_providers.astra')).toBe(true);
    expect(e.tableExists('model_providers.other')).toBe(false);
    expect(e.keyExists('model_providers.astra.name')).toBe(true);
  });

  it('Set_UnicodeString_EscapesAndRoundTrips', () => {
    const r = new TomlEditor('').setString('k', 'héllo "wörld"\n');
    expect(r.getStringValue('k')).toBe('héllo "wörld"\n');
  });

  it('InsertTable_OnFileWithoutTrailingNewline', () => {
    const r = new TomlEditor('a = 1').insertTable('model_providers.astra', [
      ['name', '"Astra"'],
      ['requires_openai_auth', 'false'],
    ]);
    expect(r.text).toBe(
      'a = 1\n\n[model_providers.astra]\nname = "Astra"\nrequires_openai_auth = false\n',
    );
  });

  it('InsertTable_ExistingTable_Throws', () => {
    expect(() => new TomlEditor('[t]\nx = 1\n').insertTable('t', [['y', '1']])).toThrow(
      EditorException,
    );
  });

  it('Set_SelfCheckDetectsBrokenDocument_AndThrows', () => {
    expect(() => new TomlEditor('a = 1\n').set('b', '[not a value]\nother = ')).toThrow();
  });

  it('InvalidDocument_ThrowsOnAnyOperation', () => {
    expect(() => new TomlEditor('not = [valid').getValue('x')).toThrow(EditorException);
  });

  it('CommentBetweenItems_StaysAttachedAfterRemoval', () => {
    const text = 'a = 1\n# documents table\ntarget = 2\n\n[t]\nx = 1\n';
    expect(new TomlEditor(text).remove('target').text).toBe(
      'a = 1\n# documents table\n\n[t]\nx = 1\n',
    );
  });
});

describe('TomlEditor edge cases', () => {
  it('is immutable', () => {
    const e = new TomlEditor('a = 1\n');
    e.set('a', '2');
    expect(e.text).toBe('a = 1\n');
  });

  it('remove drops a trailing comment with the line', () => {
    expect(new TomlEditor('a = 1 # c\nb = 2\n').remove('a').text).toBe('b = 2\n');
  });

  it('remove is a no-op for a missing key and returns the same instance', () => {
    const e = new TomlEditor('a = 1\n');
    expect(e.remove('zz')).toBe(e);
    expect(e.removeTable('zz')).toBe(e);
  });

  it('remove handles CRLF and a last line without newline', () => {
    expect(new TomlEditor('a = 1\r\nb = 2\r\nc = 3\r\n').remove('b').text).toBe(
      'a = 1\r\nc = 3\r\n',
    );
    expect(new TomlEditor('a = 1\nb = 2').remove('b').text).toBe('a = 1\n');
  });

  it('finds dotted keys and quoted keys', () => {
    const e = new TomlEditor('a.b = 1\n"q k" = 2\n[t]\nx.y = "z"\n');
    expect(e.getValue('a.b')).toBe('1');
    expect(e.getValue('t.x.y')).toBe('"z"');
    expect(e.getStringValue('t.x.y')).toBe('z');
    expect(e.getStringValue('a.b')).toBeNull();
    expect(e.set('a.b', '5').text).toBe('a.b = 5\n"q k" = 2\n[t]\nx.y = "z"\n');
  });

  it('does not look inside inline tables or arrays of tables as keys', () => {
    const e = new TomlEditor('i = { a = 1 }\n[[arr]]\nz = true\n');
    expect(e.keyExists('i.a')).toBe(false);
    expect(e.tableExists('arr')).toBe(true);
    expect(e.getBoolValue('arr.z')).toBe(true);
  });

  it('replaces multi-line values and keeps what follows', () => {
    const e = new TomlEditor('a = [\n  1,\n  2,\n] # c\nb = 1\n');
    expect(e.set('a', '[3]').text).toBe('a = [3] # c\nb = 1\n');
    expect(e.remove('a').text).toBe('b = 1\n');
  });

  it('inserts into an empty table directly after the header', () => {
    expect(new TomlEditor('[t] # h\n[u]\nx = 1\n').set('t.k', '1').text).toBe(
      '[t] # h\nk = 1\n[u]\nx = 1\n',
    );
    expect(new TomlEditor('[t]').set('t.k', '1').text).toBe('[t]\nk = 1\n');
  });

  it('inserts a top-level key before the first table when there are no top-level pairs', () => {
    expect(new TomlEditor('# c\n[t]\nx = 1\n').set('k', '1').text).toBe('# c\nk = 1\n[t]\nx = 1\n');
  });

  it('inserts after the last pair before trailing comments', () => {
    expect(new TomlEditor('a = 1\n# note\n[t]\n').set('b', '2').text).toBe(
      'a = 1\nb = 2\n# note\n[t]\n',
    );
  });

  it('creates a table at EOF without doubling blank lines', () => {
    expect(new TomlEditor('a = 1\n\n').set('t.k', '1').text).toBe('a = 1\n\n[t]\nk = 1\n');
    expect(new TomlEditor('').set('t.k', '1').text).toBe('[t]\nk = 1\n');
  });

  it('removeTable removes a table at EOF without a trailing newline', () => {
    expect(new TomlEditor('[a]\nx = 1\n\n[m]\nk = 1').removeTable('m').text).toBe('[a]\nx = 1\n');
  });

  it('removeTable works with CRLF', () => {
    expect(
      new TomlEditor('top = 1\r\n\r\n[m]\r\nk = 1\r\n\r\n[o]\r\nk = 2\r\n').removeTable('m').text,
    ).toBe('top = 1\r\n\r\n[o]\r\nk = 2\r\n');
  });

  it('removeTable keeps subtables (separate headers)', () => {
    expect(new TomlEditor('[a]\nx = 1\n[a.b]\ny = 2\n').removeTable('a').text).toBe(
      '[a.b]\ny = 2\n',
    );
  });

  it('rejects quoted segments and empty values', () => {
    expect(() => new TomlEditor('').set('"a b"', '1')).toThrow(EditorException);
    expect(() => new TomlEditor('').set('a', '  ')).toThrow();
    expect(() => new TomlEditor('').set('', '1')).toThrow();
  });

  it('throws on duplicate keys produced by a set', () => {
    expect(() => new TomlEditor('a = 1\n').set('b', '1\na = 2')).toThrow(EditorException);
  });

  it('tomlString escapes control characters', () => {
    expect(tomlString('a"b\\c\u0001\t')).toBe('"a\\"b\\\\c\\u0001\\t"');
  });
});
