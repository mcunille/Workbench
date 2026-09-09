import { browserBaseUrl } from './browser-environment';
import { expect, test } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { photoSignIn, cameraImage } from './photo-fixture';
import { createArchived, lifecycle, restore, confirmRestore, searchArchive } from './restoration-fixture';
test.setTimeout(180_000);
test('H6 archive navigation and photographed restoration persist in another session at mobile and desktop sizes', async ({ page, browser }) => {
  // GIVEN an archived photographed record and another authenticated session.
  await photoSignIn(page);
  let item = await createArchived(page);
  item = await lifecycle(page, item.id, 'restore', item.version);
  await page.goto(`/inventory/${item.id}`);
  await page.getByLabel('Choose photograph', { exact: true }).setInputFiles(await cameraImage(page));
  await page.getByRole('button', { name: 'Upload photograph', exact: true }).click();
  await expect(page.getByAltText(`Photograph of ${item.name}`)).toBeVisible();
  item = await (await page.request.get(`/api/items/${item.id}`)).json();
  const archived = await lifecycle(page, item.id, 'archive', item.version);
  await page.goto('/inventory');
  await page.getByRole('searchbox').fill('active draft');
  await page.getByRole('link', { name: 'Archive', exact: true }).click();
  await searchArchive(page, item.name);
  await page.getByRole('button', { name: 'List', exact: true }).click();
  await page.getByRole('link').filter({ has: page.getByText(item.name, { exact: true }) }).click();
  await expect(page.getByText('Archived', { exact: true })).toBeVisible();
  // WHEN using browser Back/Forward and app Back THEN independent traversal and origin survive.
  await page.goBack();
  await expect(page.getByRole('searchbox')).toHaveValue(item.name);
  await expect(page.getByRole('button', { name: 'List', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await page.goForward();
  await expect(page.getByRole('link', { name: 'Back to archive', exact: true })).toBeVisible();
  await page.getByRole('link', { name: 'Back to archive', exact: true }).click();
  await page.getByRole('link', { name: 'Collection', exact: true }).click();
  await expect(page.getByRole('searchbox')).toHaveValue('active draft');
  await page.getByRole('link', { name: 'Archive', exact: true }).click();
  await page.getByRole('link').filter({ has: page.getByText(item.name, { exact: true }) }).click();
  await restore(page).focus(); await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Restore to collection?', exact: true })).toBeFocused();
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(restore(page)).toBeFocused();
  expect((await (await page.request.get(`/api/items/${item.id}`)).json()).version).toBe(archived.version);
  await mkdir('../../artifacts/h6', { recursive: true });
  // THEN archived controls stay read-only and usable at 320px/desktop in both appearances.
  for (const width of [320, 1280]) for (const theme of ['light', 'dark']) {
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.getByRole('switch', { name: 'Dark theme' }).setChecked(theme === 'dark');
    await expect(page.getByAltText(`Photograph of ${item.name}`)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Edit details', exact: true })).toHaveCount(0);
    await expect(page.getByLabel('Choose photograph', { exact: true })).toHaveCount(0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    for (const control of await page.locator('button:visible, select:visible').all()) expect((await control.boundingBox())!.height).toBeGreaterThanOrEqual(44);
    await page.screenshot({ path: `../../artifacts/h6/archived-${width}-${theme}.png`, fullPage: true });
    await page.getByRole('link', { name: 'Back to archive', exact: true }).click();
    await expect(page.getByRole('searchbox')).toHaveValue(item.name);
    await expect(page.getByRole('button', { name: 'List', exact: true })).toHaveAttribute('aria-pressed', 'true');
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-transparency', value: 'reduce' }] });
    expect(await page.evaluate(() => getComputedStyle(document.querySelector('.topbar')!).backdropFilter)).toBe('none');
    await cdp.detach();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: `../../artifacts/h6/archive-list-${width}-${theme}.png`, fullPage: true });
    await page.getByRole('link').filter({ has: page.getByText(item.name, { exact: true }) }).click();
  }
  // WHEN restoring THEN identity, photo and saved details return in this and another authorized session.
  await restore(page).click(); await confirmRestore(page).click();
  await expect(page.getByText('Record restored to collection.', { exact: true })).toBeVisible();
  // AND the dialog's deferred focus handoff completes before the next keyboard action.
  await expect(page.getByRole('button', { name: 'Edit details', exact: true })).toBeFocused();
  // AND keyboard fragment navigation plus appearance changes preserve the archive origin.
  await page.getByRole('link', { name: 'Skip to content', exact: true }).focus();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/#main$/);
  await page.getByRole('switch', { name: 'Dark theme' }).setChecked(false);
  await expect(page.getByRole('link', { name: 'Back to archive', exact: true })).toBeVisible();
  const restored = await (await page.request.get(`/api/items/${item.id}`)).json();
  expect(restored).toEqual({ ...archived, archivedAtUtc: null, version: restored.version });
  expect(restored.version).not.toBe(archived.version);
  // WHEN reopening from Collection, then jumping between same-record history entries.
  await page.getByRole('link', { name: 'View in collection', exact: true }).click();
  await page.getByRole('searchbox').fill(item.name);
  await page.getByRole('button', { name: 'Search', exact: true }).click();
  await page.getByRole('link').filter({ has: page.getByText(item.name, { exact: true }) }).click();
  await expect(page.getByRole('link', { name: 'Back to collection', exact: true })).toBeVisible();
  await page.evaluate(() => history.go(-2));
  // THEN the archive origin renders immediately, including the earlier native fragment entry.
  await expect(page.getByRole('link', { name: 'Back to archive', exact: true })).toBeVisible();
  await page.evaluate(() => history.go(2));
  await expect(page.getByRole('link', { name: 'Back to collection', exact: true })).toBeVisible();
  await page.evaluate(() => history.go(-2));
  await page.getByRole('link', { name: 'Back to archive', exact: true }).click();
  await expect(page.getByText('No matches', { exact: true })).toBeVisible();
  const context = await browser.newContext({ baseURL: browserBaseUrl });
  try {
    const other = await context.newPage(); await photoSignIn(other, 'secondary');
    await other.getByRole('searchbox').fill(item.name); await other.getByRole('button', { name: 'Search', exact: true }).click();
    await other.getByRole('link').filter({ has: other.getByText(item.name, { exact: true }) }).click();
    await other.reload();
    await expect(other.getByAltText(`Photograph of ${item.name}`)).toBeVisible();
    await expect(other.getByRole('button', { name: 'Edit details', exact: true })).toBeVisible();
    expect(await (await other.request.get(`/api/items/${item.id}`)).json()).toEqual(restored);
  } finally { await context.close(); }
});
test('H6 lost committed restore response retains same-token retry and uncertain navigation protection', async ({ page }) => {
  // GIVEN the server commits but its first response is lost.
  await photoSignIn(page); const item = await createArchived(page); await page.goto(`/inventory/${item.id}`);
  const bodies: unknown[] = [];
  await page.route(`**/api/items/${item.id}/restore`, async route => {
    bodies.push(route.request().postDataJSON());
    if (bodies.length === 1) { expect((await route.fetch()).status()).toBe(200); await route.abort('failed'); } else await route.continue();
  });
  await restore(page).click(); await confirmRestore(page).click();
  await expect(page.getByRole('alert')).toContainText('Restoration could not be confirmed');
  await page.setViewportSize({ width: 320, height: 900 });
  await page.getByRole('switch', { name: 'Dark theme' }).setChecked(true);
  // WHEN navigating away THEN the uncertain submitted operation is disclosed.
  await page.getByRole('link', { name: 'Back to archive', exact: true }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByRole('button', { name: 'Keep editing', exact: true }).click();
  await page.getByRole('button', { name: 'Retry restore', exact: true }).click();
  // THEN same-token retry reviews saved state without a false success attribution.
  await expect(page.getByText('This record is already in the collection. Current saved record loaded.', { exact: true })).toBeVisible();
  expect(bodies).toEqual([{ expectedVersion: item.version }, { expectedVersion: item.version }]);
  await expect(restore(page)).toHaveCount(0);
});
test('H6 a competing restore and re-archive requires renewed confirmation after a failed recovery read', async ({ page, browser }) => {
  // GIVEN an archived version whose confirmation is open in one session.
  await photoSignIn(page); const item = await createArchived(page); await page.goto(`/inventory/${item.id}`); await restore(page).click();
  const context = await browser.newContext({ baseURL: browserBaseUrl });
  try {
    const other = await context.newPage(); await photoSignIn(other, 'secondary');
    const active = await lifecycle(other, item.id, 'restore', item.version);
    const current = await lifecycle(other, item.id, 'archive', active.version);
    const bodies: any[] = []; page.on('request', request => { if (request.url().endsWith(`/api/items/${item.id}/restore`)) bodies.push(request.postDataJSON()); });
    // WHEN the obsolete restore conflicts and its recovery read fails THEN no current token is silently reused.
    await page.route(`**/api/items/${item.id}`, route => route.fulfill({ status: 503, json: {} }), { times: 1 });
    await confirmRestore(page).click();
    await expect(page.getByRole('alert')).toContainText('Could not load the current record');
    await expect(confirmRestore(page)).toHaveCount(0);
    await page.getByRole('button', { name: 'Retry loading current record', exact: true }).click();
    await expect(page.getByText('Current saved record loaded.', { exact: true })).toBeVisible();
    expect(bodies).toHaveLength(1);
    // THEN only a new explicit confirmation sends the newly reviewed version.
    await restore(page).click(); await confirmRestore(page).click();
    await expect(page.getByText('Record restored to collection.', { exact: true })).toBeVisible();
    expect(bodies).toEqual([{ expectedVersion: item.version }, { expectedVersion: current.version }]);
  } finally { await context.close(); }
});
test('H6 archive searches beyond the first page and distinguishes missing matches from retryable failures', async ({ page }) => {
  // GIVEN more than one full page of archived records under a unique search prefix.
  await photoSignIn(page); const prefix = `H6-pages-${crypto.randomUUID()}`;
  for (let i = 0; i < 51; i++) await createArchived(page, `${prefix}-${String(i).padStart(2, '0')}`);
  await page.goto('/inventory/archive'); await searchArchive(page, prefix);
  await expect(page.getByRole('status')).toContainText('50 matching items loaded');
  await page.route('**/api/items/archived?**', route => route.fulfill({ status: 503, json: {} }), { times: 1 });
  // WHEN the second page fails THEN the first page remains and retry retrieves the remaining item.
  await page.getByRole('button', { name: 'Load more', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('We could not load the archive');
  await expect(page.locator('.collection-list li')).toHaveCount(50);
  await page.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(page.locator('.collection-list li')).toHaveCount(51);
  await searchArchive(page, `${prefix}-50`);
  await expect(page.locator('.collection-list li')).toHaveCount(1);
  await searchArchive(page, `${prefix}-absent`); await expect(page.getByText('No matches', { exact: true })).toBeVisible();
});
