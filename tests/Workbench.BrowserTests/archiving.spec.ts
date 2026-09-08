import { expect, test, type Page } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { cameraImage, photoSignIn } from './photo-fixture';

test.setTimeout(180_000);
async function create(page: Page) {
  const csrf = await (await page.request.get('/api/auth/antiforgery')).json();
  const response = await page.request.post('/api/items', {
    headers: { 'X-CSRF-TOKEN': csrf.requestToken },
    data: { creationRequestId: crypto.randomUUID(), name: `H5 ${crypto.randomUUID()}`, notes: 'Entrusted record', location: 'Tray A' },
  });
  expect(response.status()).toBe(201);
  return response.json() as Promise<{ id: string; name: string }>;
}
const archive = (page: Page) => page.getByRole('button', { name: 'Archive record', exact: true });
const confirm = (page: Page) => page.getByRole('button', { name: 'Confirm archive record', exact: true });

test('H5 cancellation and confirmed archive preserve a photographed bookmark across views and appearances', async ({ page }) => {
  // GIVEN a persisted record with a real uploaded synthetic photograph.
  await photoSignIn(page);
  const item = await create(page);
  await create(page); // A separate active record keeps both collection views available.
  const url = `/inventory/${item.id}`;
  await page.goto(url);
  await page.getByLabel('Choose photograph', { exact: true }).setInputFiles(await cameraImage(page));
  await page.getByRole('button', { name: 'Upload photograph', exact: true }).click();
  await expect(page.getByAltText(`Photograph of ${item.name}`)).toBeVisible();
  const before = await (await page.request.get(`/api/items/${item.id}`)).json();
  let submissions = 0;
  page.on('request', request => { if (request.url().endsWith(`/api/items/${item.id}/archive`)) submissions++; });
  // WHEN keyboard confirmation is canceled THEN no archive request is sent and focus returns.
  await archive(page).focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Archive record?', exact: true })).toBeFocused();
  await expect(page.getByText(/Restoring it to browsing is currently unavailable/)).toBeVisible();
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(archive(page)).toBeFocused();
  expect(submissions).toBe(0);
  // WHEN explicitly confirmed THEN details and the same photo remain read-only through their link.
  await archive(page).click();
  await confirm(page).click();
  await expect(page.getByText('Archived', { exact: true })).toBeVisible();
  const after = await (await page.request.get(`/api/items/${item.id}`)).json();
  expect(after.photo).toEqual(before.photo);
  expect(after.name).toBe(before.name);
  expect(after.createdAtUtc).toBe(before.createdAtUtc);
  await mkdir('../../artifacts/h5', { recursive: true });
  for (const width of [320, 1280]) {
    await page.setViewportSize({ width, height: 900 });
    for (const theme of ['light', 'dark']) {
      await page.getByRole('combobox', { name: 'Appearance', exact: true }).selectOption(theme);
      await page.reload();
      await expect(page.getByAltText(`Photograph of ${item.name}`)).toBeVisible();
      expect(await page.getByAltText(`Photograph of ${item.name}`).evaluate((image: HTMLImageElement) => image.naturalWidth)).toBeGreaterThan(0);
      await expect(archive(page)).toHaveCount(0);
      await expect(page.getByRole('button', { name: 'Edit details', exact: true })).toHaveCount(0);
      await expect(page.getByLabel('Choose photograph', { exact: true })).toHaveCount(0);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      for (const control of await page.locator('button:visible, select:visible').all())
        expect((await control.boundingBox())!.height).toBeGreaterThanOrEqual(44);
      await page.screenshot({ path: `../../artifacts/h5/archived-${width}-${theme}.png`, fullPage: true });
    }
  }
  // AND neither Grid nor List nor a matching search exposes the archived record.
  await page.getByRole('link', { name: 'Back to collection', exact: true }).click();
  for (const view of ['Grid', 'List']) {
    await page.getByRole('button', { name: view, exact: true }).click();
    await expect(page.getByRole('link').filter({ has: page.getByText(item.name, { exact: true }) })).toHaveCount(0);
    await page.getByRole('searchbox', { name: 'Search collection', exact: true }).fill(item.name);
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    await expect(page.getByText(/no matches/i)).toBeVisible();
    await page.getByRole('button', { name: 'Clear', exact: true }).click();
  }
  await page.goto(url);
  await expect(page.getByText('Archived', { exact: true })).toBeVisible();
});

test('H5 lost committed response retries the same token and reviews saved archive state', async ({ page }) => {
  // GIVEN archive commits but its success response never reaches the browser.
  await photoSignIn(page);
  const item = await create(page);
  await page.goto(`/inventory/${item.id}`);
  const payloads: unknown[] = [];
  await page.route(`**/api/items/${item.id}/archive`, async route => {
    payloads.push(route.request().postDataJSON());
    if (payloads.length === 1) {
      expect((await route.fetch()).status()).toBe(200);
      await route.abort('failed');
    } else await route.continue();
  });
  await archive(page).click();
  await confirm(page).click();
  await expect(page.getByRole('alert')).toContainText('Archiving could not be confirmed');
  await page.setViewportSize({ width: 320, height: 900 });
  await page.getByRole('combobox', { name: 'Appearance', exact: true }).selectOption('dark');
  // WHEN retrying THEN the unchanged token conflicts and current saved state is loaded.
  await page.getByRole('button', { name: 'Retry archive', exact: true }).click();
  await expect(page.getByText('Archived', { exact: true })).toBeVisible();
  expect(payloads).toHaveLength(2);
  expect(payloads[1]).toEqual(payloads[0]);
  await expect(archive(page)).toHaveCount(0);
});

test('H5 another session edit requires a fresh confirmation after conflict and failed recovery read', async ({ page, browser }) => {
  // GIVEN independently authenticated sessions sharing the displayed item version.
  await photoSignIn(page);
  const item = await create(page);
  await page.goto(`/inventory/${item.id}`);
  const context = await browser.newContext({ baseURL: 'http://127.0.0.1:4179' });
  const other = await context.newPage();
  try {
    await photoSignIn(other);
    await other.goto(`/inventory/${item.id}`);
    await archive(page).click();
    await other.getByRole('button', { name: 'Edit details', exact: true }).click();
    await other.getByLabel('Name', { exact: true }).fill('Reidentified retained record');
    await other.getByRole('button', { name: 'Save changes', exact: true }).click();
    await expect(other.getByRole('heading', { name: 'Reidentified retained record', exact: true })).toBeVisible();
    // WHEN stale archive conflicts and its recovery GET fails THEN no fresh archive is enabled.
    await page.route(`**/api/items/${item.id}`, route => route.fulfill({ status: 503, json: {} }), { times: 1 });
    await confirm(page).click();
    await expect(page.getByRole('alert')).toContainText('Could not load the current record');
    await expect(confirm(page)).toHaveCount(0);
    await page.getByRole('button', { name: 'Retry loading current record', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Reidentified retained record', exact: true })).toBeVisible();
    // THEN current text is shown and a separate explicit confirmation is required.
    await expect(confirm(page)).toHaveCount(0);
    await archive(page).click();
    await expect(page.getByText(/Archive “Reidentified retained record”/)).toBeVisible();
    await confirm(page).click();
    await expect(page.getByText('Archived', { exact: true })).toBeVisible();
  } finally { await context.close(); }
});


test('H5 an open text draft survives another session archive without offering a save', async ({ page, browser }) => {
  // GIVEN a local unsaved correction and another independently signed-in session.
  await photoSignIn(page);
  const item = await create(page);
  await page.goto(`/inventory/${item.id}`);
  await page.getByRole('button', { name: 'Edit details', exact: true }).click();
  await page.getByLabel('Name', { exact: true }).fill('Recoverable unsaved name');
  const context = await browser.newContext({ baseURL: 'http://127.0.0.1:4179' });
  try {
    const other = await context.newPage();
    await photoSignIn(other);
    await other.goto(`/inventory/${item.id}`);
    await archive(other).click();
    await confirm(other).click();
    await expect(other.getByText('Archived', { exact: true })).toBeVisible();
    // WHEN saving the old draft THEN it is retained for recovery and cannot overwrite the archive.
    await page.getByRole('button', { name: 'Save changes', exact: true }).click();
    await expect(page.getByText('Recoverable unsaved name', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Review my edits', exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Save changes', exact: true })).toHaveCount(0);
    await page.getByRole('button', { name: 'Discard draft and view archived record', exact: true }).click();
    await expect(page.getByText('Archived', { exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { name: item.name, exact: true })).toBeVisible();
  } finally { await context.close(); }
});
