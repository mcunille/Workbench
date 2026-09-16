import { uiWorkers } from './browser-isolation.mjs';
import { browserBaseUrl } from './browser-environment';
import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  testMatch: '*.spec.ts',
  fullyParallel: false,
  workers: 1 + uiWorkers(process.env.WORKBENCH_BROWSER_UI_WORKERS),
  projects: [
    { name: 'live', testIgnore: '*.ui.spec.ts', workers: 1, fullyParallel: false },
    { name: 'intercepted', testMatch: '*.ui.spec.ts', workers: uiWorkers(process.env.WORKBENCH_BROWSER_UI_WORKERS), fullyParallel: true },
  ],
  reporter: [['./diagnostic-reporter.ts', { outputFile: '../../artifacts/browser/results.json' }], ['line']],
  globalSetup: './diagnostic-setup.ts',
  use: {
    baseURL: browserBaseUrl,
    trace: 'off',
  },
  webServer: {
    command: 'pwsh -NoProfile -File ../../scripts/run-browser-server.ps1',
    url: `${browserBaseUrl}/health/ready`,
    reuseExistingServer: false,
    timeout: 180_000,
  },
});
