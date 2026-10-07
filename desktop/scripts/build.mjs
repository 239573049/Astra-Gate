#!/usr/bin/env node
// Compiles the Electron main and preload TypeScript entry points with esbuild.
// Output is CommonJS so the preload works with the Chromium sandbox enabled.
import { build } from 'esbuild';

const common = {
  bundle: true,
  sourcemap: false,
  platform: 'node',
  format: 'cjs',
  target: 'node20',
  external: ['electron'],
  logLevel: 'info',
  minify: false,
};

await build({
  ...common,
  entryPoints: ['src/main.ts'],
  outfile: 'dist/main.js',
});

await build({
  ...common,
  entryPoints: ['src/preload.ts'],
  outfile: 'dist/preload.js',
});
