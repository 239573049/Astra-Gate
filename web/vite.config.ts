import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';

// Dev: the UI runs on :5173 and proxies the admin API to a local astra-server, so requests are
// same-origin (the server only allows the app://astra origin cross-origin).
const server = process.env.ASTRA_DEV_SERVER ?? 'http://127.0.0.1:17321';

export default defineConfig({
  // Absolute base: deep links like /providers/x must still load /assets/*. In the desktop app the
  // page is always app://astra/ (HashRouter), so absolute paths resolve there too.
  base: '/',
  plugins: [react(), tailwindcss()],
  // Arc components install under src/components/arc (components.json → @/components).
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': { target: server, changeOrigin: false },
    },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    sourcemap: false,
    chunkSizeWarningLimit: 1200,
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    css: false,
  },
});
