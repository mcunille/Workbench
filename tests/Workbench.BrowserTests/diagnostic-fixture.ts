import { createApiGuard } from './browser-isolation.mjs';
import { test as base } from '@playwright/test';
import { captureLayout, diagnosticsRoot } from './safe-diagnostics.mjs';
export * from '@playwright/test';

export const test = base.extend<{ failureDiagnostics: void; interceptedApiGuard: void }>({
  interceptedApiGuard: [async ({ context, failureDiagnostics }, use, testInfo) => {
    // Dependency makes guard teardown fail before safe diagnostic teardown runs.
    void failureDiagnostics;
    if (testInfo.project.name !== 'intercepted') { await use(); return; }
    const guard = createApiGuard();
    await context.route('**/api/beta/**', route => guard.handle(route));
    try { await use(); } finally { guard.assertClean(); }
  }, { auto: true }],
  failureDiagnostics: [async ({ page }, use, testInfo) => {
    await use();
    if (testInfo.status === testInfo.expectedStatus || testInfo.status === 'skipped') return;
    try {
      const directory = await captureLayout(page, testInfo.config.metadata.diagnosticsRoot ?? diagnosticsRoot);
      if (directory) console.log(`Safe browser layout evidence: ${directory} (layout.json and layout.png)`);
      else console.log('Safe browser layout evidence omitted: diagnostic budget reached.');
    } catch {
      // Do not replace the original result or log potentially sensitive browser errors.
      console.log('Safe browser layout evidence unavailable: page closed or capture failed.');
    }
  }, { auto: true, timeout: 10_000 }],
});
