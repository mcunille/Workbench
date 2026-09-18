import { expect, test } from './diagnostic-fixture';
import { useAuthenticatedSession as signIn } from './auth-fixture';

for (const width of [320, 390, 600, 1440]) test(`purchase-order search keeps results anchored at ${width}px`, async ({ page }) => {
  // GIVEN a saved draft and a settled list at the target viewport.
  await signIn(page);
  await page.setViewportSize({ width, height: 900 });
  const title = `Layout anchor ${width} ${Date.now()}`;
  await page.goto('/purchase-orders/new');
  await page.getByLabel('Title', { exact: true }).fill(title);
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await expect(page).toHaveURL(/\/purchase-orders\/[a-f0-9-]{36}$/);
  await page.goto('/purchase-orders');
  await expect(page.getByText(title, { exact: true })).toBeVisible();
  const panel = page.locator('.po-draft-panel');
  const top = (await panel.boundingBox())!.y;
  let release!: () => void;
  const delayed = new Promise<void>(resolve => { release = resolve; });
  await page.route('**/api/beta/purchase-orders?*', async route => {
    await delayed;
    await route.continue();
  });
  // WHEN typing starts and a slow search is pending THEN controls and feedback do not move the results.
  await page.getByRole('searchbox').fill(title);
  await expect(page.getByRole('status')).toContainText('Loading purchases');
  await expect.poll(async () => (await panel.boundingBox())!.y).toBeCloseTo(top, 0);
  release();
  await expect(page.getByRole('status')).not.toContainText('Loading purchases');
  await expect.poll(async () => (await panel.boundingBox())!.y).toBeCloseTo(top, 0);
  // WHEN clearing THEN the original position and keyboard path remain intact.
  await page.getByRole('button', { name: 'Clear search' }).click();
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
