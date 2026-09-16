import { test, expect } from '@playwright/test';
import { useAuthenticatedSession } from './auth-fixture';

test('itemized pieces, weight and batch pricing persist with explainable draft estimates', async ({ page }) => {
  // GIVEN a business owner planning stones priced by weight and settings priced per hundred.
  await useAuthenticatedSession(page);
  await page.goto('/purchase-orders/new');
  await page.getByLabel('Title', { exact: true }).fill(`Itemized purchase ${Date.now()}`);
  await page.getByLabel('Currency', { exact: true }).fill('USD');
  await page.getByRole('button', { name: 'Add entry', exact: true }).click();
  await page.getByLabel('Description 1', { exact: true }).fill('Ten sapphires');
  await page.getByLabel('Quantity 1', { exact: true }).fill('10');
  await page.getByLabel('Unit 1', { exact: true }).selectOption('piece');
  await page.getByLabel('Pricing unit 1', { exact: true }).selectOption('carat');
  await page.getByLabel('Total quantity priced 1', { exact: true }).fill('12.5');
  await page.getByLabel('Unit price 1', { exact: true }).fill('2000');
  await page.getByText('Line details', { exact: true }).click();
  await page.getByLabel('Supplier SKU 1', { exact: true }).fill('SAP-10');
  await page.getByLabel('Item type 1', { exact: true }).fill('Gemstone');
  await page.getByRole('button', { name: 'Add entry', exact: true }).click();
  await page.getByLabel('Description 2', { exact: true }).fill('Settings');
  await page.getByLabel('Quantity 2', { exact: true }).fill('250');
  await page.getByLabel('Unit 2', { exact: true }).selectOption('piece');
  await page.getByLabel('Per quantity 2', { exact: true }).fill('100');
  await page.getByLabel('Unit price 2', { exact: true }).fill('800');
  // THEN the subtotal is 250 for stones plus 20 for settings, without inventing a count from weight.
  await expect(page.getByRole('heading', { name: 'Merchandise estimate' })).toBeVisible();
  await expect(page.locator('.po-estimate-value')).toHaveText('USD 270.00');
  await expect(page.locator('.po-line-estimate').first()).toContainText('Ordered 10 piece');
  // WHEN saving and reopening THEN quantities, bases and metadata survive independently.
  const saved = page.waitForResponse(response => /\/api\/v3\/purchase-order-drafts(?:\/[a-f0-9-]+)?$/.test(response.url()) && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  expect((await saved).ok()).toBeTruthy();
  await expect(page).toHaveURL(/\/purchase-orders\/[a-f0-9-]{36}$/);
  await expect(page.getByLabel('Title', { exact: true })).toBeEnabled();
  await page.reload();
  await expect(page.getByLabel('Quantity 1', { exact: true })).toHaveValue('10.0000');
  await expect(page.getByLabel('Total quantity priced 1', { exact: true })).toHaveValue('12.5000');
  await expect(page.getByLabel('Supplier SKU 1', { exact: true })).toHaveValue('SAP-10');
  await expect(page.locator('.po-estimate-value')).toHaveText('USD 270.00');
  // WHEN an incomplete line is added THEN the known subtotal is explicit, never a complete total.
  await page.getByRole('button', { name: 'Add entry', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Known line subtotal' })).toBeVisible();
  await expect(page.locator('.po-merchandise-estimate')).toContainText('1 lines need quantity or pricing details');
  // AND enlarged text on a narrow screen retains readable controls without horizontal scrolling.
  await page.setViewportSize({ width: 320, height: 900 });
  await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});
