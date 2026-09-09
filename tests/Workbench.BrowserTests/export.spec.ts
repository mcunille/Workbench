import { expect, test } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { useAuthenticatedSession } from './auth-fixture';
import { lifecycle } from './restoration-fixture';
import { archiveExportItems, createExportItem, downloadExport } from './export-fixture';

test.setTimeout(180_000);

test.afterEach(async ({ page }) => {
  // GIVEN each scenario owns its seeded IDs, WHEN it finishes or fails,
  // THEN archive those records so later scenarios can browse their active first page.
  await archiveExportItems(page);
});

test('H7 explicit export scopes include records beyond browsing pages and preserve exact encoded text', async ({ page }) => {
  // GIVEN more than a browsing page, an archived record, and multiline formula-leading text.
  await useAuthenticatedSession(page);
  const prefix = `H7-${crypto.randomUUID()}`;
  const ids: string[] = [];
  for (let i = 0; i < 51; i++) ids.push((await createExportItem(page, `${prefix}-${i}`)).id);
  const special = await createExportItem(page, `=SUM(1,2) ${prefix}`, "'Original\nQuoted \"detail\", café", '@Tray');
  const archived = await createExportItem(page, `${prefix}-archived`);
  await lifecycle(page, archived.id, 'archive', archived.version);
  await page.getByRole('searchbox').fill(`${prefix}-absent`);
  await page.getByRole('button', { name: 'Search', exact: true }).click();
  // WHEN opening export from a filtered collection THEN an explicit choice is required.
  await page.getByRole('link', { name: 'Export records', exact: true }).click();
  await expect(page).toHaveURL(/\/inventory\/export$/);
  await expect(page.getByRole('radio', { name: 'Active records', exact: true })).not.toBeChecked();
  await expect(page.getByRole('radio', { name: 'Active and archived records', exact: true })).not.toBeChecked();
  await expect(page.getByRole('button', { name: 'Prepare export', exact: true })).toBeDisabled();
  await page.getByRole('radio', { name: 'Active records', exact: true }).check();
  await page.getByRole('button', { name: 'Prepare export', exact: true }).click();
  await expect(page.getByRole('link', { name: 'Download CSV', exact: true })).toBeVisible();
  // THEN CSV includes every seeded active record despite the search and loaded-page boundary.
  const active = await downloadExport(page);
  expect(active.map(row => row[3])).toEqual(expect.arrayContaining([...ids, special.id]));
  expect(active.find(row => row[3] === archived.id)).toBeUndefined();
  const specialRow = active.find(row => row[3] === special.id)!;
  expect(specialRow[5].slice(1)).toBe(special.name);
  expect(specialRow[6].slice(1)).toBe(special.notes);
  expect(specialRow[7].slice(1)).toBe(special.location);
  expect(active.every(row => row[2] === 'active' && row[8] === 'false')).toBe(true);
  // WHEN choosing all records THEN the old file is removed and the archived row is included.
  await page.getByRole('radio', { name: 'Active and archived records', exact: true }).check();
  await expect(page.getByRole('link', { name: 'Download CSV', exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Prepare export', exact: true }).click();
  await expect(page.getByRole('link', { name: 'Download CSV', exact: true })).toBeVisible();
  const all = await downloadExport(page);
  expect(all.map(row => row[3])).toEqual(expect.arrayContaining([...ids, special.id, archived.id]));
  expect(all.find(row => row[3] === archived.id)![8]).toBe('true');
  expect(all.find(row => row[3] === archived.id)![10]).not.toBe('');
  expect(all.every(row => row[2] === 'all')).toBe(true);
});

test('H7 prepared download survives navigation and appearance and remains usable with keyboard at 320px', async ({ page }) => {
  // GIVEN an export opened from Archive.
  await useAuthenticatedSession(page);
  await createExportItem(page, `H7-layout-${crypto.randomUUID()}`);
  await page.getByRole('link', { name: 'Archive', exact: true }).click();
  await page.getByRole('link', { name: 'Export records', exact: true }).click();
  const scope = page.getByRole('radio', { name: 'Active records', exact: true });
  await scope.focus(); await page.keyboard.press('Space');
  const prepare = page.getByRole('button', { name: 'Prepare export', exact: true });
  await prepare.focus(); await page.keyboard.press('Enter');
  await expect(page.getByRole('link', { name: 'Download CSV', exact: true })).toBeVisible();
  // WHEN navigating back and forward THEN private in-memory preparation survives.
  await page.goBack(); await page.goForward();
  await expect(scope).toBeChecked();
  await expect(page.getByRole('link', { name: 'Download CSV', exact: true })).toBeVisible();
  await mkdir('../../artifacts/h7', { recursive: true });
  for (const width of [320, 1280]) for (const theme of ['light', 'dark']) {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.getByRole('switch', { name: 'Dark theme' }).setChecked(theme === 'dark');
    // THEN status, download and every button remain visible without horizontal overflow.
    await expect(page.getByRole('link', { name: 'Download CSV', exact: true })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    for (const control of await page.locator('button:visible, select:visible, a.button:visible, .export-scope label:visible').all()) expect((await control.boundingBox())!.height).toBeGreaterThanOrEqual(44);
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-transparency', value: 'reduce' }] });
    expect(await page.evaluate(() => getComputedStyle(document.querySelector('.topbar')!).backdropFilter)).toBe('none');
    await cdp.detach();
    await page.evaluate(() => window.scrollTo(0, 0));
    await page.screenshot({ path: `../../artifacts/h7/export-${width}-${theme}.png`, fullPage: true });
    expect((await downloadExport(page)).length).toBeGreaterThan(0);
  }
  // WHEN reloading THEN the old file and scope are cleared.
  await page.reload();
  await expect(page.getByRole('link', { name: 'Download CSV', exact: true })).toHaveCount(0);
  await expect(scope).not.toBeChecked();
});

test('H7 empty, failed and disconnected preparations never offer a partial download and support retry', async ({ page }) => {
  // GIVEN an authenticated collector with an explicit scope.
  await useAuthenticatedSession(page);
  await createExportItem(page, `H7-retry-${crypto.randomUUID()}`);
  await page.getByRole('link', { name: 'Export records', exact: true }).click();
  await page.getByRole('radio', { name: 'Active records', exact: true }).check();
  const prepare = page.getByRole('button', { name: 'Prepare export', exact: true });
  // WHEN the selected scope is empty THEN no file is offered.
  await page.route('**/api/items/export', route => route.fulfill({ status: 204 }), { times: 1 });
  await prepare.click();
  await expect(page.getByRole('status')).toContainText(/no records/i);
  await expect(page.getByRole('link', { name: 'Download CSV', exact: true })).toHaveCount(0);
  // WHEN preparation fails THEN retry is explicit and there is no stale file.
  await page.route('**/api/items/export', route => route.fulfill({ status: 503, contentType: 'application/problem+json', json: { title: 'Preparation failed', code: 'export_preparation_failed' } }), { times: 1 });
  await page.getByRole('button', { name: 'Prepare new export', exact: true }).click();
  await expect(page.getByRole('alert')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Download CSV', exact: true })).toHaveCount(0);
  await page.route('**/api/items/export', route => route.abort('failed'), { times: 1 });
  await page.getByRole('button', { name: /retry/i }).click();
  await expect(page.getByRole('alert')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Download CSV', exact: true })).toHaveCount(0);
  // THEN a new successful snapshot can be downloaded after retry.
  await page.getByRole('button', { name: /retry/i }).click();
  await expect(page.getByRole('link', { name: 'Download CSV', exact: true })).toBeVisible();
  expect((await downloadExport(page)).length).toBeGreaterThan(0);
});

test('H7 cancelling preparation rejects a late complete response and lets the collector prepare again', async ({ page }) => {
  // GIVEN a complete server response held back before it reaches the browser.
  await useAuthenticatedSession(page);
  await createExportItem(page, `H7-cancel-${crypto.randomUUID()}`);
  await page.getByRole('link', { name: 'Export records', exact: true }).click();
  await page.getByRole('radio', { name: 'Active records', exact: true }).check();
  let release!: () => void; const held = new Promise<void>(resolve => { release = resolve; });
  let received!: () => void; const responseReady = new Promise<void>(resolve => { received = resolve; });
  let delivered!: () => void; const settled = new Promise<void>(resolve => { delivered = resolve; });
  await page.route('**/api/items/export', async route => {
    try {
      const response = await route.fetch(); received(); await held;
      await route.fulfill({ response });
    } finally { delivered(); }
  }, { times: 1 });
  await page.getByRole('button', { name: 'Prepare export', exact: true }).click();
  await responseReady;
  // WHEN cancelling THEN no download is offered, even after the complete response is released.
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  release(); await settled;
  await expect(page.getByRole('link', { name: 'Download CSV', exact: true })).toHaveCount(0);
  await expect(page.getByRole('radio', { name: 'Active records', exact: true })).toBeChecked();
  // THEN another explicit preparation succeeds.
  await page.getByRole('button', { name: 'Prepare export', exact: true }).click();
  await expect(page.getByRole('link', { name: 'Download CSV', exact: true })).toBeVisible();
  expect((await downloadExport(page)).length).toBeGreaterThan(0);
});
