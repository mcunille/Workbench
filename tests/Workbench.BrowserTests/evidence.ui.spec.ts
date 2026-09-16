import { expect, test } from './diagnostic-fixture';
import { captureEvidence } from './evidence-fixture';
import { mkdtemp, readFile, readdir, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';

test('optional evidence writes a real PNG only when explicitly enabled', async ({ page }) => {
  // GIVEN a synthetic page and an empty private test directory without evidence enabled.
  const directory = await mkdtemp(join(tmpdir(), 'workbench-evidence-contract-'));
  const previous = process.env.WORKBENCH_BROWSER_EVIDENCE_DIRECTORY;
  try {
    delete process.env.WORKBENCH_BROWSER_EVIDENCE_DIRECTORY;
    await page.setContent('<main style="width:80px;height:40px;background:navy">Sample</main>');
    // WHEN capture is requested by an ordinary test THEN no path or artifact is produced.
    expect(await captureEvidence(page, 'sample.png')).toBeUndefined();
    expect(await readdir(directory)).toEqual([]);
    // WHEN explicitly enabled THEN real browser capture creates a PNG in the requested directory.
    process.env.WORKBENCH_BROWSER_EVIDENCE_DIRECTORY = directory;
    const path = await captureEvidence(page.locator('main'), 'sample.png');
    expect(path).toBe(join(directory, 'sample.png'));
    expect([...(await readFile(path!)).subarray(0, 8)]).toEqual([137, 80, 78, 71, 13, 10, 26, 10]);
  } finally {
    if (previous === undefined) delete process.env.WORKBENCH_BROWSER_EVIDENCE_DIRECTORY;
    else process.env.WORKBENCH_BROWSER_EVIDENCE_DIRECTORY = previous;
    await rm(directory, { recursive: true, force: true });
  }
});
