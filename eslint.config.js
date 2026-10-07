/* eslint-env node */
/** @type {import('eslint').Linter.Config[]} */
const tseslint = require('typescript-eslint');

// Lean shared config. Packages (web/, desktop/, cli/) may add their own
// eslint.config.js — flat config lookup starts at the package directory
// and walks up to this file.
module.exports = tseslint.config(
  {
    ignores: [
      '**/node_modules/**',
      '**/dist/**',
      '**/coverage/**',
      'web/**',
      'desktop/**',
      'src/**',
      'tests/**',
      'scripts/**',
      'npm/**',
    ],
  },
  ...tseslint.configs.recommended,
  {
    rules: {
      '@typescript-eslint/no-unused-vars': [
        'error',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_' },
      ],
      '@typescript-eslint/no-explicit-any': 'off',
    },
  },
);
