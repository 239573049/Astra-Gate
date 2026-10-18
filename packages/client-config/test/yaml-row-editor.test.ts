import { describe, expect, it } from 'vitest';
import { EditorException } from '../src/editing/errors';
import { YamlEditor } from '../src/editing/yaml-editor';

/** YAML documents whose root is a sequence of mappings (DeepSeek Harness's cordis.patch.yml), addressed by [id=...]. */
const Patch = `# Your patch layer
- id: agent-default-model
  name: "@deepseek-ai/dsh-agent-default-model"
  config:
    provider: deepseek-account
    model: deepseek-flash
    reasoningEffort: high
# keep me
- id: ui-chat
  name: "@deepseek-ai/dsh-client-ui-chat"
  config:
    transcriptView: detailed
`;

const row = (id: string): string => `[id=${id}]`;
type Obj = Record<string, any>;

describe('YamlEditor rows', () => {
  it('Reads_Rows_And_Nested_Values', () => {
    const editor = new YamlEditor(Patch);
    expect(editor.has(row('ui-chat'))).toBe(true);
    expect(editor.has(row('llm-pi-ai'))).toBe(false);
    expect(editor.getJsonText(row('agent-default-model') + '.config.model')).toBe(
      '"deepseek-flash"',
    );
    expect(editor.getJsonText(row('agent-default-model') + '.config.nope')).toBeNull();
    expect(editor.getJsonText(row('llm-pi-ai') + '.config')).toBeNull();
    expect((editor.get(row('ui-chat')) as Obj)['id']).toBe('ui-chat');
  });

  it('Set_Inside_A_Row_Rewrites_Only_That_Row', () => {
    const updated = new YamlEditor(Patch).setRaw(
      row('agent-default-model') + '.config.model',
      '"astra-model"',
    ).text;
    expect(updated).toContain('# Your patch layer\n');
    expect(updated).toContain(
      '# keep me\n- id: ui-chat\n  name: "@deepseek-ai/dsh-client-ui-chat"\n  config:\n    transcriptView: detailed\n',
    );
    const doc = YamlEditor.parseToJson(updated) as Obj[];
    expect(doc[0]!['config']['model']).toBe('astra-model');
    expect(doc[0]!['config']['reasoningEffort']).toBe('high');
    expect(doc.length).toBe(2);
  });

  it('Set_Creates_Nested_Containers_In_A_Row_And_Appends_New_Rows', () => {
    const withProvider = new YamlEditor(Patch)
      .setRaw(row('ui-chat') + '.config.extra.deep', '{"a":[1,2]}')
      .setRaw(row('llm-pi-ai'), '{"id":"llm-pi-ai","name":"@deepseek-ai/dsh-llm-pi-ai"}')
      .setRaw(
        row('llm-pi-ai') + '.config.providers.astra',
        '{"api":"openai-completions","models":[{"id":"m"}]}',
      );
    const doc = YamlEditor.parseToJson(withProvider.text) as Obj[];
    expect(doc.map((r) => r['id'])).toEqual(['agent-default-model', 'ui-chat', 'llm-pi-ai']);
    expect(doc[1]!['config']['extra']['deep']['a'].length).toBe(2);
    expect(doc[2]!['config']['providers']['astra']['models'][0]['id']).toBe('m');
    expect(withProvider.text).toContain('# keep me\n');
  });

  it('Set_On_A_Blank_Document_Starts_The_Sequence', () => {
    const editor = new YamlEditor('# nothing yet\n').setRaw(row('a'), '{"id":"a","name":"x"}');
    expect(editor.text.replace('- id: a', '- id: "a"')).toBe(
      '# nothing yet\n- id: "a"\n  name: "x"\n',
    );
    expect((YamlEditor.parseToJson(editor.text) as Obj[])[0]!['id']).toBe('a');
  });

  it('Remove_Takes_Out_A_Row_Or_A_Nested_Key', () => {
    const editor = new YamlEditor(Patch);
    const withoutEffort = editor.remove(row('agent-default-model') + '.config.reasoningEffort');
    expect(withoutEffort.text).not.toContain('reasoningEffort');
    expect(withoutEffort.text.replaceAll('"', '')).toContain('model: deepseek-flash');

    const withoutRow = editor.remove(row('agent-default-model'));
    expect(withoutRow.text).toBe(
      '# Your patch layer\n# keep me\n- id: ui-chat\n  name: "@deepseek-ai/dsh-client-ui-chat"\n  config:\n    transcriptView: detailed\n',
    );
    expect(editor.remove(row('nope'))).toBe(editor);
    expect(editor.remove(row('ui-chat') + '.config.nope')).toBe(editor);

    const last = new YamlEditor('- id: a\n  name: x\n').remove(row('a'));
    expect(last.text).toBe('');
  });

  it('A_Row_Whose_Items_Harness_Folded_Over_Several_Lines_Is_Replaced_Whole', () => {
    // Harness rewrites the patch file with long flow items spread over several lines.
    const folded = `- id: "llm-pi-ai"
  name: "@deepseek-ai/dsh-llm-pi-ai"
  config:
    providers:
      astra:
        api: "openai-completions"
        models:
          - {
              "id": "deepseek-flash",
              "input": [ "text", "image" ]
            }`;
    const text = Patch + folded + '\n';

    const withoutProvider = new YamlEditor(text).remove(
      row('llm-pi-ai') + '.config.providers.astra',
    );
    expect(withoutProvider.text).toBe(
      Patch +
        '- id: "llm-pi-ai"\n  name: "@deepseek-ai/dsh-llm-pi-ai"\n  config:\n    providers: {}\n',
    );
    expect(new YamlEditor(text).remove(row('llm-pi-ai')).text).toBe(Patch);

    const appended = new YamlEditor(text).setRaw(row('x'), '{"id":"x","name":"y"}').text;
    expect(appended.replace('- id: x\n', '- id: "x"\n')).toBe(text + '- id: "x"\n  name: "y"\n');
  });

  it('Selectors_Need_A_Sequence_Root_And_Block_Rows', () => {
    expect(() => new YamlEditor('a: 1\n').has(row('x'))).toThrow(EditorException);
    expect(() => new YamlEditor('- {id: a}\n').setRaw(row('a') + '.k', '1')).toThrow(
      EditorException,
    );
    expect(() => new YamlEditor(Patch).setRaw(row('missing') + '.config.x', '1')).toThrow(
      EditorException,
    );
  });
});
