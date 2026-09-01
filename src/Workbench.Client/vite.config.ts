import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5000',
      '/health': 'http://localhost:5000',
    },
  },
  test: {
    environment: 'jsdom',
    execArgv: ['--no-experimental-webstorage'],
    globals: true,
    setupFiles: './src/test/setup.ts',
  },
});
