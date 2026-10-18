import { describe, expect, it } from 'vitest';
import { EditorException } from '../src/editing/errors';
import { YamlEditor } from '../src/editing/yaml-editor';

const Hermes = `# Hermes Agent CLI Configuration
_config_version: 50

model:
  # Default model to use
  default: "anthropic/claude-opus-4.6"

  # Inference provider selection
  provider: "auto"
  base_url: "https://openrouter.ai/api/v1"

terminal:
  backend: "local"   # keep me
  timeout: 180`;

describe('YamlEditor', () => {
  it('Get_ReadsScalarsMappingsAndTypes', () => {
    const editor = new YamlEditor(Hermes);
    expect(editor.getJsonText('model.provider')).toBe('"auto"');
    expect(editor.getJsonText('terminal.timeout')).toBe('180');
    expect((editor.get('model') as Record<string, unknown>)['default']).toBe(
      'anthropic/claude-opus-4.6',
    );
    expect(editor.getJsonText('model.api_key')).toBeNull();
    expect(new YamlEditor('a:\n').getJsonText('a')).toBe('null');
  });

  it('Get_MatchesSystemTextJsonEscaping', () => {
    const editor = new YamlEditor('a: "é<>&\'+`\\u0001 😀"\nb: 1.50\nc: 1e20\nd: 0.00001\n');
    expect(editor.getJsonText('a')).toBe(
      '"\\u00E9\\u003C\\u003E\\u0026\\u0027\\u002B\\u0060\\u0001 \\uD83D\\uDE00"',
    );
    expect(editor.getJsonText('b')).toBe('1.5');
    expect(editor.getJsonText('c')).toBe('1E+20');
    expect(editor.getJsonText('d')).toBe('1E-05');
  });

  it('Set_ExistingScalar_ReplacesOnlyThatLine', () => {
    const result = new YamlEditor(Hermes).setRaw('model.provider', '"custom"');
    expect(result.text).toBe(Hermes.replace('provider: "auto"', 'provider: "custom"'));
  });

  it('Set_NewKey_AppendsAfterLastEntryOfThatMapping', () => {
    const result = new YamlEditor(Hermes).setRaw('model.api_key', '"astra-key"');
    expect(result.text).toContain(
      '  base_url: "https://openrouter.ai/api/v1"\n  api_key: "astra-key"\n\nterminal:',
    );
    expect(result.text).toContain('backend: "local"   # keep me');
    // Removing it again gives the original bytes back.
    expect(result.remove('model.api_key').text).toBe(Hermes);
  });

  it('Set_NestedObject_RendersBlockYaml_AndRemoveRestores', () => {
    const text = 'logLevel: info\ndefaultModel: minimax/MiniMax-M3\n';
    const value = {
      name: 'Astra',
      api: 'openai-completions',
      options: { baseURL: 'http://127.0.0.1:1/v1', apiKey: 'k' },
      models: { 'gpt-4.1': { name: 'GPT 4.1' }, yes: {} },
    };
    const result = new YamlEditor(text).setRaw('custom_provider.astra', JSON.stringify(value));
    expect(result.text).toBe(`logLevel: info
defaultModel: minimax/MiniMax-M3
custom_provider:
  astra:
    name: "Astra"
    api: "openai-completions"
    options:
      baseURL: "http://127.0.0.1:1/v1"
      apiKey: "k"
    models:
      gpt-4.1:
        name: "GPT 4.1"
      "yes": {}
`);
    expect(result.get('custom_provider.astra')).toEqual(value);

    // Removing the only child leaves an empty mapping (not a null) behind for the caller to prune.
    const removed = result.remove('custom_provider.astra');
    expect(removed.getJsonText('custom_provider')).toBe('{}');
    expect(removed.remove('custom_provider').text).toBe(text);
  });

  it('Set_IntoNullOrFlowMapping_RebuildsThatEntry', () => {
    const fromNull = new YamlEditor('model:\nother: 1\n').setRaw('model.provider', '"custom"');
    expect(fromNull.text).toBe('model:\n  provider: "custom"\nother: 1\n');

    const fromFlow = new YamlEditor('model: {provider: auto}\n').setRaw(
      'model.base_url',
      '"http://x/v1"',
    );
    const model = fromFlow.get('model') as Record<string, unknown>;
    expect(model['provider']).toBe('auto');
    expect(model['base_url']).toBe('http://x/v1');
  });

  it('Set_OnEmptyOrCommentOnlyText_CreatesEntries', () => {
    expect(new YamlEditor('').setRaw('model.provider', '"custom"').text).toBe(
      'model:\n  provider: "custom"\n',
    );
    expect(new YamlEditor('# hi\n').setRaw('model.provider', '"custom"').text).toBe(
      '# hi\nmodel:\n  provider: "custom"\n',
    );
  });

  it('Remove_MappingBlock_RemovesNestedLines', () => {
    const result = new YamlEditor(Hermes).remove('model');
    expect(result.text).not.toContain('base_url');
    expect(result.text).toContain('terminal:\n  backend: "local"   # keep me');
    expect(result.get('model')).toBeNull();
  });

  it('MultiLineFlowValues_AreReplacedAndRemovedWhole', () => {
    const text =
      'a:\n  list:\n    - {\n        "id": "x",\n        "n": 1\n      }\n  flow: {\n      k: 1,\n      j: [ 1,\n        2 ]\n    }\nb: 2\n';
    expect(new YamlEditor(text).setRaw('a.flow', '3').text).toBe(
      'a:\n  list:\n    - {\n        "id": "x",\n        "n": 1\n      }\n  flow: 3\nb: 2\n',
    );
    expect(new YamlEditor(text).remove('a.list').text).toBe(
      'a:\n  flow: {\n      k: 1,\n      j: [ 1,\n        2 ]\n    }\nb: 2\n',
    );
    expect(new YamlEditor(text).setRaw('a.c', '4').text).toBe(
      text.replace('b: 2\n', '') + '  c: 4\nb: 2\n',
    );
  });

  it('Quote_EscapesSpecialCharacters', () => {
    const result = new YamlEditor('').setRaw('k', '"a\\"b\\\\c\\nd: #e"');
    expect(result.get('k')).toBe('a"b\\c\nd: #e');
  });

  it('Crlf_KeepsNewlineStyle', () => {
    const result = new YamlEditor('a: 1\r\nb: 2\r\n').setRaw('c', '"x"');
    expect(result.text).toBe('a: 1\r\nb: 2\r\nc: "x"\r\n');
    expect(result.remove('b').text).toBe('a: 1\r\nc: "x"\r\n');
  });

  it('InvalidOrNonMappingYaml_Throws', () => {
    expect(() => new YamlEditor('a: [1, 2\n').get('a')).toThrow(EditorException);
    expect(() => new YamlEditor('- 1\n- 2\n').setRaw('a', '1')).toThrow(EditorException);
  });
});
