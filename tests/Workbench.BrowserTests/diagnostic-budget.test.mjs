import test from 'node:test';
import assert from 'node:assert/strict';
import { chromium } from '@playwright/test';
import { mkdtemp, mkdir, readdir, readFile, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { captureLayout } from './safe-diagnostics.mjs';

test('concurrent real captures reserve the last diagnostic slot atomically', { timeout: 30_000 }, async () => {
  // GIVEN nine retained slots and two real browser pages failing at the same time.
  const root = await mkdtemp(join(tmpdir(), 'diagnostic-budget-'));
  const browser = await chromium.launch();
  try {
    for (let index = 0; index < 9; index++) await mkdir(join(root, `failure-${index}`));
    const pages = await Promise.all([browser.newPage(), browser.newPage()]);
    for (const page of pages) {
      await page.setContent('<main style="width:160px;height:80px">Synthetic layout</main>');
    }
    // WHEN both request the last remaining slot concurrently.
    const captures = pages.map(page => captureLayout(page, root));
    const results = await Promise.all(captures);
    // THEN exactly one snapshot is retained, and the cap holds after both complete.
    assert.equal(results.filter(Boolean).length, 1);
    assert.equal((await readdir(root)).length, 10);
    const retained = results.find(Boolean);
    assert.deepEqual((await readdir(retained)).sort(), ['layout.json', 'layout.png']);
    assert.ok((await readFile(join(retained, 'layout.png'))).length > 100);
  } finally {
    await browser.close();
    await rm(root, { recursive: true, force: true });
  }
});

test('failed capture releases its reservation and the returned path locates complete evidence', async () => {
  // GIVEN a closed page and a new diagnostic budget.
  const root = await mkdtemp(join(tmpdir(), 'diagnostic-budget-release-'));
  const browser = await chromium.launch();
  try {
    const closed = await browser.newPage();
    await closed.close();
    // WHEN capture cannot read its source THEN no partial output or reservation remains.
    await assert.rejects(captureLayout(closed, root));
    assert.deepEqual(await readdir(root), []);
    // WHEN another failure arrives THEN the released slot is reused and its returned path is usable.
    const page = await browser.newPage();
    await page.setContent('<main>Retained layout</main>');
    const retained = await captureLayout(page, root);
    assert.equal(retained, join(root, 'failure-0'));
    assert.deepEqual((await readdir(retained)).sort(), ['layout.json', 'layout.png']);
  } finally {
    await browser.close();
    await rm(root, { recursive: true, force: true });
  }
});
