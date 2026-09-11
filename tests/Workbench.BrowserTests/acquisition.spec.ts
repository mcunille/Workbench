import { expect, test, type Page } from '@playwright/test';
import { browserBaseUrl } from './browser-environment';
import { signInThroughUi, useAuthenticatedSession } from './auth-fixture';
import { lifecycle } from './restoration-fixture';
import { setAppearance } from './user-menu-fixture';

test.setTimeout(120_000);

async function createItem(page: Page) {
  const csrf = await (await page.request.get('/api/auth/antiforgery')).json();
  const response = await page.request.post('/api/items', {
    headers: { 'X-CSRF-TOKEN': csrf.requestToken },
    data: { creationRequestId: crypto.randomUUID(), name: `H9 ${crypto.randomUUID()}` },
  });
  expect(response.status()).toBe(201);
  return response.json() as Promise<{ id: string; name: string }>;
}
const panel = (page: Page) => page.getByRole('region', { name: 'Acquisition', exact: true });
const save = (page: Page) => page.getByRole('button', { name: 'Save acquisition', exact: true }).click();

test('H9 optional facts, explicit methods and partial dates survive a new login and archive', async ({ page, browser }) => {
  // GIVEN ordinary acquisitions whose source and exact date are not always known.
  await useAuthenticatedSession(page);
  const items: { id: string; method: string; date: string }[] = [];
  for (const [method, precision, date] of [['Unknown', 'Unknown', 'Not recorded'], ['Gift', 'Year', '2020'], ['Inheritance', 'Month', '2020-02'], ['Trade', 'Exact date', '2020-02-29']]) {
    const item = await createItem(page);
    await page.goto(`/inventory/${item.id}`);
    await expect(panel(page).getByText('No acquisition recorded.', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Add acquisition', exact: true }).click();
    await expect(page.getByLabel('Acquisition method', { exact: true })).toHaveValue('');
    await page.getByLabel('Acquisition method', { exact: true }).selectOption(method);
    await page.getByLabel('Date precision', { exact: true }).selectOption(precision);
    if (precision !== 'Unknown') await page.getByLabel('Year', { exact: true }).fill('2020');
    if (precision === 'Month' || precision === 'Exact date') await page.getByLabel('Month', { exact: true }).fill('2');
    if (precision === 'Exact date') await page.getByLabel('Day', { exact: true }).fill('29');
    if (method === 'Gift') {
      // GIVEN a fractional year WHEN saving THEN allow correction without an uncertain request.
      await page.getByLabel('Year', { exact: true }).fill('2020.5');
      await save(page);
      await expect(page.getByText('Enter a whole year from 1 to 9999.', { exact: true })).toBeVisible();
      await expect(page.getByLabel('Year', { exact: true })).toBeFocused();
      await page.getByLabel('Year', { exact: true }).fill('2020');
    }
    await page.getByLabel('Provenance notes (optional)', { exact: true }).fill('<b>Family recollection</b>');
    // WHEN the collector saves only known facts THEN the saved view invents no precision or HTML.
    await save(page);
    await expect(page.getByRole('button', { name: 'Edit acquisition', exact: true })).toBeFocused();
    await expect(panel(page).getByText(method, { exact: true })).toBeVisible();
    await expect(panel(page).getByText('<b>Family recollection</b>', { exact: true })).toBeVisible();
    expect(await panel(page).locator('b').count()).toBe(0);
    items.push({ id: item.id, method, date });
  }
  // WHEN a fresh independent session signs in THEN persisted facts remain readable.
  const fresh = await browser.newContext({ baseURL: browserBaseUrl });
  try {
    const reader = await fresh.newPage();
    await signInThroughUi(reader);
    for (const item of items) {
      await reader.goto(`/inventory/${item.id}`);
      await expect(panel(reader).getByText(item.method, { exact: true })).toBeVisible();
      await expect(panel(reader).locator('dd').filter({ hasText: new RegExp(`^${item.date}$`) }).first()).toBeVisible();
    }
    const item = items[1];
    const current = await (await reader.request.get(`/api/items/${item.id}`)).json();
    await lifecycle(reader, item.id, 'archive', current.version);
    await reader.goto(`/inventory/${item.id}`);
    await expect(panel(reader).getByText('Gift', { exact: true })).toBeVisible();
    await expect(panel(reader).getByRole('button', { name: /Edit acquisition|Add acquisition|Change acquisition|Remove connection|Connect to an acquisition/i })).toHaveCount(0);
    await expect(panel(reader).getByRole('link', { name: 'View acquisition', exact: true })).toBeVisible();
  } finally { await fresh.close(); }
});

test('H9 competing sessions preserve drafts through failed conflict reads and deliberate reconciliation', async ({ page, browser }) => {
  // GIVEN two independently authenticated editors of the same acquisition.
  await useAuthenticatedSession(page);
  const item = await createItem(page);
  await page.goto(`/inventory/${item.id}`);
  await page.getByRole('button', { name: 'Add acquisition', exact: true }).click();
  await page.getByLabel('Acquisition method', { exact: true }).selectOption('Gift');
  await page.getByLabel('Gift from (optional)', { exact: true }).fill('Family');
  await save(page);
  await expect(page.getByRole('button', { name: 'Edit acquisition', exact: true })).toBeVisible();
  const context = await browser.newContext({ baseURL: browserBaseUrl });
  try {
    const other = await context.newPage();
    await useAuthenticatedSession(other, 'secondary');
    await other.goto(`/inventory/${item.id}`);
    for (const session of [page, other]) await session.getByRole('button', { name: 'Edit acquisition', exact: true }).click();
    await page.getByLabel('Gift from (optional)', { exact: true }).fill('Aunt Mira');
    await other.getByLabel('Provenance notes (optional)', { exact: true }).fill('My retained recollection');
    await save(page);
    await expect(panel(page).getByText('Aunt Mira', { exact: true })).toBeVisible();
    await other.route(`**/api/items/${item.id}/acquisition`, route => route.request().method() === 'GET'
      ? route.fulfill({ status: 503 }) : route.continue(), { times: 1 });
    // WHEN a stale edit conflicts and its comparison read fails THEN retry retains the draft.
    await save(other);
    await expect(other.getByRole('button', { name: 'Retry loading current acquisition', exact: true })).toBeVisible();
    await expect(other.getByText('My retained recollection', { exact: true })).toBeVisible();
    await other.getByRole('button', { name: 'Retry loading current acquisition', exact: true }).click();
    await expect(other.getByText('Aunt Mira', { exact: true })).toBeVisible();
    await other.setViewportSize({ width: 320, height: 900 });
    const cdp = await context.newCDPSession(other);
    await cdp.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-motion', value: 'reduce' }, { name: 'prefers-reduced-transparency', value: 'reduce' }] });
    for (const dark of [false, true]) {
      await setAppearance(other, dark);
      expect(await other.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await expect(other.getByText('My retained recollection', { exact: true })).toBeVisible();
      await expect(other.getByRole('navigation', { name: 'Workspace', exact: true })).toHaveCSS('backdrop-filter', 'none');
    }
    await cdp.detach();
    // THEN keyboard reconciliation starts with saved values and only explicit submission changes them.
    const review = other.getByRole('button', { name: 'Review my edits', exact: true });
    await review.focus(); await other.keyboard.press('Enter');
    await expect(other.getByLabel('Acquisition method', { exact: true })).toBeFocused();
    await expect(other.getByLabel('Gift from (optional)', { exact: true })).toHaveValue('Aunt Mira');
    await expect(other.getByLabel('Provenance notes (optional)', { exact: true })).toHaveValue('');
    await other.getByLabel('Provenance notes (optional)', { exact: true }).fill('My retained recollection');
    expect(await other.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    for (const control of await panel(other).locator('input, textarea, select, button').all()) {
      expect((await control.boundingBox())!.height).toBeGreaterThanOrEqual(44);
    }
    // AND cancelling navigation and changing appearance preserve the editable reconciliation.
    await other.getByRole('link', { name: 'Back to collection', exact: true }).click();
    await other.getByRole('dialog').getByRole('button', { name: 'Keep editing', exact: true }).click();
    await setAppearance(other, false);
    await expect(other.getByLabel('Provenance notes (optional)', { exact: true })).toHaveValue('My retained recollection');
    await save(other);
    await page.reload();
    await expect(panel(page).getByText('Aunt Mira', { exact: true })).toBeVisible();
    await expect(panel(page).getByText('My retained recollection', { exact: true })).toBeVisible();
    expect(new URL(page.url()).pathname).toBe(`/inventory/${item.id}`);
  } finally { await context.close(); }
});

test('H9 a lost creation response retries the identical request without a duplicate', async ({ page }) => {
  // GIVEN creation commits but its response is lost.
  await useAuthenticatedSession(page);
  const item = await createItem(page);
  await page.goto(`/inventory/${item.id}`);
  await page.getByRole('button', { name: 'Add acquisition', exact: true }).click();
  await page.getByLabel('Acquisition method', { exact: true }).selectOption('Trade');
  await page.getByLabel('Traded with (optional)', { exact: true }).fill('Local collector');
  const payloads: unknown[] = [];
  await page.route(`**/api/items/${item.id}/acquisition`, async route => {
    if (route.request().method() !== 'POST') return route.continue();
    payloads.push(route.request().postDataJSON());
    if (payloads.length > 1) return route.continue();
    expect((await route.fetch()).status()).toBe(201);
    await route.abort('failed');
  });
  await save(page);
  await expect(page.getByRole('alert')).toContainText('could not confirm');
  await expect(page.getByLabel('Traded with (optional)', { exact: true })).toBeDisabled();
  const committed = await (await page.request.get(`/api/items/${item.id}/acquisition`)).json();
  // WHEN retrying THEN the immutable request resolves to the same persisted acquisition.
  const replay = page.waitForResponse(response => response.url().endsWith(`/api/items/${item.id}/acquisition`) && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Retry save', exact: true }).click();
  expect((await replay).status()).toBe(200);
  expect(payloads).toHaveLength(2); expect(payloads[1]).toEqual(payloads[0]);
  await expect(panel(page).getByText('Local collector', { exact: true })).toBeVisible();
  const saved = await (await page.request.get(`/api/items/${item.id}/acquisition`)).json();
  expect(saved.acquisition.id).toBe(committed.acquisition.id);
  // AND an edit with a lost response uses its original version and explicitly resolves the conflict.
  await page.getByRole('button', { name: 'Edit acquisition', exact: true }).click();
  await page.getByLabel('Provenance notes (optional)', { exact: true }).fill('Corrected recollection');
  await page.route(`**/api/items/${item.id}/acquisition/${saved.acquisition.id}`, async route => {
    expect((await route.fetch()).status()).toBe(200);
    await route.abort('failed');
  }, { times: 1 });
  await save(page);
  await page.getByRole('button', { name: 'Retry save', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Review current acquisition', exact: true })).toBeVisible();
  await expect(panel(page).getByText('Corrected recollection', { exact: true })).toHaveCount(2);
  await page.getByRole('button', { name: 'Use saved record', exact: true }).click();
  await expect(panel(page).getByText('Corrected recollection', { exact: true })).toBeVisible();
});
