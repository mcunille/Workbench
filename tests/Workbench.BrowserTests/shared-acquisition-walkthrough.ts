import { expect, test } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve, isAbsolute, sep } from 'node:path';
import { browserBaseUrl } from './browser-environment';
import { useAuthenticatedSession } from './auth-fixture';
import { acquisitionPanel, createOrigin, createPiece } from './shared-acquisition-fixture';
import { lifecycle } from './restoration-fixture';
import { setAppearance } from './user-menu-fixture';

test('H10 narrated shared acquisition walkthrough', async ({ browser }) => {
  test.setTimeout(300_000);
  const output = process.env.WORKBENCH_H10_MEDIA;
  if (!output || !isAbsolute(output)) throw new Error('Set WORKBENCH_H10_MEDIA to an absolute directory outside the repository.');
  const root = resolve(output);
  const repo = resolve('../..');
  if (root.toLowerCase() === repo.toLowerCase() || root.toLowerCase().startsWith(repo.toLowerCase() + sep))
    throw new Error('Walkthrough media must remain outside the repository.');
  await mkdir(root, { recursive: true });
  const setup = await browser.newContext({ baseURL: browserBaseUrl });
  const seed = await setup.newPage();
  await useAuthenticatedSession(seed);
  const pieces = [];
  for (const name of ['Blue sapphire from the fair', 'Green tourmaline from the fair', 'Violet amethyst from the fair'])
    pieces.push(await createPiece(seed, name));
  await createOrigin(seed, pieces[0].id, 'Autumn mineral fair');
  const context = await browser.newContext({ baseURL: browserBaseUrl, storageState: await setup.storageState(), viewport: { width: 1280, height: 900 }, recordVideo: { dir: root, size: { width: 1280, height: 900 } } });
  const page = await context.newPage();
  const start = Date.now();
  const segments: { start: number; end: number; text: string }[] = [];
  async function narrate(text: string, seconds = 13) {
    const begin = (Date.now() - start) / 1000;
    // This hold reserves narration time; assertions synchronize all application state.
    await page.waitForTimeout(seconds * 1000);
    segments.push({ start: begin, end: (Date.now() - start) / 1000, text });
  }
  try {
    // GIVEN individually recorded pieces, only the first has acquisition context.
    await page.goto(`/inventory/${pieces[1].id}`);
    await acquisitionPanel(page).scrollIntoViewIfNeeded();
    await expect(page.getByText('No acquisition recorded.', { exact: true })).toBeVisible();
    await narrate('Each stone has its own permanent identity, photographs and location. We can connect these separate pieces to the same acquisition without copying the source, date or notes.');
    for (const [index, piece] of pieces.slice(1).entries()) {
      if (index) await page.goto(`/inventory/${piece.id}`);
      await acquisitionPanel(page).getByRole('button', { name: 'Connect to an acquisition', exact: true }).click();
      await page.getByRole('searchbox', { name: 'Search acquisitions', exact: true }).fill('Autumn mineral fair');
      await page.getByRole('button', { name: 'Search', exact: true }).click();
      await page.getByRole('button', { name: /^Select Autumn mineral fair/ }).click();
      if (!index) {
        await narrate('Choose the saved acquisition and review the intended connection. This action changes the relationship only. It does not create a second acquisition or replace the physical item.');
        await page.route(`**/api/items/${piece.id}/acquisition-link`, async route => {
          expect((await route.fetch()).status()).toBe(200);
          await route.abort('failed');
        }, { times: 1 });
      }
      await page.getByRole('button', { name: 'Save connection', exact: true }).click();
      if (!index) {
        await expect(page.getByRole('alert')).toContainText(/confirm/i);
        await narrate('Here the connection saved, but the demonstration deliberately loses its response. Retry keeps the original versions. Review current state before deciding what to do next; no newer change is silently overwritten.');
        await page.getByRole('button', { name: 'Retry connection save', exact: true }).click();
        await expect(page.getByRole('heading', { name: 'Review current connection', exact: true })).toBeVisible();
        await page.getByRole('button', { name: 'Use saved connection', exact: true }).click();
      }
      await expect(acquisitionPanel(page).getByText('Autumn mineral fair', { exact: true })).toBeVisible();
    }
    // WHEN opening the shared view THEN all three independently recorded pieces appear.
    await acquisitionPanel(page).getByRole('link', { name: 'View acquisition', exact: true }).click();
    for (const piece of pieces) await expect(page.getByRole('link', { name: piece.name, exact: true })).toBeVisible();
    await page.screenshot({ path: `${root}/shared-acquisition-desktop.png`, fullPage: true });
    await narrate('All three stones now lead to one acquisition. Open a related piece and return through the acquisition while retaining the collection journey. Shared corrections are read from one record, including through archived relationships.');
    await page.getByRole('link', { name: 'Back to piece', exact: true }).click();
    await page.getByRole('button', { name: 'Edit acquisition', exact: true }).click();
    await page.getByLabel('Provenance notes (optional)', { exact: true }).fill('Corrected recollection: all three stones came from the same fair visit.');
    await page.getByRole('button', { name: 'Save acquisition', exact: true }).click();
    await expect(acquisitionPanel(page).getByText('Corrected recollection: all three stones came from the same fair visit.', { exact: true })).toBeVisible();
    await page.setViewportSize({ width: 320, height: 900 });
    await setAppearance(page, true);
    await acquisitionPanel(page).getByRole('button', { name: 'Remove connection', exact: true }).click();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await narrate('A mistaken connection can be removed explicitly, even on a narrow screen. The confirmation preserves both identities. Removing a connection does not delete a stone or its acquisition.');
    await page.getByRole('button', { name: 'Remove connection', exact: true }).click();
    await expect(acquisitionPanel(page).getByText('No acquisition recorded.', { exact: true })).toBeVisible();
    const current = await (await seed.request.get(`/api/items/${pieces[1].id}`)).json();
    await lifecycle(seed, pieces[1].id, 'archive', current.version);
    await page.goto(`/inventory/${pieces[1].id}`);
    await acquisitionPanel(page).scrollIntoViewIfNeeded();
    await expect(acquisitionPanel(page).getByText('Corrected recollection: all three stones came from the same fair visit.', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Change acquisition', exact: true })).toHaveCount(0);
    await page.screenshot({ path: `${root}/archived-acquisition-mobile-dark.png`, fullPage: true });
    await narrate('Archive keeps the connection and shared context readable. This archived detail cannot change the relationship. Restoring the piece keeps its acquisition. Documents, export changes and financial records are outside this delivery.');
  } finally {
    await writeFile(`${root}/segments.json`, JSON.stringify(segments, null, 2));
    const video = page.video()!;
    await context.close();
    await video.saveAs(`${root}/capture.webm`);
    await setup.close();
  }
});
