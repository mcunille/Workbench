import { captureEvidence } from './evidence-fixture';
import { expect, test } from './diagnostic-fixture';
import { setAppearance } from './user-menu-fixture';
import { useAuthenticatedSession } from './auth-fixture';
import { lifecycle } from './restoration-fixture';
import { archiveExportItems, createExportItem } from './export-fixture';
import { downloadPackage, uploadPackagePhoto } from './package-fixture';

test.setTimeout(180_000);
test.afterEach(async ({ page }) => { await archiveExportItems(page); });

test('H8 packages exact stored photographs, literal names and archived records with portable mapping', async ({ page }) => {
  // GIVEN active photo/no-photo records, an archived photo, and path-like Unicode text.
  await useAuthenticatedSession(page);
  const photo = await createExportItem(page, `../=Stone café ${crypto.randomUUID()}`, 'Line one\nLine two', '../Tray "A"');
  const stored = await uploadPackagePhoto(page, photo.id);
  const plain = await createExportItem(page, `No photograph ${crypto.randomUUID()}`);
  const archived = await createExportItem(page, `Archived photograph ${crypto.randomUUID()}`);
  const retired = await uploadPackagePhoto(page, archived.id);
  await lifecycle(page, archived.id, 'archive', retired.detail.version);
  await page.goto('/inventory');
  await page.getByRole('searchbox').fill('no matching visible record');
  await page.getByRole('button', { name: 'Search', exact: true }).click();
  await page.getByRole('link', { name: 'Export records', exact: true }).click();
  expect(await page.getByRole('radio', { name: 'Records (CSV)', exact: true }).isChecked()).toBe(true);
  await page.getByRole('radio', { name: 'Records, photographs and acquisition documents (ZIP)', exact: true }).check();
  await expect(page.getByRole('button', { name: 'Prepare export', exact: true })).toBeDisabled();
  // WHEN preparing active records THEN every included photograph matches the stored detail bytes.
  await page.getByRole('radio', { name: 'Active records', exact: true }).check();
  await page.getByRole('button', { name: 'Prepare export', exact: true }).click();
  await expect(page.getByRole('link', { name: 'Download ZIP', exact: true })).toBeVisible();
  const active = await downloadPackage(page);
  expect(active.entries.get(`photos/${photo.id}.webp`)).toEqual(stored.bytes);
  expect(active.manifest.items.find(item => item.item_id === plain.id)?.photo).toEqual({ status: 'none' });
  expect(active.manifest.items.find(item => item.item_id === photo.id)?.name).toBe(photo.name);
  expect(active.manifest.items.find(item => item.item_id === archived.id)).toBeUndefined();
  // WHEN all records are selected THEN the previous package is discarded and archived photos are present.
  await page.getByRole('radio', { name: 'Active and archived records', exact: true }).check();
  await expect(page.getByRole('link', { name: 'Download ZIP', exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Prepare export', exact: true }).click();
  await expect(page.getByRole('link', { name: 'Download ZIP', exact: true })).toBeVisible();
  const all = await downloadPackage(page);
  expect(all.entries.get(`photos/${archived.id}.webp`)).toEqual(retired.bytes);
  expect(all.records.find(row => row[3] === archived.id)?.[8]).toBe('true');
  // AND changing format removes the prepared private package.
  await page.getByRole('radio', { name: 'Records (CSV)', exact: true }).check();
  await expect(page.getByRole('link', { name: 'Download ZIP', exact: true })).toHaveCount(0);
});

test('H8 format and package survive navigation and both appearances with keyboard at 320px', async ({ page }) => {
  // GIVEN explicit scope and format selected by keyboard.
  await useAuthenticatedSession(page);
  await createExportItem(page, `H8-layout-${crypto.randomUUID()}`);
  await page.getByRole('link', { name: 'Archive', exact: true }).click();
  await page.getByRole('link', { name: 'Export records', exact: true }).click();
  const format = page.getByRole('radio', { name: 'Records, photographs and acquisition documents (ZIP)', exact: true });
  const scope = page.getByRole('radio', { name: 'Active records', exact: true });
  await format.focus(); await page.keyboard.press('Space');
  await scope.focus(); await page.keyboard.press('Space');
  await page.getByRole('button', { name: 'Prepare export', exact: true }).focus();
  await page.keyboard.press('Enter');
  const download = page.getByRole('link', { name: 'Download ZIP', exact: true });
  await expect(download).toBeVisible();
  const originalUrl = await download.getAttribute('href');
  // WHEN navigating and changing appearance THEN the same complete file and choices remain.
  await page.goBack(); await page.goForward();

  // CSV owns the shared desktop layout matrix; preserve the longest format label on a phone.
  for (const width of [320]) for (const theme of ['light', 'dark']) {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await setAppearance(page, theme === 'dark');
    await expect(format).toBeChecked(); await expect(scope).toBeChecked();
    await expect(download).toHaveAttribute('href', originalUrl!);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    for (const control of await page.locator('button:visible, a.button:visible, .export-scope label:visible').all()) expect((await control.boundingBox())!.height).toBeGreaterThanOrEqual(44);
    // Shared shell transparency is covered by inventory.spec.ts.
    await captureEvidence(page, `h8/package-${width}-${theme}.png`, { fullPage: true });
  }
  expect((await downloadPackage(page)).records.length).toBeGreaterThan(0);
  // WHEN reloading THEN both the private file and previous choices are cleared.
  await page.reload();
  await expect(download).toHaveCount(0); await expect(scope).not.toBeChecked();
  await expect(page.getByRole('radio', { name: 'Records (CSV)', exact: true })).toBeChecked();
});

test('H8 failures and interrupted bodies never expose a partial package; retry recovers', async ({ page }) => {
  // GIVEN a saved collection and selected package scope.
  await useAuthenticatedSession(page);
  await createExportItem(page, `H8-retry-${crypto.randomUUID()}`);
  await page.getByRole('link', { name: 'Export records', exact: true }).click();
  await page.getByRole('radio', { name: 'Records, photographs and acquisition documents (ZIP)', exact: true }).check();
  await page.getByRole('radio', { name: 'Active records', exact: true }).check();
  let attempts = 0;
  // Keep interception installed between phases: replacing one-shot handlers can race its teardown.
  await page.route('**/api/beta/items/export-package', async route => {
    attempts++;
    if (attempts === 1) return route.fulfill({ status: 503 });
    if (attempts === 2) return route.fulfill({ status: 200, headers: { 'Content-Type': 'application/zip', 'Content-Length': '100', 'Content-Disposition': 'attachment; filename="workbench-package-v2-active-20260909T120000Z.zip"' }, body: 'partial' });
    await route.continue();
  });
  // WHEN preparation is unavailable THEN recovery explains that photographs cannot be silently omitted.
  await page.getByRole('button', { name: 'Prepare export', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('photograph or document storage');
  // WHEN a successful-looking response has incomplete bytes THEN no download is offered.
  const partialResponse = page.waitForResponse('**/api/beta/items/export-package');
  await page.getByRole('button', { name: 'Retry', exact: true }).click();
  const partial = await partialResponse;
  expect(partial.status()).toBe(200);
  expect(partial.headers()['content-length']).toBe('100');
  expect(attempts).toBe(2);
  await expect(page.getByRole('alert')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Download ZIP', exact: true })).toHaveCount(0);
  // WHEN explicitly retrying THEN a complete, independently parsed package can be downloaded.
  await page.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(page.getByRole('link', { name: 'Download ZIP', exact: true })).toBeVisible();
  expect((await downloadPackage(page)).records.length).toBeGreaterThan(0);
  expect(attempts).toBe(3);
});
