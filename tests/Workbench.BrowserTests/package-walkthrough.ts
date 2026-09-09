import { browserBaseUrl } from './browser-environment';
import { expect, test } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { useAuthenticatedSession } from './auth-fixture';
import { lifecycle } from './restoration-fixture';
import { archiveExportItems, createExportItem } from './export-fixture';
import { downloadPackage, uploadPackagePhoto } from './package-fixture';

test('H8 narrated collection package walkthrough', async ({ browser }) => {
  test.setTimeout(300_000);
  const root = '../../artifacts/h8/walkthrough'; await mkdir(root, { recursive: true });
  const setup = await browser.newContext({ baseURL: browserBaseUrl });
  const seed = await setup.newPage(); await useAuthenticatedSession(seed);
  const active = await createExportItem(seed, 'Blue stone from the September fair', 'Natural blue colour\nPurchased at the fair', 'Tray A');
  const stored = await uploadPackagePhoto(seed, active.id);
  const plain = await createExportItem(seed, 'Quartz awaiting a photograph', 'Text record without a photograph', 'Tray B');
  const archived = await createExportItem(seed, 'Archived quartz specimen', 'Original record retained', 'Tray C');
  await lifecycle(seed, archived.id, 'archive', archived.version);
  const context = await browser.newContext({ baseURL: browserBaseUrl, storageState: await setup.storageState(), viewport: { width: 1280, height: 900 }, recordVideo: { dir: root, size: { width: 1280, height: 900 } } });
  const page = await context.newPage();
  const start = Date.now(); const segments: { start: number; end: number; text: string }[] = [];
  async function narrate(text: string, seconds: number) {
    const begin = (Date.now() - start) / 1000;
    // Deliberate presentation hold for spoken narration, not a synchronization wait.
    await page.waitForTimeout(seconds * 1000);
    segments.push({ start: begin, end: (Date.now() - start) / 1000, text });
  }
  try {
    // GIVEN saved photo, no-photo and archived records.
    await page.goto(`/inventory/${active.id}`);
    await expect(page.getByAltText(`Photograph of ${active.name}`)).toBeVisible();
    await narrate('This saved stone has a stored detail photograph. A collection package keeps current text records and their photographs together in an ordinary ZIP.', 11);
    await page.getByRole('link', { name: 'Back to collection', exact: true }).click();
    await page.getByRole('link', { name: 'Export records', exact: true }).click();
    await narrate('CSV remains the default for text records. Select Records and photographs to include stored WebP images, a manifest, and offline instructions. Camera originals and history are excluded.', 13);
    await page.getByRole('radio', { name: 'Records and photographs (ZIP)', exact: true }).focus();
    await page.keyboard.press('Space');
    await expect(page.getByRole('button', { name: 'Prepare export', exact: true })).toBeDisabled();
    await page.getByRole('radio', { name: 'Active records', exact: true }).check();
    await narrate('Choose the record scope explicitly. Search and loaded pages do not restrict it. The package is limited to ten thousand records and one hundred twenty eight mebibytes, with a two minute preparation deadline.', 15);
    // WHEN preparation succeeds THEN independent ZIP parsing proves portable text/photo mapping.
    await page.getByRole('button', { name: 'Prepare export', exact: true }).click();
    await expect(page.getByRole('link', { name: 'Download ZIP', exact: true })).toBeVisible();
    const activePackage = await downloadPackage(page, `${root}/active-package.zip`);
    expect(activePackage.entries.get(`photos/${active.id}.webp`)).toEqual(stored.bytes);
    expect(activePackage.manifest.items.find(item => item.item_id === plain.id)?.photo).toEqual({ status: 'none' });
    expect(activePackage.manifest.items.find(item => item.item_id === archived.id)).toBeUndefined();
    await narrate('Download ZIP appears only after the complete response arrives. The downloaded file was independently read and checked: the photograph matches stored bytes, and the no-photo record remains present.', 14);
    await page.goBack(); await page.goForward();
    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByRole('switch', { name: 'Dark theme' }).setChecked(true);
    await expect(page.getByRole('link', { name: 'Download ZIP', exact: true })).toBeVisible();
    await narrate('Navigation and appearance changes retain the selected format and prepared file in private memory. Reload, sign out, identity changes, or ten minutes clear the file. Download started does not confirm a disk save.', 15);
    // WHEN the scope changes and preparation fails THEN retry produces a fresh complete snapshot.
    await page.getByRole('radio', { name: 'Active and archived records', exact: true }).check();
    await page.route('**/api/items/export-package', route => route.fulfill({ status: 503 }), { times: 1 });
    await page.getByRole('button', { name: 'Prepare export', exact: true }).click();
    await expect(page.getByRole('alert')).toBeVisible();
    await narrate('Changing scope discards the old file. Here preparation deliberately fails. Required photographs are never silently omitted. Retry captures a new snapshot; persistent failures need storage investigation, or a separate text CSV.', 15);
    await page.getByRole('button', { name: 'Retry', exact: true }).click();
    await expect(page.getByRole('link', { name: 'Download ZIP', exact: true })).toBeVisible();
    const complete = await downloadPackage(page, `${root}/all-package.zip`);
    expect(complete.records.find(row => row[3] === archived.id)?.[8]).toBe('true');
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.getByRole('switch', { name: 'Dark theme' }).setChecked(false);
    // THEN show exact extracted manifest text and stored photo bytes without the app document's CSP.
    // Navigation removes Workbench scripts; offline mode proves no authenticated request is needed.
    await page.goto('about:blank');
    await context.setOffline(true);
    await page.setContent('<main style="font:20px system-ui;max-width:1100px;margin:40px auto;color:#26231f"><h1>Inside the downloaded collection package</h1><p>README.txt · records.csv · manifest.json · photos/&lt;item_id&gt;.webp</p><img alt="Exported stored detail photograph" style="float:right;width:300px;margin:0 0 24px 24px"><h2>manifest.json — exact extracted text</h2><pre style="font-size:14px;white-space:pre-wrap;overflow-wrap:anywhere"></pre></main>');
    await page.locator('img').evaluate((image, base64) => { (image as HTMLImageElement).src = `data:image/webp;base64,${base64}`; }, complete.entries.get(`photos/${active.id}.webp`)!.toString('base64'));
    await expect.poll(() => page.locator('img').evaluate(image => (image as HTMLImageElement).naturalWidth)).toBeGreaterThan(0);
    await page.locator('pre').evaluate((element, text) => { element.textContent = text; }, complete.entries.get('manifest.json')!.toString('utf8'));
    await narrate('After extraction, match item IDs across the CSV, manifest, and photograph filenames. The manifest preserves literal text and includes byte lengths and SHA 256 digests. A WebP-capable viewer opens the photographs without Workbench.', 16);
    await page.locator('h2').evaluate(element => { element.textContent = 'README.txt — exact extracted instructions'; });
    await page.locator('pre').evaluate((element, text) => { element.textContent = text; }, complete.entries.get('README.txt')!.toString('utf8'));
    await narrate('The archive includes both active and archived records, with an explicit none status for records without photographs. This portable collection copy is not a database backup or an import format. Human collector usability still needs evaluation.', 15);
  } finally {
    await writeFile(`${root}/segments.json`, JSON.stringify(segments, null, 2));
    const video = page.video()!; await context.close(); await video.saveAs(`${root}/capture.webm`);
    try { await archiveExportItems(seed); } finally { await setup.close(); }
  }
});
