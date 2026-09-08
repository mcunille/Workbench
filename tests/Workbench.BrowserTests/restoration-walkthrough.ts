import { expect, test } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { photoSignIn, cameraImage } from './photo-fixture';
import { createArchived, lifecycle, restore, confirmRestore, searchArchive } from './restoration-fixture';
test('H6 narrated archive recovery walkthrough', async ({ browser }) => {
  test.setTimeout(240_000);
  const root = '../../artifacts/h6/walkthrough'; await mkdir(root, { recursive: true });
  const setup = await browser.newContext({ baseURL: 'http://127.0.0.1:4179' });
  const seed = await setup.newPage(); await photoSignIn(seed);
  let item = await createArchived(seed, 'Blue stone from the September fair');
  item = await lifecycle(seed, item.id, 'restore', item.version);
  await seed.goto(`/inventory/${item.id}`);
  await seed.getByLabel('Choose photograph', { exact: true }).setInputFiles(await cameraImage(seed));
  await seed.getByRole('button', { name: 'Upload photograph', exact: true }).click();
  await expect(seed.getByAltText(`Photograph of ${item.name}`)).toBeVisible();
  item = await (await seed.request.get(`/api/items/${item.id}`)).json();
  item = await lifecycle(seed, item.id, 'archive', item.version);
  const context = await browser.newContext({ baseURL: 'http://127.0.0.1:4179', storageState: await setup.storageState(), viewport: { width: 1280, height: 900 }, recordVideo: { dir: root, size: { width: 1280, height: 900 } } });
  const page = await context.newPage();
  const start = Date.now(); const segments: { start: number; end: number; text: string }[] = [];
  async function narrate(text: string, seconds: number) {
    const begin = (Date.now() - start) / 1000;
    // Deliberate presentation hold for spoken narration, not a synchronization wait.
    await page.waitForTimeout(seconds * 1000);
    segments.push({ start: begin, end: (Date.now() - start) / 1000, text });
  }
  try {
    await page.goto('/inventory');
    await page.getByRole('link', { name: 'Archive', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Archive', exact: true })).toBeVisible();
    await narrate('The Archive is separate from the active collection. Records set aside here keep their saved details and photographs.', 9);
    await searchArchive(page, 'September');
    await page.getByRole('button', { name: 'List', exact: true }).click();
    await narrate('Search finds words in names, notes, and locations throughout the archive, including records beyond the first page.', 9);
    await page.getByRole('link').filter({ has: page.getByText(item.name, { exact: true }) }).click();
    await expect(page.getByText('Archived', { exact: true })).toBeVisible();
    await narrate('This is the original record and photograph. Archived details are read only. Restore to collection makes them editable again.', 10);
    await restore(page).focus(); await page.keyboard.press('Enter');
    await narrate('Restoration asks for explicit confirmation. Cancel makes no change and returns keyboard focus to the restore button.', 9);
    await page.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(restore(page)).toBeFocused();
    const bodies: unknown[] = [];
    await page.route(`**/api/items/${item.id}/restore`, async route => {
      bodies.push(route.request().postDataJSON());
      if (bodies.length === 1) { expect((await route.fetch()).status()).toBe(200); await route.abort('failed'); } else await route.continue();
    });
    await restore(page).click(); await confirmRestore(page).click();
    await expect(page.getByRole('alert')).toContainText('Restoration could not be confirmed');
    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByRole('combobox', { name: 'Appearance', exact: true }).selectOption('dark');
    await narrate('Here we deliberately lose the response after the server saves. The screen reports uncertainty and keeps the original request across appearance changes.', 11);
    await page.getByRole('button', { name: 'Retry restore', exact: true }).click();
    await expect(page.getByText('This record is already in the collection. Current saved record loaded.', { exact: true })).toBeVisible();
    expect(bodies[1]).toEqual(bodies[0]);
    await narrate('Retry uses the same checked version. The saved record is already active, so Workbench reloads its current state without claiming this retry restored it.', 12);
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.getByRole('combobox', { name: 'Appearance', exact: true }).selectOption('light');
    await page.getByRole('link', { name: 'Back to archive', exact: true }).click();
    await expect(page.getByText('No matches', { exact: true })).toBeVisible();
    await narrate('The archive search is refreshed and no longer includes the restored record. Its search words and List preference remain available.', 10);
    await page.getByRole('link', { name: 'Collection', exact: true }).click();
    await page.getByRole('searchbox').fill('September'); await page.getByRole('button', { name: 'Search', exact: true }).click();
    await page.getByRole('link').filter({ has: page.getByText(item.name, { exact: true }) }).click();
    await page.reload();
    await expect(page.getByRole('button', { name: 'Edit details', exact: true })).toBeVisible();
    await expect(page.getByAltText(`Photograph of ${item.name}`)).toBeVisible();
    const saved = await (await page.request.get(`/api/items/${item.id}`)).json();
    expect(saved.id).toBe(item.id); expect(saved.photo).toEqual(item.photo); expect(saved.archivedAtUtc).toBeNull();
    await narrate('After reload, the same identifier, notes, location, and photograph are back in the active collection. These automated checks do not establish collector usability.', 12);
  } finally {
    await writeFile(`${root}/segments.json`, JSON.stringify(segments, null, 2));
    const video = page.video()!; await context.close(); await video.saveAs(`${root}/capture.webm`); await setup.close();
  }
});
