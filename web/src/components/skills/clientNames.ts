/** Display names of the clients the Skills page links into (brand names, so not part of the i18n catalogs). */
const NAMES: Record<string, string> = {
  'claude-code': 'Claude Code',
  codex: 'Codex',
  'gemini-cli': 'Gemini CLI',
  opencode: 'OpenCode',
  'copilot-cli': 'Copilot CLI',
  droid: 'Droid',
  'grok-build': 'Grok Build',
  'qwen-code': 'Qwen Code',
  pi: 'Pi',
  omp: 'oh-my-pi',
  'minimax-code': 'MiniMax Code',
  'hermes-agent': 'Hermes Agent',
  'deepseek-harness': 'DeepSeek Harness',
  'command-code': 'Command Code',
  crush: 'Crush',
  zcode: 'ZCode',
  'antigravity-cli': 'Antigravity CLI',
  goose: 'Goose',
};

export const clientName = (kind: string): string => NAMES[kind] ?? kind;
