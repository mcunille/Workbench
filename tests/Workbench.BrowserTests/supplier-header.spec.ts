import { expect, test } from '@playwright/test';
import { useAuthenticatedSession as signIn } from './auth-fixture';

for (const width of [320, 1440]) test(`supplier header saves and keeps record context at ${width}px`, async ({ page }) => {
  // GIVEN a new supplier in the standalone editor.
  await signIn(page);
  await page.setViewportSize({ width, height: 720 });
  await page.goto('/suppliers/new');
  await expect(page.getByRole('heading', { name: 'New supplier', exact: true })).toBeVisible();
  const toolbar = page.locator('.po-editor-toolbar');
  const save = toolbar.getByRole('button', { name: 'Save supplier', exact: true });
  await expect(save).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save supplier', exact: true })).toHaveCount(1);
  // AND the supplier uses the application's short-form width rather than the wider purchase-order canvas.
  expect((await page.getByRole('region', { name: 'Supplier editor' }).boundingBox())!.width).toBeLessThanOrEqual(768);
  const name = `供應商 💎 مورد ${'A'.repeat(150)} ${width} ${Date.now()}`;
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
