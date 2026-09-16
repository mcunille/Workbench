import type { Page, Locator } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { dirname, join } from 'node:path';

// Explicit local evidence only. Safe failure diagnostics remain independently enabled.
export async function captureEvidence(target: Page | Locator, name: string, options: { fullPage?: boolean } = {}) {
  const directory = process.env.WORKBENCH_BROWSER_EVIDENCE_DIRECTORY;
  if (!directory) return undefined;
  const path = join(directory, name);
  await mkdir(dirname(path), { recursive: true });
  await target.screenshot({ ...options, path });
  return path;
}
