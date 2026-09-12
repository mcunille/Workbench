import { expect, test } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve, isAbsolute, sep } from 'node:path';
import { browserBaseUrl } from './browser-environment';
import { useAuthenticatedSession } from './auth-fixture';
import { acquisitionPanel, createOrigin, createPiece } from './shared-acquisition-fixture';
import { receiptImage } from './acquisition-document-fixture';
import { lifecycle } from './restoration-fixture';
import { downloadExport } from './export-fixture';
import { downloadPackage, uploadPackagePhoto } from './package-fixture';
import { setAppearance } from './user-menu-fixture';

test('H12 narrated acquisition history and paperwork export journey', async ({ browser }) => {
  test.setTimeout(420_000);
  const output = process.env.WORKBENCH_H12_MEDIA;
  if (!output || !isAbsolute(output)) throw new Error('Set WORKBENCH_H12_MEDIA to an absolute directory outside the repository.');
  const root = resolve(output); const repo = resolve('../..');
  if (root.toLowerCase() === repo.toLowerCase() || root.toLowerCase().startsWith(repo.toLowerCase() + sep))
    throw new Error('Walkthrough media must remain outside the repository.');
  await mkdir(root, { recursive: true });
  const setup = await browser.newContext({ baseURL: browserBaseUrl });
  const seed = await setup.newPage();
  await useAuthenticatedSession(seed);
  const token = crypto.randomUUID().slice(0, 8);
  const pieces = [];
  for (const name of ['Blue sapphire', 'Green tourmaline', 'Violet amethyst'])
    pieces.push(await createPiece(seed, `${name} — fair ${token}`));
  const source = `Autumn mineral fair ${token}`;
  const origin = await createOrigin(seed, pieces[0].id, source);
  const context = await browser.newContext({ baseURL: browserBaseUrl, storageState: await setup.storageState(),
    viewport: { width: 1280, height: 900 }, recordVideo: { dir: root, size: { width: 1280, height: 900 } } });
  const page = await context.newPage(); const start = Date.now();
  const runtimeErrors: string[] = [];
  page.on('pageerror', error => runtimeErrors.push(error.message));
  const segments: { start: number; end: number; text: string }[] = [];
  async function narrate(text: string, seconds = 8) {
    const begin = (Date.now() - start) / 1000;
    // Reserve narration time only; assertions synchronize all application state.
    await page.waitForTimeout(seconds * 1000);
    segments.push({ start: begin, end: (Date.now() - start) / 1000, text });
  }
  try {
    // GIVEN three independent pieces, WHEN connected through the UI THEN one acquisition owns their facts.
    for (const piece of pieces.slice(1)) {
      await page.goto(`/inventory/${piece.id}`);
      await acquisitionPanel(page).getByRole('button', { name: 'Connect to an acquisition', exact: true }).click();
      await page.getByRole('searchbox', { name: 'Search acquisitions', exact: true }).fill(source);
      await page.getByRole('button', { name: 'Search', exact: true }).click();
      await page.getByRole('button', { name: new RegExp(`^Select ${source}`) }).click();
      const savedLink = page.waitForResponse(response => response.request().method() === 'PUT' && new URL(response.url()).pathname === `/api/items/${piece.id}/acquisition-link`);
      await page.getByRole('button', { name: 'Save connection', exact: true }).click();
      expect((await savedLink).status()).toBe(200);
      await expect(page.getByRole('button', { name: 'Save connection', exact: true })).toHaveCount(0);
      await expect(acquisitionPanel(page).getByText(source, { exact: true })).toBeVisible();
    }
    await acquisitionPanel(page).getByRole('link', { name: 'View acquisition', exact: true }).click();
    for (const piece of pieces) await expect(page.getByRole('link', { name: piece.name, exact: true })).toBeVisible();
    await page.screenshot({ path: `${root}/shared-pieces-desktop.png`, fullPage: true });
    await narrate('Three stones retain separate identities and share one acquisition. The year is known; no month or day is invented. The downloaded history will preserve that relationship and its partial date.');

    // WHEN paperwork and a photograph are added THEN exact committed bytes become export evidence.
    const photo = await uploadPackagePhoto(page, pieces[0].id);
    const receipt = await receiptImage(page);
    const reportData = await page.evaluate(() => {
      const canvas = document.createElement('canvas'); canvas.width = 600; canvas.height = 400;
      const drawing = canvas.getContext('2d')!; drawing.fillStyle = '#fff'; drawing.fillRect(0, 0, 600, 400);
      drawing.fillStyle = '#222'; drawing.font = '22px sans-serif';
      drawing.fillText('SAMPLE SUPPORTING REPORT', 30, 70);
      drawing.font = '18px sans-serif'; drawing.fillText('Three fair stones — collector notes only', 30, 120);
      drawing.fillText('Synthetic example, not authentication', 30, 165);
      return canvas.toDataURL('image/png').split(',')[1];
    });
    const expectedFiles = [
      { label: 'Fair receipt', upload: receipt },
      { label: 'Supporting report', upload: { name: 'sample-report.png', mimeType: 'image/png', buffer: Buffer.from(reportData, 'base64') } },
    ];
    for (const file of expectedFiles) {
      await page.getByRole('button', { name: 'Add document', exact: true }).click();
      await page.getByLabel('Document label', { exact: true }).fill(file.label);
      await page.getByLabel('Choose document', { exact: true }).setInputFiles(file.upload);
      await page.getByRole('button', { name: 'Upload document', exact: true }).click();
      await expect(page.getByRole('button', { name: `Download ${file.label}`, exact: true })).toBeVisible();
    }
    await page.screenshot({ path: `${root}/paperwork-desktop.png`, fullPage: true });
    await narrate('A receipt and a supporting report belong to the shared acquisition. These sample documents preserve their uploaded bytes and labels. They are collector records, not proof of authenticity.');

    const archived = await (await seed.request.get(`/api/items/${pieces[2].id}`)).json();
    await lifecycle(seed, pieces[2].id, 'archive', archived.version);
    await page.goto(`/inventory/${pieces[2].id}`);
    await expect(page.getByRole('button', { name: 'Download Fair receipt', exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Add document', exact: true })).toHaveCount(0);
    await page.goto('/inventory');
    await page.getByRole('link', { name: 'Export records', exact: true }).click();
    await expect(page).toHaveURL(/\/inventory\/export$/);
    expect(await page.title()).toMatch(/Workbench/i);
    await expect(page.getByRole('heading', { name: 'Export records', exact: true })).toBeVisible();
    await expect(page.getByText(/CSV includes acquisition facts/)).toBeVisible();
    await page.getByRole('radio', { name: 'Active records', exact: true }).check();
    await page.getByRole('button', { name: 'Prepare export', exact: true }).click();
    await expect(page.getByRole('link', { name: 'Download CSV', exact: true })).toBeVisible();
    const csv = await downloadExport(page);
    for (const piece of pieces.slice(0, 2)) {
      const record = csv.find(row => row[3] === piece.id)!;
      expect(record.slice(11)).toEqual([origin.acquisition.id, 'Purchase', `'${source}`, 'year', '2020', '', '', "'Three individually recorded stones acquired together."]);
    }
    expect(csv.some(row => row[3] === pieces[2].id)).toBe(false);
    await narrate('CSV carries acquisition facts and shared identifiers. It excludes the archived stone in active scope and keeps unknown date components empty. Select ZIP when the paperwork and photographs must travel too.');

    // WHEN choosing ZIP THEN scope guidance precedes preparation and included documents appear once.
    const zipChoice = page.getByRole('radio', { name: 'Records, photographs and acquisition documents (ZIP)', exact: true });
    await zipChoice.focus(); await page.keyboard.press('Space');
    await expect(page.getByText(/Shared acquisition documents appear once/)).toBeVisible();
    await page.getByRole('button', { name: 'Prepare export', exact: true }).focus(); await page.keyboard.press('Enter');
    await expect(page.getByRole('link', { name: 'Download ZIP', exact: true })).toBeVisible();
    const active = await downloadPackage(page, `${root}/active-package.zip`);
    const activeAcquisition = active.manifest.acquisitions.find(value => value.acquisition_id === origin.acquisition.id)!;
    expect(activeAcquisition.included_item_ids.slice().sort()).toEqual(pieces.slice(0, 2).map(piece => piece.id).sort());
    expect(activeAcquisition.documents).toHaveLength(2);
    expect(JSON.stringify(active.manifest)).not.toContain(pieces[2].id);
    expect(JSON.stringify(active.manifest)).not.toContain(pieces[2].name);
    expect(active.entries.get(`photos/${pieces[0].id}.webp`)).toEqual(photo.bytes);
    for (const file of expectedFiles) {
      const document = activeAcquisition.documents.find(value => value.label === file.label)!;
      expect(active.entries.get(document.path)).toEqual(file.upload.buffer);
      expect([...active.entries.keys()].filter(path => path.includes(document.document_id))).toHaveLength(1);
    }
    await narrate('The active package contains both active stones and exactly one copy of each shared document. Its relationship list excludes the archived identity. A receipt may still describe that stone: document contents and written notes are not redacted.');

    // WHEN appearances, navigation and reduced preferences change THEN the ready file remains usable at 320px.
    const ready = page.getByRole('link', { name: 'Download ZIP', exact: true });
    const readyUrl = await ready.getAttribute('href');
    await page.goBack(); await page.goForward();
    for (const width of [320, 1280]) for (const theme of ['light', 'dark']) {
      await page.setViewportSize({ width, height: 900 });
      await page.emulateMedia({ reducedMotion: 'reduce' });
      await setAppearance(page, theme === 'dark');
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
      await expect(ready).toHaveAttribute('href', readyUrl!);
      await expect(zipChoice).toBeChecked();
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      for (const control of await page.locator('button:visible, a.button:visible, .export-scope label:visible').all())
        expect((await control.boundingBox())!.height).toBeGreaterThanOrEqual(44);
      const cdp = await page.context().newCDPSession(page);
      await cdp.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-transparency', value: 'reduce' }] });
      expect(await page.evaluate(() => getComputedStyle(document.querySelector('.workspace-nav')!).backdropFilter)).toBe('none');
      await cdp.detach();
      await page.screenshot({ path: `${root}/export-${width}-${theme}.png`, fullPage: true });
    }
    await narrate('The ready download survives navigation and appearance changes. The same choices and controls remain available on a narrow screen, by keyboard, and with reduced visual effects.');

    // WHEN all scope is selected THEN the archived identity returns without duplicating shared paperwork.
    await page.getByRole('radio', { name: 'Active and archived records', exact: true }).check();
    await expect(ready).toHaveCount(0);
    await page.getByRole('button', { name: 'Prepare export', exact: true }).click();
    await expect(ready).toBeVisible();
    const all = await downloadPackage(page, `${root}/all-package.zip`);
    const allAcquisition = all.manifest.acquisitions.find(value => value.acquisition_id === origin.acquisition.id)!;
    expect(allAcquisition.included_item_ids.slice().sort()).toEqual(pieces.map(piece => piece.id).sort());
    expect(allAcquisition.documents).toHaveLength(2);
    expect(all.records.find(row => row[3] === pieces[2].id)?.[8]).toBe('true');
    for (const file of expectedFiles) expect(all.entries.get(allAcquisition.documents.find(value => value.label === file.label)!.path)).toEqual(file.upload.buffer);
    await writeFile(`${root}/manifest.json`, all.entries.get('manifest.json')!);
    await writeFile(`${root}/README.txt`, all.entries.get('README.txt')!);

    // WHEN storage preparation fails (simulated) THEN no partial package is offered and a new snapshot can recover.
    await page.route('**/api/items/export-package', route => route.fulfill({ status: 503 }), { times: 1 });
    await page.getByRole('button', { name: 'Prepare new export', exact: true }).click();
    await expect(page.getByRole('alert')).toContainText('photograph or document storage');
    await expect(ready).toHaveCount(0);
    await page.screenshot({ path: `${root}/preparation-failure.png`, fullPage: true });
    await narrate('This simulated preparation failure offers no partial ZIP. Retry reads a new snapshot; repeated storage failures need operator recovery. CSV remains an alternative for facts, but excludes photographs and paperwork.');
    await page.getByRole('button', { name: 'Retry', exact: true }).click();
    await expect(ready).toBeVisible();
    await downloadPackage(page);
    expect(runtimeErrors).toEqual([]);

    // THEN ordinary offline text inspection explains relationships without a Workbench session.
    const offline = await browser.newContext({ offline: true, viewport: { width: 1280, height: 900 } });
    try {
      const reader = await offline.newPage();
      const summary = JSON.stringify({ scope: all.manifest.scope, acquisition: allAcquisition }, null, 2);
      const escaped = summary.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');
      await reader.setContent(`<title>Offline acquisition export inspection</title><main style="max-width:70rem;margin:2rem auto;font:18px system-ui"><h1>Downloaded acquisition history</h1><p>Offline text inspection of the downloaded manifest. Three identities share one acquisition and two documents.</p><pre style="white-space:pre-wrap;overflow-wrap:anywhere">${escaped}</pre></main>`);
      await expect(reader.getByRole('heading', { name: 'Downloaded acquisition history' })).toBeVisible();
      await expect(reader.locator('pre')).toContainText('Fair receipt');
      await reader.screenshot({ path: `${root}/offline-manifest.png`, fullPage: true });
    } finally { await offline.close(); }
    await writeFile(`${root}/verification.json`, JSON.stringify({ acquisitionId: origin.acquisition.id, includedActiveIds: activeAcquisition.included_item_ids,
      includedAllIds: allAcquisition.included_item_ids, documentCount: allAcquisition.documents.length,
      exactDocumentBytesVerified: true, runtimeErrors, failureSimulation: 'HTTP 503 route interception',
      humanTrial: 'Not performed; automation does not establish uncoached collector comprehension.' }, null, 2));
  } finally {
    await writeFile(`${root}/segments.json`, JSON.stringify(segments, null, 2));
    const video = page.video(); await context.close();
    if (video) await video.saveAs(`${root}/capture.webm`);
    await setup.close();
  }
});
