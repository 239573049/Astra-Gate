import { describe, expect, it } from 'vitest';
import { npmChildEnv } from '../src/npm-runner.js';

describe('npmChildEnv', () => {
  it('drops the keys a parent npx exports that break nested installs', () => {
    const env = npmChildEnv({
      PATH: '/usr/bin',
      npm_config_allow_scripts: '@anthropic-ai/claude-code',
      'npm_config_allow-scripts': 'x',
      NPM_CONFIG_ALLOW_SCRIPTS: 'x',
      npm_config_call: 'astra install --desktop',
      npm_config_package: '@aidotnet/astra-gate',
      npm_config_registry: 'https://registry.npmmirror.com/',
      npm_config_cache: '/Users/demo/.npm',
    });
    expect(env).toEqual({
      PATH: '/usr/bin',
      npm_config_registry: 'https://registry.npmmirror.com/',
      npm_config_cache: '/Users/demo/.npm',
    });
  });
});
