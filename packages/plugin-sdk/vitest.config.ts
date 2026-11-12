import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  resolve: { alias: { astra: fileURLToPath(new URL('./runtime/astra.mjs', import.meta.url)) } },
  test: {
    environment: 'node',
    include: ['test/**/*.test.ts'],
  },
});
