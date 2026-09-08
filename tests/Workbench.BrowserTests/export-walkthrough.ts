import { browserBaseUrl } from './browser-environment';
import { expect, test } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { useAuthenticatedSession } from './auth-fixture';
import { lifecycle } from './restoration-fixture';
import { createExportItem, downloadExport } from './export-fixture';

test('H7 narrated collection export walkthrough', async ({ browser }) => {
  test.setTimeout(240_000);
  const root = '../../artifacts/h7/walkthrough'; await mkdir(root, { recursive: true });
  const setup = await browser.newContext({ baseURL: browserBaseUrl });
  const seed = await setup.newPage(); await useAuthenticatedSession(seed);
  const active = await createExportItem(seed, 'Blue stone from the September fair', 'Natural blue colour\nPurchased at the fair', 'Tray A');
  const archived = await createExportItem(seed, 'Archived quartz specimen', 'Original record retained', 'Tray B');
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
    await page.goto('/inventory');
    await page.getByRole('searchbox').fill('September');
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    await narrate('The collection search shows the September stone. Export records lets us retrieve the collection independently of this search or the pages already loaded.', 11);
    await page.getByRole('link', { name: 'Export records', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Prepare export', exact: true })).toBeDisabled();
    await narrate('Choose the export scope explicitly: active records, or active and archived records. This is a text record export. It excludes photographs and is not an application backup.', 13);
    await page.getByRole('radio', { name: 'Active records', exact: true }).focus();
    await page.keyboard.press('Space');
    await narrate('The version one CSV supports up to ten thousand records and thirty two mebibytes. Import columns as text to preserve identifiers and timestamps.', 12);
    await page.getByRole('button', { name: 'Prepare export', exact: true }).click();
    await expect(page.getByRole('link', { name: 'Download CSV', exact: true })).toBeVisible();
    await narrate('Workbench prepares the whole file before offering Download CSV. User text has one protective apostrophe prefix. CSV readers recover the original by removing exactly one prefix.', 13);
    const activeRows = await downloadExport(page);
    expect(activeRows.find(row => row[3] === active.id)).toBeDefined();
    expect(activeRows.find(row => row[3] === archived.id)).toBeUndefined();
    await narrate('Download started means the browser received the download request. It does not claim that your operating system saved the file.', 10);
    await page.goBack(); await page.goForward();
    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByRole('combobox', { name: 'Appearance', exact: true }).selectOption('dark');
    await expect(page.getByRole('link', { name: 'Download CSV', exact: true })).toBeVisible();
    await narrate('Ordinary navigation and appearance changes retain the prepared file in private application memory. Reload, sign out, an identity change, or ten minutes clears it.', 12);
    await page.getByRole('radio', { name: 'Active and archived records', exact: true }).check();
    await expect(page.getByRole('link', { name: 'Download CSV', exact: true })).toHaveCount(0);
    await page.route('**/api/items/export', route => route.abort('failed'), { times: 1 });
    await page.getByRole('button', { name: 'Prepare export', exact: true }).click();
    await expect(page.getByRole('alert')).toBeVisible();
    await narrate('Changing scope discards the previous file. Here we deliberately interrupt preparation. No partial download is offered. Retry prepares a new snapshot; records may have changed.', 13);
    await page.getByRole('button', { name: /retry/i }).click();
    await expect(page.getByRole('link', { name: 'Download CSV', exact: true })).toBeVisible();
    const allRows = await downloadExport(page);
    expect(allRows.find(row => row[3] === archived.id)![8]).toBe('true');
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.getByRole('combobox', { name: 'Appearance', exact: true }).selectOption('light');
    await narrate('The successful export now includes the archived record and its archival timestamp. Automated checks parse the download and compare saved text. Collector usability still needs human evaluation.', 13);
  } finally {
    await writeFile(`${root}/segments.json`, JSON.stringify(segments, null, 2));
    const video = page.video()!; await context.close(); await video.saveAs(`${root}/capture.webm`); await setup.close();
  }
});
