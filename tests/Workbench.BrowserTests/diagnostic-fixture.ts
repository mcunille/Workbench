import { test as base } from '@playwright/test';
import { createHash } from 'node:crypto';
import { captureLayout, diagnosticsRoot } from './safe-diagnostics.mjs';
export * from '@playwright/test';

export const test = base.extend<{ failureDiagnostics: void }>({
  failureDiagnostics: [async ({ page }, use, testInfo) => {
    await use();
    if (testInfo.status === testInfo.expectedStatus || testInfo.status === 'skipped') return;
    const id = 'failure-' + createHash('sha256').update(testInfo.testId + ':' + testInfo.retry).digest('hex').slice(0, 16);
    try {
      const directory = await captureLayout(page, testInfo.config.metadata.diagnosticsRoot ?? diagnosticsRoot, id);
      if (directory) console.log(`Safe browser layout evidence: ${directory} (layout.json and layout.png)`);
      else console.log('Safe browser layout evidence omitted: diagnostic budget reached.');
    } catch {
      // Do not replace the original result or log potentially sensitive browser errors.
      console.log('Safe browser layout evidence unavailable: page closed or capture failed.');
    }
  }, { auto: true, timeout: 10_000 }],
});
