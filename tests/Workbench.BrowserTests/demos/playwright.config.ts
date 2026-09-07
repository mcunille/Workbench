import { defineConfig } from '@playwright/test';
import base from '../playwright.config';
import { fileURLToPath } from 'node:url';

// Explicit opt-in: narrated recording is not part of the ordinary test suite.
export default defineConfig({
  ...base,
  testDir: '.',
  testMatch: 'h1.demo.ts',
  timeout: 300_000,
  webServer: {
    command: `pwsh -NoProfile -File "${fileURLToPath(new URL('../../../scripts/run-browser-server.ps1', import.meta.url))}"`,
    url: 'http://127.0.0.1:4179/health/ready',
    reuseExistingServer: false,
    timeout: 180_000,
  },
});
