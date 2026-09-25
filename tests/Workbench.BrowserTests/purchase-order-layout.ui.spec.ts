import { expect, test } from './diagnostic-fixture';
import { useInterceptedSession } from './intercepted-auth-fixture';

for (const width of [320, 390, 600, 1440]) test(`purchase-order search keeps results anchored at ${width}px`, async ({ page }) => {
  // GIVEN deterministic list responses owned before the first navigation at each viewport.
  const title = 'Layout anchor';
  const row = { id: '00000000-0000-4000-8000-000000000001', title, supplierName: null,
    firstItemDescription: null, poReference: 'PO-000001', supplierOrderReference: null,
    platform: null, state: 'Draft', updatedAtUtc: '2026-09-01T12:00:00Z' };
  let release!: () => void;
  const delayed = new Promise<void>(resolve => { release = resolve; });
  let received!: () => void;
  const searchReceived = new Promise<void>(resolve => { received = resolve; });
  await page.route('**/api/beta/items', route => route.fulfill({ json: { items: [], nextCursor: null } }));
  await page.route('**/api/beta/purchase-orders**', async route => {
    expect(route.request().method()).toBe('GET');
    expect(new URL(route.request().url()).pathname).toBe('/api/beta/purchase-orders');
    if (new URL(route.request().url()).searchParams.get('query')) { received(); await delayed; }
    await route.fulfill({ json: { items: [row], nextCursor: null } });
  });
  await useInterceptedSession(page);
  await page.setViewportSize({ width, height: 900 });
  await page.goto('/purchase-orders');
  await expect(page.getByText(title, { exact: true })).toBeVisible();
  const panel = page.locator('.po-draft-panel');
  const top = (await panel.boundingBox())!.y;
  // WHEN typing starts and a slow search is pending THEN controls and feedback do not move the results.
  await page.getByRole('searchbox').fill(title);
  await searchReceived;
  await expect(page.getByRole('status')).toContainText('Loading purchases');
  await expect.poll(async () => (await panel.boundingBox())!.y).toBeCloseTo(top, 0);
  release();
  await expect(page.getByRole('status')).not.toContainText('Loading purchases');
  await expect.poll(async () => (await panel.boundingBox())!.y).toBeCloseTo(top, 0);
  // WHEN clearing THEN the original position and keyboard path remain intact.
  await page.getByRole('button', { name: 'Clear search' }).click();
  await expect(page.getByRole('searchbox')).toBeFocused();
  await expect(page.getByRole('status')).not.toContainText('Loading purchases');
  await expect.poll(async () => (await panel.boundingBox())!.y).toBeCloseTo(top, 0);
  // AND enlarged text can grow naturally without clipping or horizontal page overflow.
  await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
  await expect(page.getByRole('button', { name: 'Refresh', exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  // AND wider platform font metrics keep each action inside the page content without clipping.
  await page.addStyleTag({ content: '.po-page-actions { font-family: monospace; }' });
  const actions = page.locator('.po-page-actions');
  const bounds = (await actions.boundingBox())!;
  for (const action of await actions.getByRole('link').all()) {
    const box = (await action.boundingBox())!;
    expect(box.x + box.width, await action.innerText()).toBeLessThanOrEqual(bounds.x + bounds.width);
    expect(await action.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
  }
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});
