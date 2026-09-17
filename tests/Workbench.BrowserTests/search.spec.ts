import { pagedInventory, syntheticItem } from './paged-inventory-fixture';
import { captureEvidence } from './evidence-fixture';
import { setAppearance } from './user-menu-fixture';
import { expect, test, type Page } from './diagnostic-fixture';
import { photoSignIn } from './photo-fixture';

test.setTimeout(180_000);

async function seed(page: Page, name: string, notes: string, location: string, requestToken?: string) {
  const token = requestToken ?? (await (await page.request.get('/api/beta/auth/antiforgery')).json()).requestToken;
  const response = await page.request.post('/api/beta/items', {
    headers: { 'X-Workbench-Api-Revision': 'beta-1', 'X-CSRF-TOKEN': token },
    data: { creationRequestId: crypto.randomUUID(), name, notes, location },
  });
  expect(response.status()).toBe(201);
  return response.json();
}

for (const width of [320, 1280]) {
  test(`searches all pages and restores selected result at ${width}px`, async ({ page }) => {
    // GIVEN a real desktop collection or isolated phone responses beyond the first page.
    await page.setViewportSize({ width, height: 900 });
    const phrase = `Search-${crypto.randomUUID()}`;
    let unownedCollectionRequests = 0;
    if (width === 320) {
      // A lower-priority sentinel exposes any real collection request before fixture ownership.
      await page.route(url => url.pathname === '/api/beta/items', async route => {
        unownedCollectionRequests++;
        await route.fulfill({ json: { items: [], nextCursor: null } });
      });
      // Own inventory responses before navigation can start a real list or its thumbnails.
      await pagedInventory(page, [
        ...Array.from({ length: 52 }, (_, i) => syntheticItem(i, `Unrelated ${i}`)),
        ...Array.from({ length: 53 }, (_, i) => syntheticItem(i + 52, `Piece ${i} ${phrase}`)),
      ]);
    }
    const initialCollection = width === 320
      ? page.waitForResponse(response => new URL(response.url()).pathname === '/api/beta/items')
      : undefined;
    await photoSignIn(page);
    if (initialCollection) await initialCollection;
    if (width === 1280) {
      // One token belongs only to this authenticated setup batch; no cross-session cache.
      const { requestToken } = await (await page.request.get('/api/beta/auth/antiforgery')).json();
      for (let i = 0; i < 52; i++) await seed(page, `Unrelated ${i}`, '', '', requestToken);
      for (let i = 0; i < 53; i++) await seed(page, `Piece ${i} ${phrase}`, `Remember ${phrase}`, 'Tray H3', requestToken);
    } else {
      // THEN the initial page belongs to the synthetic fixture, before any reload.
      expect(unownedCollectionRequests).toBe(0);
    }
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
    // Center the target above floating navigation before recording the position.
    // Otherwise Playwright may scroll again to avoid the pill during click().
    await selected.evaluate(element => element.scrollIntoView({ block: 'center', behavior: 'instant' }));
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
      await setAppearance(page, appearance === 'dark');
      await expect(search).toHaveValue(phrase.toUpperCase());
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    }
    await search.scrollIntoViewIfNeeded();

    await captureEvidence(page, `h3/search-${width}.png`);
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
  await page.route('**/api/beta/items?*', async route => {
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
