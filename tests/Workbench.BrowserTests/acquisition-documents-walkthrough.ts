import { expect, test } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve, isAbsolute, sep } from 'node:path';
import { browserBaseUrl } from './browser-environment';
import { useAuthenticatedSession } from './auth-fixture';
import { createOrigin, createPiece } from './shared-acquisition-fixture';
import { receiptImage } from './acquisition-document-fixture';
import { setAppearance } from './user-menu-fixture';

test('H11 narrated paperwork walkthrough', async ({ browser }) => {
  test.setTimeout(240_000);
  const output = process.env.WORKBENCH_H11_MEDIA;
  if (!output || !isAbsolute(output)) throw new Error('Set WORKBENCH_H11_MEDIA to an absolute directory outside the repository.');
  const root = resolve(output); const repo = resolve('../..');
  if (root.toLowerCase() === repo.toLowerCase() || root.toLowerCase().startsWith(repo.toLowerCase() + sep))
    throw new Error('Walkthrough media must remain outside the repository.');
  await mkdir(root, { recursive: true });
  const setup = await browser.newContext({ baseURL: browserBaseUrl });
  const seed = await setup.newPage();
  await useAuthenticatedSession(seed);
  const piece = await createPiece(seed, 'Blue sapphire from the autumn fair');
  const origin = await createOrigin(seed, piece.id, 'Autumn mineral fair');
  const context = await browser.newContext({ baseURL: browserBaseUrl, storageState: await setup.storageState(),
    viewport: { width: 1280, height: 900 }, recordVideo: { dir: root, size: { width: 1280, height: 900 } } });
  const page = await context.newPage(); const start = Date.now();
  const segments: { start: number; end: number; text: string }[] = [];
  async function narrate(text: string, seconds = 12) {
    const begin = (Date.now() - start) / 1000;
    // Holds reserve narration time; assertions synchronize application state.
    await page.waitForTimeout(seconds * 1000);
    segments.push({ start: begin, end: (Date.now() - start) / 1000, text });
  }
  try {
    await page.goto(`/inventory/${piece.id}`);
    await page.getByRole('button', { name: 'Add document', exact: true }).click();
    await page.getByLabel('Document label', { exact: true }).fill('Fair supporting record');
    await page.getByLabel('Choose document', { exact: true }).setInputFiles(await receiptImage(page));
    await narrate('Paperwork belongs to the acquisition and is shared by its linked pieces. Give each file an understandable label. The stored report or image is supporting evidence, not a claim of verified authenticity.');
    const path = `/api/items/${piece.id}/acquisition/${origin.acquisition.id}/documents`;
    await page.route(`**${path}`, async route => {
      if (route.request().method() !== 'POST') { await route.continue(); return; }
      expect((await route.fetch()).status()).toBe(200); await route.abort('failed');
    }, { times: 1 });
    await page.getByRole('button', { name: 'Upload document', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Check and retry', exact: true })).toBeVisible();
    await narrate('This demonstration loses the response after the server saves the file. Workbench keeps the exact request. Check and retry resolves that outcome without creating a second successful attachment.');
    await page.getByRole('button', { name: 'Check and retry', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Download Fair supporting record', exact: true })).toBeVisible();
    expect((await (await page.request.get(path)).json()).documents).toHaveLength(1);
    await page.screenshot({ path: `${root}/documents-desktop.png`, fullPage: true });
    await narrate('The saved list identifies each document by label, format, size and upload date. Downloads deliver the validated original bytes. A failed download is reported separately and can be retried safely.');
    const download = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Download Fair supporting record', exact: true }).click();
    await download;
    await page.setViewportSize({ width: 320, height: 900 }); await setAppearance(page, true);
    await page.getByRole('button', { name: 'Rename Fair supporting record', exact: true }).click();
    await page.getByLabel('Document label', { exact: true }).fill('Fair identification record');
    await page.getByRole('button', { name: 'Save label', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Download Fair identification record', exact: true })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: `${root}/documents-mobile.png`, fullPage: true });
    await narrate('On a narrow screen, the same paperwork remains accessible. A checked label correction updates shared context. File content stays immutable; add a corrected copy and explicitly remove a mistaken file.');
  } finally {
    await writeFile(`${root}/segments.json`, JSON.stringify(segments, null, 2));
    const video = page.video(); await context.close();
    if (video) await video.saveAs(`${root}/capture.webm`);
    await setup.close();
  }
});
