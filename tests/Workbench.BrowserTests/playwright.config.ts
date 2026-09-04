import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  testMatch: 'auth.spec.ts',
  fullyParallel: false,
  workers: 1,
  reporter: 'line',
  use: {
    baseURL: 'http://127.0.0.1:4179',
    trace: 'retain-on-failure',
  },
  webServer: {
    command: 'pwsh -NoProfile -File ../../scripts/run-browser-server.ps1',
    url: 'http://127.0.0.1:4179/health/ready',
    reuseExistingServer: false,
    timeout: 180_000,
  },
});
