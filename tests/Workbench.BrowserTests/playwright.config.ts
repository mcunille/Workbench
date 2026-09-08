import { browserBaseUrl } from './browser-environment';
import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  testMatch: '*.spec.ts',
  fullyParallel: false,
  workers: 1,
  reporter: 'line',
  use: {
    baseURL: browserBaseUrl,
    trace: 'retain-on-failure',
  },
  webServer: {
    command: 'pwsh -NoProfile -File ../../scripts/run-browser-server.ps1',
    url: `${browserBaseUrl}/health/ready`,
    reuseExistingServer: false,
    timeout: 180_000,
  },
});
