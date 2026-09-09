import { setAppearance } from './user-menu-fixture';
import { browserBaseUrl } from './browser-environment';
import { expect, test, type Page } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { photoSignIn } from './photo-fixture';

test.setTimeout(120_000);

async function checkTextContrast(page: Page) {
  for (const text of await page.locator('h1:visible, h2:visible, h3:visible, label:visible, dt:visible, dd:visible, [role="alert"]:visible').all()) {
    const contrast = await text.evaluate(element => {
      const luminance = (color: string) => color.match(/[\d.]+/g)!.slice(0, 3).map(Number).map(channel => {
        const value = channel / 255;
        return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
      }).reduce((sum, value, index) => sum + value * [0.2126, 0.7152, 0.0722][index], 0);
      let background = 'rgb(255, 255, 255)';
      for (let parent: Element | null = element; parent; parent = parent.parentElement) {
        const candidate = getComputedStyle(parent).backgroundColor;
        if (candidate !== 'rgba(0, 0, 0, 0)' && candidate !== 'transparent') { background = candidate; break; }
      }
      const foreground = luminance(getComputedStyle(element).color);
      const behind = luminance(background);
      return (Math.max(foreground, behind) + 0.05) / (Math.min(foreground, behind) + 0.05);
    });
    expect(contrast, `Text contrast: ${await text.textContent()}`).toBeGreaterThanOrEqual(4.5);
  }
}

async function create(page: Page) {
  const csrf = await (await page.request.get('/api/auth/antiforgery')).json();
  const response = await page.request.post('/api/items', {
    headers: { 'X-CSRF-TOKEN': csrf.requestToken },
    data: { creationRequestId: crypto.randomUUID(), name: `H4 ${crypto.randomUUID()}`, notes: 'September fair', location: 'Tray A' },
  });
  expect(response.status()).toBe(201);
  return response.json() as Promise<{ id: string; name: string }>;
}

test('H4 two independent sessions recover stale edits and refresh search', async ({ page, browser }) => {
  // GIVEN two independently authenticated sessions reading one persisted version.
  await photoSignIn(page);
  const item = await create(page);
  const otherContext = await browser.newContext({ baseURL: browserBaseUrl });
  const other = await otherContext.newPage();
  try {
    await photoSignIn(other, 'secondary');
    await page.getByRole('searchbox', { name: 'Search collection', exact: true }).fill(item.name);
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    await page.getByRole('link').filter({ has: page.getByText(item.name, { exact: true }) }).click();
    await other.goto(`/inventory/${item.id}`);
    for (const session of [page, other]) await session.getByRole('button', { name: 'Edit details', exact: true }).click();
    await page.getByLabel('Name', { exact: true }).fill('Identified blue sapphire');
    await other.getByLabel('Storage location (optional)', { exact: true }).fill('Display box');

    // WHEN the first session saves and the second submits its older version.
    await page.getByRole('button', { name: 'Save changes', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Identified blue sapphire', exact: true })).toBeVisible();
    await other.getByRole('button', { name: 'Save changes', exact: true }).click();

    // THEN the draft survives and explicit reconciliation starts from the current saved name.
    await expect(other.getByRole('button', { name: 'Review my edits', exact: true })).toBeVisible();
    await expect(other.getByText('Display box', { exact: true })).toBeVisible();
    await other.setViewportSize({ width: 320, height: 900 });
    await mkdir('../../artifacts/h4', { recursive: true });
    for (const theme of ['dark', 'light']) {
      await setAppearance(other, theme === 'dark');
      expect(await other.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await checkTextContrast(other);
      await other.screenshot({ path: `../../artifacts/h4/conflict-320-${theme}.png`, fullPage: true });
    }
    await other.getByRole('button', { name: 'Review my edits', exact: true }).click();
    await expect(other.getByLabel('Name', { exact: true })).toHaveValue('Identified blue sapphire');
    await other.getByLabel('Storage location (optional)', { exact: true }).fill('Display box');
    await other.getByRole('button', { name: 'Save changes', exact: true }).click();
    await expect(other.getByRole('button', { name: 'Edit details', exact: true })).toBeVisible();
    await page.reload();
    await expect(page.getByText('Display box', { exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Identified blue sapphire', exact: true })).toBeVisible();
    expect(new URL(page.url()).pathname).toBe(`/inventory/${item.id}`);
    // AND search reflects both the new match and removal of the old match.
    await page.getByRole('link', { name: 'Back to collection', exact: true }).click();
    const search = page.getByRole('searchbox', { name: 'Search collection', exact: true });
    await search.fill(item.name);
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    await expect(page.getByText(/no matches/i)).toBeVisible();
    await search.fill('Display box');
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    await expect(page.getByRole('link').filter({ has: page.getByText('Identified blue sapphire', { exact: true }) })).toBeVisible();
  } finally { await otherContext.close(); }
});

test('H4 cancel and failed save preserve truthful state across appearance and layout changes', async ({ page }) => {
  // GIVEN a saved item opened for correction.
  await photoSignIn(page);
  const item = await create(page);
  await page.goto(`/inventory/${item.id}`);
  await page.getByRole('button', { name: 'Edit details', exact: true }).click();
  await page.getByLabel('Name', { exact: true }).fill('Discarded correction');
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.getByRole('heading', { name: item.name, exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Edit details', exact: true }).click();
  await page.getByLabel('Name', { exact: true }).fill('Recovered correction');

  // WHEN a save fails before reaching the server.
  await page.route(`**/api/items/${item.id}`, route => route.request().method() === 'PUT'
    ? route.fulfill({ status: 503, json: { title: 'Unavailable' } }) : route.continue(), { times: 1 });
  await page.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(page.getByRole('alert')).toBeVisible();
  await mkdir('../../artifacts/h4', { recursive: true });
  for (const width of [320, 1280]) {
    await page.setViewportSize({ width, height: 900 });
    for (const theme of ['dark', 'light', 'system']) {
      await setAppearance(page, theme === 'dark');
      // THEN recoverable text survives, controls fit, and keyboard retry remains available.
      await expect(page.getByLabel('Name', { exact: true })).toHaveValue('Recovered correction');
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await checkTextContrast(page);
      for (const control of await page.locator('button:visible, input:visible, select:visible').all())
        expect((await control.boundingBox())!.height).toBeGreaterThanOrEqual(44);
      await page.evaluate(() => window.scrollTo(0, 0));
      await page.screenshot({ path: `../../artifacts/h4/editor-${width}-${theme}.png`, fullPage: true });
    }
  }
  const retry = page.getByRole('button', { name: 'Retry save', exact: true });
  await retry.focus();
  await page.keyboard.press('Tab');
  await page.keyboard.press('Shift+Tab');
  await expect(retry).toBeFocused();
  expect(await retry.evaluate(element => getComputedStyle(element).outlineStyle)).not.toBe('none');
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Recovered correction', exact: true })).toBeVisible();
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Recovered correction', exact: true })).toBeVisible();
});

test('H4 a lost success response cannot overwrite a later save on retry', async ({ page }) => {
  // GIVEN an edit that commits but loses its response on the way back to the browser.
  await photoSignIn(page);
  const item = await create(page);
  await page.goto(`/inventory/${item.id}`);
  await page.getByRole('button', { name: 'Edit details', exact: true }).click();
  await page.getByLabel('Name', { exact: true }).fill('First saved correction');
  await page.route(`**/api/items/${item.id}`, async route => {
    const response = await route.fetch();
    expect(response.status()).toBe(200);
    await route.abort('failed');
  }, { times: 1 });
  await page.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Retry save', exact: true })).toBeVisible();
  await expect(page.getByRole('alert')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Retry save', exact: true })).toBeEnabled();
  const current = await (await page.request.get(`/api/items/${item.id}`)).json();
  expect(current.name).toBe('First saved correction');
  const csrf = await (await page.request.get('/api/auth/antiforgery')).json();
  const later = await page.request.put(`/api/items/${item.id}`, {
    headers: { 'X-CSRF-TOKEN': csrf.requestToken },
    data: { expectedVersion: current.version, name: 'Later correction', notes: current.notes, location: current.location },
  });
  expect(later.status()).toBe(200);
  // WHEN retrying the unconfirmed request THEN its old token conflicts and preserves the later save.
  await page.getByRole('button', { name: 'Retry save', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Use saved record', exact: true })).toBeVisible();
  await expect(page.getByText('First saved correction', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Use saved record', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Later correction', exact: true })).toBeVisible();
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Later correction', exact: true })).toBeVisible();
});

test('H4 a lost creation response retries successfully after another session edits the item', async ({ page, browser }) => {
  // GIVEN creation commits but its response is lost before the original browser receives it.
  await photoSignIn(page);
  const name = `Unconfirmed creation ${crypto.randomUUID()}`;
  const editedName = `Identified sapphire ${crypto.randomUUID()}`;
  const payloads: unknown[] = [];
  await page.route('**/api/items', async route => {
    if (route.request().method() !== 'POST') return route.continue();
    payloads.push(route.request().postDataJSON());
    if (payloads.length === 1) {
      const response = await route.fetch();
      expect(response.status()).toBe(201);
      await route.abort('failed');
    } else {
      await route.continue();
    }
  });
  await page.getByRole('link', { name: 'Add item', exact: true }).click();
  await page.getByLabel('Name', { exact: true }).fill(name);
  await page.getByLabel('Notes (optional)', { exact: true }).fill('Original identification');
  await page.getByLabel('Storage location (optional)', { exact: true }).fill('Tray A');
  await page.getByRole('button', { name: 'Save item', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Retry save', exact: true })).toBeEnabled();
  await expect(page.getByLabel('Name', { exact: true })).toBeDisabled();

  // AND another independently authenticated session discovers and corrects the saved item.
  const otherContext = await browser.newContext({ baseURL: browserBaseUrl });
  const other = await otherContext.newPage();
  try {
    await photoSignIn(other, 'secondary');
    await other.getByRole('searchbox', { name: 'Search collection', exact: true }).fill(name);
    await other.getByRole('button', { name: 'Search', exact: true }).click();
    await other.getByRole('link').filter({ has: other.getByText(name, { exact: true }) }).click();
    const detailUrl = other.url();
    await other.getByRole('button', { name: 'Edit details', exact: true }).click();
    await other.getByLabel('Name', { exact: true }).fill(editedName);
    await other.getByLabel('Notes (optional)', { exact: true }).fill('Corrected identification');
    await other.getByLabel('Storage location (optional)', { exact: true }).fill('Display box');
    await other.getByRole('button', { name: 'Save changes', exact: true }).click();
    await expect(other.getByRole('heading', { name: editedName, exact: true })).toBeVisible();

    // WHEN the original browser retries its unchanged creation request.
    const replayResponse = page.waitForResponse(response => response.url().endsWith('/api/items') && response.request().method() === 'POST');
    await page.getByRole('button', { name: 'Retry save', exact: true }).click();

    // THEN the retry resolves to the same item with all current details and no duplicate.
    expect((await replayResponse).status()).toBe(200);
    expect(payloads).toHaveLength(2);
    expect(payloads[1]).toEqual(payloads[0]);
    await expect(page).toHaveURL(detailUrl);
    await expect(page.getByRole('heading', { name: editedName, exact: true })).toBeVisible();
    await expect(page.getByText('Corrected identification', { exact: true })).toBeVisible();
    await expect(page.getByText('Display box', { exact: true })).toBeVisible();
    await page.reload();
    await expect(page.getByRole('heading', { name: editedName, exact: true })).toBeVisible();
    await page.getByRole('link', { name: 'Back to collection', exact: true }).click();
    await page.getByRole('searchbox', { name: 'Search collection', exact: true }).fill(editedName);
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    await expect(page.getByRole('link').filter({ has: page.getByText(editedName, { exact: true }) })).toHaveCount(1);
  } finally { await otherContext.close(); }
});
