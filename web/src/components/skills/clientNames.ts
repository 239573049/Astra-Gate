/** Display names of the clients the Skills page links into (brand names, so not part of the i18n catalogs). */
const NAMES: Record<string, string> = {
  'claude-code': 'Claude Code',
  codex: 'Codex',
  'gemini-cli': 'Gemini CLI',
  opencode: 'OpenCode',
  'copilot-cli': 'Copilot CLI',
  droid: 'Droid',
  'grok-build': 'Grok Build',
};

export const clientName = (kind: string): string => NAMES[kind] ?? kind;
