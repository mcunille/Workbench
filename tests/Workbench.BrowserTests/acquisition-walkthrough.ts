import { expect, test } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve, isAbsolute, sep } from 'node:path';
import { browserBaseUrl } from './browser-environment';
import { useAuthenticatedSession } from './auth-fixture';
import { lifecycle } from './restoration-fixture';

test('H9 narrated acquisition walkthrough', async ({ browser }) => {
  test.setTimeout(300_000);
  const output = process.env.WORKBENCH_H9_MEDIA;
  if (!output || !isAbsolute(output)) throw new Error('Set WORKBENCH_H9_MEDIA to an absolute directory outside the repository.');
  const root = resolve(output);
  const repo = resolve('../..');
  if (root.toLowerCase() === repo.toLowerCase() || root.toLowerCase().startsWith(repo.toLowerCase() + sep))
    throw new Error('Walkthrough media must remain outside the repository.');
  await mkdir(root, { recursive: true });
  const setup = await browser.newContext({ baseURL: browserBaseUrl });
  const seed = await setup.newPage(); await useAuthenticatedSession(seed);
  const csrf = await (await seed.request.get('/api/auth/antiforgery')).json();
  const created = await seed.request.post('/api/items', {
    headers: { 'X-CSRF-TOKEN': csrf.requestToken },
    data: { creationRequestId: crypto.randomUUID(), name: 'Blue stone from a family collection', notes: 'Synthetic walkthrough record', location: 'Tray A' },
  });
  expect(created.status()).toBe(201); const item = await created.json();
  const context = await browser.newContext({ baseURL: browserBaseUrl, storageState: await setup.storageState(), viewport: { width: 1280, height: 900 }, recordVideo: { dir: root, size: { width: 1280, height: 900 } } });
  const page = await context.newPage();
  const start = Date.now(); const segments: { start: number; end: number; text: string }[] = [];
  async function narrate(text: string, seconds: number) {
    const begin = (Date.now() - start) / 1000;
    // Presentation hold for narration, never application synchronization.
    await page.waitForTimeout(seconds * 1000);
    segments.push({ start: begin, end: (Date.now() - start) / 1000, text });
  }
  try {
    // GIVEN a persisted item with no required acquisition context.
    await page.goto(`/inventory/${item.id}`);
    await expect(page.getByText('No acquisition recorded.', { exact: true })).toBeVisible();
    await page.getByRole('heading', { name: 'Acquisition', exact: true }).scrollIntoViewIfNeeded();
    await narrate('The piece already has a saved identity. Acquisition is optional context: record when and how you got this piece. These are your recollections, not independently verified provenance.', 14);
    await page.getByRole('button', { name: 'Add acquisition', exact: true }).click();
    await page.getByLabel('Acquisition method', { exact: true }).selectOption('Inheritance');
    await page.getByLabel('Inherited from (optional)', { exact: true }).fill('Family collection');
    await page.getByLabel('Date precision', { exact: true }).selectOption('Year');
    await page.getByLabel('Year', { exact: true }).fill('2018');
    await page.getByLabel('Provenance notes (optional)', { exact: true }).fill('Remembered as part of the family collection; exact date unknown.');
    await narrate('Choose a method explicitly, including Unknown when needed. Inheritance does not require a seller or price. Only the year is known here, so no month or day is invented.', 14);
    // WHEN creation commits but transport drops its response THEN the retry uses the original request.
    await page.route(`**/api/items/${item.id}/acquisition`, async route => {
      if (route.request().method() !== 'POST') return route.continue();
      expect((await route.fetch()).status()).toBe(201); await route.abort('failed');
    }, { times: 1 });
    await page.getByRole('button', { name: 'Save acquisition', exact: true }).click();
    await expect(page.getByRole('alert')).toContainText('could not confirm');
    await narrate('This demonstration deliberately loses the success response after saving. The submitted input stays unchanged. Retry sends that same request, resolving its outcome without creating another acquisition.', 14);
    await page.getByRole('button', { name: 'Retry save', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Edit acquisition', exact: true })).toBeFocused();
    await page.reload();
    await expect(page.getByText('2018', { exact: true })).toBeVisible();
    await page.getByRole('heading', { name: 'Acquisition', exact: true }).scrollIntoViewIfNeeded();
    await page.getByRole('region', { name: 'Acquisition', exact: true }).screenshot({ path: root + '/saved-acquisition.png' });
    await narrate('Reload retrieves the persisted acquisition and its year-only date. Corrections retain the same item identity. Version checks require deliberate review when another save has changed the record.', 14);
    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByRole('switch', { name: 'Dark theme' }).setChecked(true);
    await page.getByRole('button', { name: 'Edit acquisition', exact: true }).click();
    await page.getByLabel('Acquisition method', { exact: true }).selectOption('Gift');
    await expect(page.getByLabel('Gift from (optional)', { exact: true })).toHaveValue('Family collection');
    await page.getByLabel('Provenance notes (optional)', { exact: true }).fill('Corrected recollection: a family gift in 2018.');
    await narrate('On a narrow dark appearance, changing the method preserves the source text. This correction records a gift. Optional facts may remain unknown; the note explains what the collector remembers.', 14);
    await page.getByRole('button', { name: 'Save acquisition', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Edit acquisition', exact: true })).toBeVisible();
    const current = await (await seed.request.get(`/api/items/${item.id}`)).json();
    await lifecycle(seed, item.id, 'archive', current.version);
    await page.reload();
    await page.getByRole('heading', { name: 'Acquisition', exact: true }).scrollIntoViewIfNeeded();
    await expect(page.getByText('Corrected recollection: a family gift in 2018.', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Edit acquisition', exact: true })).toHaveCount(0);
    await narrate('Archive retains readable acquisition context and removes editing actions. This delivery covers remembering an acquisition. Shared-link correction, documents, and acquisition exports are separate future work.', 14);
    await page.screenshot({ path: `${root}/archived-mobile-dark.png`, fullPage: true });
  } finally {
    await writeFile(`${root}/segments.json`, JSON.stringify(segments, null, 2));
    const video = page.video()!; await context.close(); await video.saveAs(`${root}/capture.webm`);
    await setup.close();
  }
});
