import { expect, test } from './diagnostic-fixture';
import { useInterceptedSession } from './intercepted-auth-fixture';
import type { Supplier } from '../../src/Workbench.Client/src/api/suppliers';

for (const width of [320, 1440]) test(`supplier header keeps saved record context at ${width}px`, async ({ page }) => {
  // GIVEN owned create/read responses installed before navigating to the standalone editor.
  const id = '00000000-0000-4000-8000-000000000001';
  let record: Supplier | undefined;
  await page.route('**/api/beta/items', route => route.fulfill({ json: { items: [], nextCursor: null } }));
  await page.route('**/api/beta/suppliers', async route => {
    expect(route.request().method()).toBe('POST');
    const body = route.request().postDataJSON();
    record = { id, supplier: body.supplier, isArchived: false, version: 'AAAAAAAAB9E=',
      createdAtUtc: '2026-09-01T12:00:00Z', updatedAtUtc: '2026-09-01T12:00:00Z' };
    await route.fulfill({ status: 201, json: { requestId: body.requestId, supplierId: id,
      savedVersion: record.version, replayed: false, completedAtUtc: record.updatedAtUtc } });
  });
  await page.route('**/api/beta/suppliers/' + id, route => {
    expect(route.request().method()).toBe('GET');
    expect(record).toBeDefined();
    return route.fulfill({ json: record });
  });
  await useInterceptedSession(page);
  await page.setViewportSize({ width, height: 720 });
  await page.goto('/suppliers/new');
  await expect(page.getByRole('heading', { name: 'New supplier', exact: true })).toBeVisible();
  const toolbar = page.locator('.po-editor-toolbar');
  const save = toolbar.getByRole('button', { name: 'Save supplier', exact: true });
  await expect(save).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save supplier', exact: true })).toHaveCount(1);
  // AND the supplier uses the application's short-form width rather than the wider purchase-order canvas.
  expect((await page.getByRole('region', { name: 'Supplier editor' }).boundingBox())!.width).toBeLessThanOrEqual(768);
  const name = `供應商 💎 مورد ${'A'.repeat(150)} ${width}`;
  await page.getByLabel('Supplier name', { exact: true }).fill(name);
  await expect(page.getByRole('status')).toContainText('Unsaved changes');
  // WHEN saving from the toolbar THEN it submits the form and establishes saved identity.
  await save.click();
  await expect(page).toHaveURL(/\/suppliers\/[a-f0-9-]{36}$/);
  await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
  await expect(page.getByRole('status')).toContainText('Saved ');
  await expect(page.getByRole('status')).not.toContainText('Unsaved changes');
  await expect(page.getByRole('button', { name: 'Save supplier', exact: true })).toBeDisabled();
  // WHEN changing the name THEN the header identifies the saved record and announces unsaved work.
  await page.getByLabel('Supplier name', { exact: true }).fill('Local rename');
  await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
  await expect(page.getByRole('status')).toContainText('Unsaved changes');
  // AND toolbar actions remain reachable while scrolling, including enlarged text.
  await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
  await page.getByLabel('Postal address').scrollIntoViewIfNeeded();
  await expect(toolbar.getByRole('button', { name: 'Back to suppliers' })).toBeInViewport();
  await expect(toolbar.getByRole('button', { name: 'Save supplier', exact: true })).toBeInViewport();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  if (width === 1440) {
    // THEN enlarged contact controls stack when their available space cannot support comfortable columns.
    const contact = (await page.getByLabel('Contact name', { exact: true }).boundingBox())!;
    const email = (await page.getByLabel('Email', { exact: true }).boundingBox())!;
    expect(email.x).toBeCloseTo(contact.x, 0);
    expect(email.y).toBeGreaterThan(contact.y);
  }
});
