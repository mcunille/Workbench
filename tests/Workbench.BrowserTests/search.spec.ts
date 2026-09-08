import { expect, test, type Page } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { photoSignIn } from './photo-fixture';

test.setTimeout(180_000);

async function seed(page: Page, name: string, notes: string, location: string) {
  const token = await (await page.request.get('/api/auth/antiforgery')).json();
  const response = await page.request.post('/api/items', {
    headers: { 'X-CSRF-TOKEN': token.requestToken },
    data: { creationRequestId: crypto.randomUUID(), name, notes, location },
  });
  expect(response.status()).toBe(201);
  return response.json();
}

for (const width of [320, 1280]) {
  test(`searches all pages and restores selected result at ${width}px`, async ({ page }) => {
    // GIVEN matching records beyond the unfiltered first page in a real tenant collection.
    await page.setViewportSize({ width, height: 900 });
    await photoSignIn(page);
    const phrase = `Search-${crypto.randomUUID()}`;
    for (let i = 0; i < 52; i++) await seed(page, `Unrelated ${i}`, '', '');
    for (let i = 0; i < 53; i++) await seed(page, `Piece ${i} ${phrase}`, `Remember ${phrase}`, 'Tray H3');
    await page.reload();
    const search = page.getByRole('searchbox', { name: 'Search collection', exact: true });
    await expect(search).toBeVisible();
    // WHEN submitting a case-insensitive phrase and loading the remaining matches.
    await search.fill(phrase.toUpperCase());
    await search.press('Enter');
    await expect(page.locator('.collection-list > li')).toHaveCount(50);
    await page.getByRole('button', { name: 'List', exact: true }).click();
    await page.getByRole('button', { name: 'Load more', exact: true }).click();
    await expect(page.locator('.collection-list > li')).toHaveCount(53);
    const selected = page.getByRole('link').filter({ has: page.getByText(`Piece 51 ${phrase}`, { exact: true }) });
    await selected.scrollIntoViewIfNeeded();
    const scroll = await page.evaluate(() => window.scrollY);
    await selected.click();
    await expect(page.getByRole('heading', { name: `Piece 51 ${phrase}`, exact: true })).toBeVisible();
    // THEN both the app link and browser history return to the loaded result and position.
    await page.getByRole('link', { name: 'Back to collection', exact: true }).click();
    await expect(page.locator('.collection-list > li')).toHaveCount(53);
    await expect(search).toHaveValue(phrase.toUpperCase());
    await expect(page.getByRole('button', { name: 'List', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect(selected).toBeFocused();
    await expect.poll(() => page.evaluate(() => window.scrollY)).toBeCloseTo(scroll, 0);
    await selected.click();
    await page.goBack();
    await expect(selected).toBeFocused();
    await expect.poll(() => page.evaluate(() => window.scrollY)).toBeCloseTo(scroll, 0);
    await page.goForward();
    await expect(page.getByRole('heading', { name: `Piece 51 ${phrase}`, exact: true })).toBeVisible();
    await page.goBack();
    await expect(selected).toBeFocused();
    for (const appearance of ['dark', 'light']) {
      await page.getByRole('switch', { name: 'Dark theme' }).setChecked(appearance === 'dark');
      await expect(search).toHaveValue(phrase.toUpperCase());
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    }
    await search.scrollIntoViewIfNeeded();
    await mkdir('../../artifacts/h3', { recursive: true });
    await page.screenshot({ path: `../../artifacts/h3/search-${width}.png` });
    // AND no matches is distinct from an empty collection; clearing retains the chosen view.
    await search.fill(`Absent-${crypto.randomUUID()}`);
    await search.press('Enter');
    await expect(page.getByText(/no matches/i)).toBeVisible();
    await page.getByRole('button', { name: 'Clear', exact: true }).click();
    await expect(page.locator('.collection-list > li')).toHaveCount(50);
    await expect(search).toHaveValue('');
    await expect(page.getByRole('button', { name: 'List', exact: true })).toHaveAttribute('aria-pressed', 'true');
    // AND reloading starts a fresh in-memory traversal without exposing search in the page URL.
    expect(new URL(page.url()).search).toBe('');
    await page.reload();
    await expect(search).toHaveValue('');
    await expect(page.getByRole('button', { name: 'Grid', exact: true })).toHaveAttribute('aria-pressed', 'true');
  });
}

test('search failure retries the submitted query rather than showing an empty collection', async ({ page }) => {
  // GIVEN a saved searchable item and a one-time transport failure for its search.
  await photoSignIn(page);
  const phrase = `Retry-${crypto.randomUUID()}`;
  await seed(page, 'Found after retry', phrase, 'Retry tray');
  let fail = true;
  await page.route('**/api/items?*', async route => {
    if (new URL(route.request().url()).searchParams.get('q') === phrase && fail) {
      fail = false;
      await route.fulfill({ status: 503, contentType: 'application/problem+json', body: '{}' });
    } else await route.continue();
  });
  // WHEN the submitted search fails THEN the page offers retry and does not claim no matches.
  await page.getByRole('searchbox', { name: 'Search collection', exact: true }).fill(phrase);
  await page.getByRole('button', { name: 'Search', exact: true }).click();
  await expect(page.getByRole('alert')).toBeVisible();
  await expect(page.getByText(/no matches/i)).toHaveCount(0);
  await page.getByRole('button', { name: 'Retry', exact: true }).click();
  // THEN the same query finds the persisted notes match and opens its location.
  await page.getByRole('link').filter({ has: page.getByText('Found after retry', { exact: true }) }).click();
  await expect(page.getByText('Retry tray', { exact: true })).toBeVisible();
});
