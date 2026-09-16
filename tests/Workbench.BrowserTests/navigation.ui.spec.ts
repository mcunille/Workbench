import { expect, test } from './diagnostic-fixture';
import { join } from 'node:path';

test('initially collapsed navigation can expand below 900px', async ({ page }) => {
  // GIVEN a signed-in workspace first loaded at a narrow desktop width.
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.setViewportSize({ width: 899, height: 900 });
  await page.route('**/api/system', route => route.fulfill({ json: { name: 'Workbench', version: 'test' } }));
  await page.route('**/api/auth/me', route => route.fulfill({ json: {
    userId: 'sample', tenantName: 'Sample Studio', email: 'preview@example.test', permissions: ['TenantAccess'],
  } }));
  await page.route('**/api/items{,?*}', route => route.fulfill({ json: { items: [], nextCursor: null } }));
  await page.goto('/');
  const nav = page.getByRole('navigation', { name: 'Workspace' });
  const expand = nav.getByRole('button', { name: 'Expand navigation' });
  // WHEN starting collapsed THEN the expand control is visible and keyboard-operable.
  await expect(expand).toBeVisible();
  await expect(expand).toHaveAttribute('aria-expanded', 'false');
  await expect.poll(async () => (await nav.boundingBox())!.width).toBe(72);
  await expand.focus();
  await page.keyboard.press('Enter');
  await expect.poll(async () => (await nav.boundingBox())!.width).toBe(229);
  const collapse = nav.getByRole('button', { name: 'Collapse navigation' });
  await expect(collapse).toHaveAttribute('aria-expanded', 'true');
  // AND collapsing again restores the available expand control.
  await collapse.click();
  await expect.poll(async () => (await nav.boundingBox())!.width).toBe(72);
  await expect(expand).toBeVisible();
  const directory = process.env.WORKBENCH_MENU_EVIDENCE_DIRECTORY;
  if (directory) await page.screenshot({ path: join(directory, 'navigation-expand-control.png') });
  // AND a phone still uses the pill without a sidebar toggle.
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(expand).toBeHidden();
  await expect.poll(async () => (await nav.boundingBox())!.y).toBeGreaterThan(700);
});

test('content stays centered beside the viewport-aligned navigation', async ({ page }) => {
  // GIVEN a wide workspace with sample data and deterministic layout transitions.
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.route('**/api/system', route => route.fulfill({ json: { name: 'Workbench', version: 'test' } }));
  await page.route('**/api/auth/me', route => route.fulfill({ json: {
    userId: 'sample', tenantName: 'Sample Studio', email: 'preview@example.test', permissions: ['TenantAccess'],
  } }));
  await page.route('**/api/items?*', route => route.fulfill({ json: { items: [], nextCursor: null } }));
  await page.route('**/api/items', route => route.fulfill({ json: { items: [], nextCursor: null } }));
  await page.setViewportSize({ width: 2400, height: 1000 });
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Collection', exact: true })).toBeVisible();
  const nav = page.getByRole('navigation', { name: 'Workspace' });
  for (const collapsed of [false, true]) {
    // WHEN the sidebar is expanded or collapsed THEN only the content column is centered.
    if (collapsed) await nav.getByRole('button', { name: 'Collapse navigation' }).click();
    for (const width of [2400, 1600, 1280]) {
      await page.setViewportSize({ width, height: 1000 });
      const sidebar = (await nav.boundingBox())!;
      const content = (await page.locator('main').boundingBox())!;
      expect(sidebar.x).toBe(0);
      expect(content.x - sidebar.width).toBeCloseTo(width - content.x - content.width, 0);
      expect(content.width).toBeLessThanOrEqual(1216);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      const directory = process.env.WORKBENCH_MENU_EVIDENCE_DIRECTORY;
      if (directory && width === 2400) await page.screenshot({ path: join(directory, `content-centered-${collapsed ? 'collapsed' : 'expanded'}.png`) });
    }
  }
  // AND the mobile pill leaves the full-width content centered in the viewport.
  await page.setViewportSize({ width: 390, height: 844 });
  const mobile = (await page.locator('main').boundingBox())!;
  expect(mobile.x + mobile.width / 2).toBeCloseTo(195, 0);
});
